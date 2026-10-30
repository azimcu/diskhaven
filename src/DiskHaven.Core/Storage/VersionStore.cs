using System.Security.Cryptography;
using System.Text;

namespace DiskHaven.Core.Storage;

/// <summary>One earlier state of a file, kept when the file was replaced.</summary>
public sealed record FileVersion(string Id, DateTimeOffset SavedAt, long Size);

/// <summary>
/// Previous versions of files ("Previous versions" in Windows). When an upload replaces a file, the old file is
/// moved (one rename, same disk) into .diskhaven/versions/&lt;key&gt;/, where the key is derived from the file's
/// path. Each folder also holds path.txt so a person browsing the disk can tell which file the versions belong to.
/// Versions are plain copies of the old file: nothing is converted, any of them can be copied out by hand.
/// </summary>
public sealed class VersionStore(VaultRoot root, TimeProvider? clock = null)
{
    public const int MaxPerFile = 20;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private string Dir => Path.Combine(root.FullPath, VaultRoot.InternalFolderName, "versions");

    /// <summary>Moves the current file at <paramref name="path"/> into its version history.</summary>
    public FileVersion Keep(string path)
    {
        var rel = VaultRoot.Normalize(path);
        var full = root.Resolve(rel);
        if (!File.Exists(full))
            throw new FileNotFoundException("File not found.");
        lock (_lock)
        {
            var folder = FolderOf(rel);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "path.txt"), rel);
            var at = _clock.GetUtcNow();
            var id = $"{at.UtcTicks:D19}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
            var size = new FileInfo(full).Length;
            File.Move(full, Path.Combine(folder, id));
            foreach (var old in Ids(folder).Skip(MaxPerFile))
                File.Delete(Path.Combine(folder, old));
            return new FileVersion(id, at, size);
        }
    }

    /// <summary>Versions of a file, newest first.</summary>
    public IReadOnlyList<FileVersion> List(string path)
    {
        var folder = FolderOf(VaultRoot.Normalize(path));
        return Ids(folder).Select(id => new FileVersion(id, AtOf(id), new FileInfo(Path.Combine(folder, id)).Length)).ToList();
    }

    /// <summary>Full path of a stored version, for reading it.</summary>
    public string Get(string path, string id)
    {
        var file = Path.Combine(FolderOf(VaultRoot.Normalize(path)), ValidId(id));
        return File.Exists(file) ? file : throw new FileNotFoundException("Version not found.");
    }

    /// <summary>Puts a version back. The file that is there now becomes a version itself, so nothing is lost.</summary>
    public void Restore(string path, string id)
    {
        var rel = VaultRoot.Normalize(path);
        var stored = Get(rel, id);
        var full = root.Resolve(rel);
        if (Directory.Exists(full))
            throw new VaultConflictException("A folder now has this name.");
        if (File.Exists(full))
            Keep(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        root.Resolve(rel);   // re-check after creating parents: no symlink on the way
        File.Move(stored, full);
    }

    /// <summary>Removes versions older than <paramref name="age"/>. Returns how many were removed.</summary>
    public int PurgeOlderThan(TimeSpan age)
    {
        if (!Directory.Exists(Dir))
            return 0;
        var limit = _clock.GetUtcNow() - age;
        var removed = 0;
        lock (_lock)
        {
            foreach (var folder in Directory.EnumerateDirectories(Dir))
            {
                foreach (var id in Ids(folder).Where(id => AtOf(id) < limit))
                {
                    File.Delete(Path.Combine(folder, id));
                    removed++;
                }
                if (!Ids(folder).Any())
                    Directory.Delete(folder, recursive: true);
            }
        }
        return removed;
    }

    private string FolderOf(string rel) =>
        Path.Combine(Dir, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rel.ToLowerInvariant())))[..32]);

    private static IEnumerable<string> Ids(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>().Where(IsId).OrderDescending(StringComparer.Ordinal)
            : [];

    private static bool IsId(string name) =>
        name.Length == 26 && name[19] == '-' && name[..19].All(char.IsAsciiDigit) && name[20..].All(char.IsAsciiHexDigitLower);

    private static string ValidId(string id) => IsId(id) ? id : throw new FileNotFoundException("Version not found.");

    private static DateTimeOffset AtOf(string id) => new(long.Parse(id[..19]), TimeSpan.Zero);
}
