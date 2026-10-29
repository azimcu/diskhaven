using System.IO.Compression;

namespace DiskHaven.Core.Storage;

/// <summary>
/// ZIP like Windows Explorer: "Compress to ZIP file" next to the selection and "Extract all" into a new folder.
/// Archives are built in the temp folder and renamed into place, so a half-written ZIP never appears.
/// Extraction refuses entries that would land outside the target folder (zip slip) and checks free space first.
/// </summary>
public sealed class Archives(VaultRoot root, IVaultChangeListener? listener = null)
{
    private string TempDir => Path.Combine(root.FullPath, VaultRoot.InternalFolderName, "tmp");

    /// <summary>Creates "&lt;name&gt;.zip" in the folder of the first item. Returns its relative path.</summary>
    public string Compress(IReadOnlyList<string> paths, string? zipName = null, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            throw new VaultPathException("Nothing to compress.");
        var fulls = paths.Select(p => Existing(p)).ToList();
        var dir = Path.GetDirectoryName(fulls[0])!;
        if (fulls.Any(f => Path.GetDirectoryName(f) != dir))
            throw new VaultPathException("Items must be in the same folder.");
        var baseName = zipName is { Length: > 0 }
            ? Path.GetFileNameWithoutExtension(FileNames.Validate(zipName))
            : fulls.Count == 1 ? Path.GetFileNameWithoutExtension(fulls[0]) is { Length: > 0 } n ? n : Path.GetFileName(fulls[0]) : Path.GetFileName(dir) is { Length: > 0 } d ? d : "Arşiv";
        var target = Path.Combine(dir, FileNames.Unique(dir, baseName + ".zip"));

        Directory.CreateDirectory(TempDir);
        var tmp = Path.Combine(TempDir, $"zip-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: System.Text.Encoding.UTF8))
            {
                foreach (var full in fulls)
                {
                    if (File.Exists(full))
                        AddFile(zip, full, Path.GetFileName(full), ct);
                    else
                        AddFolder(zip, full, Path.GetFileName(full), ct);
                }
            }
            File.Move(tmp, target);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        listener?.Added(target);
        return root.ToRelative(target);
    }

    /// <summary>Writes a ZIP of the items to <paramref name="output"/> (for "download as ZIP"; nothing is stored).</summary>
    public void WriteTo(Stream output, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: System.Text.Encoding.UTF8);
        foreach (var full in paths.Select(p => Existing(p)))
        {
            if (File.Exists(full)) AddFile(zip, full, Path.GetFileName(full), ct, CompressionLevel.Fastest);
            else AddFolder(zip, full, Path.GetFileName(full), ct, CompressionLevel.Fastest);
        }
    }

    /// <summary>Extracts a ZIP into a new folder named after it, next to it. Returns the folder's relative path.</summary>
    public string Extract(string zipPath, long freeBytes, CancellationToken ct = default)
    {
        var full = Existing(zipPath);
        if (!File.Exists(full))
            throw new FileNotFoundException("File not found.");
        var dir = Path.GetDirectoryName(full)!;
        var folderName = FileNames.Unique(dir, Path.GetFileNameWithoutExtension(full) is { Length: > 0 } n ? n : "Arşiv");

        Directory.CreateDirectory(TempDir);
        var staging = Path.Combine(TempDir, $"unzip-{Guid.NewGuid():N}");
        try
        {
            using (var zip = OpenZip(full))
            {
                var total = zip.Entries.Sum(e => e.Length);
                if (total > freeBytes)
                    throw new IOException("Not enough free space to extract this archive.");
                Directory.CreateDirectory(staging);
                var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = entry.FullName.Replace('\\', '/');
                    var segments = rel.Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => FileNames.Validate(s)).ToArray();   // rejects "..", control characters, reserved names
                    if (segments.Length == 0)
                        continue;
                    var dest = Path.GetFullPath(Path.Combine(staging, Path.Combine(segments)));
                    if (!dest.StartsWith(stagingRoot, StringComparison.Ordinal))
                        throw new VaultPathException("Archive contains an unsafe path.");
                    if (rel.EndsWith('/'))
                    {
                        Directory.CreateDirectory(dest);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using (var src = entry.Open())
                    using (var outFile = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                        src.CopyTo(outFile);
                    File.SetLastWriteTimeUtc(dest, entry.LastWriteTime.UtcDateTime);
                }
            }
            var target = Path.Combine(dir, folderName);
            Directory.Move(staging, target);
            listener?.Added(target);
            return root.ToRelative(target);
        }
        catch (InvalidDataException) { throw new IOException("This is not a valid ZIP file."); }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    /// <summary>Entries of a ZIP for a quick look inside (name, size), at most <paramref name="max"/>.</summary>
    public IReadOnlyList<(string Name, long Size)> List(string zipPath, int max = 500)
    {
        using var zip = OpenZip(Existing(zipPath));
        return zip.Entries.Take(max).Select(e => (e.FullName, e.Length)).ToList();
    }

    private static ZipArchive OpenZip(string full)
    {
        try { return ZipFile.Open(full, ZipArchiveMode.Read, System.Text.Encoding.UTF8); }
        catch (InvalidDataException) { throw new IOException("This is not a valid ZIP file."); }
    }

    private string Existing(string path)
    {
        var full = root.Resolve(path);
        if (full == root.FullPath || (!File.Exists(full) && !Directory.Exists(full)))
            throw new FileNotFoundException("Item not found.");
        return full;
    }

    private static void AddFile(ZipArchive zip, string full, string name, CancellationToken ct, CompressionLevel level = CompressionLevel.Optimal)
    {
        ct.ThrowIfCancellationRequested();
        // already-compressed media is stored, not squeezed again: much faster, same size
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".mp4" or ".mkv" or ".mov" or ".mp3" or ".m4a"
            or ".zip" or ".7z" or ".rar" or ".gz" or ".xz" or ".docx" or ".xlsx" or ".pptx" or ".pdf")
            level = CompressionLevel.NoCompression;
        var entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = File.GetLastWriteTime(full);
        using var dst = entry.Open();
        using var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        src.CopyTo(dst);
    }

    private static void AddFolder(ZipArchive zip, string dir, string prefix, CancellationToken ct, CompressionLevel level = CompressionLevel.Optimal)
    {
        var any = false;
        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if (info.LinkTarget is not null)
                continue;
            any = true;
            if (info is FileInfo f) AddFile(zip, f.FullName, $"{prefix}/{f.Name}", ct, level);
            else AddFolder(zip, info.FullName, $"{prefix}/{info.Name}", ct, level);
        }
        if (!any)
            zip.CreateEntry(prefix + "/");
    }
}
