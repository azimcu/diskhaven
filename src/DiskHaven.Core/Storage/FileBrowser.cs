namespace DiskHaven.Core.Storage;

/// <summary>Read-only access to folders and files of a vault.</summary>
public sealed class FileBrowser(VaultRoot root, FolderSizeIndex? sizes = null)
{
    public VaultRoot Root { get; } = root;

    /// <summary>Lists a folder: folders first, then files, each sorted by name like Windows Explorer.</summary>
    public IReadOnlyList<FileEntry> List(string? relativePath)
    {
        var full = Root.Resolve(relativePath);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("Folder not found.");

        var entries = new List<FileEntry>();
        foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos())
        {
            if (info.LinkTarget is not null)
                continue;
            if (VaultRoot.IsInternalName(info.Name) && full == Root.FullPath)
                continue;
            entries.Add(ToEntry(info));
        }
        return entries
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Name, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering))
            .ToList();
    }

    /// <summary>
    /// Finds files and folders under a folder whose name contains every word of the query.
    /// Matching ignores case and Turkish accents ("isik" finds "Işık"). Stops after <paramref name="max"/> hits.
    /// </summary>
    public SearchResult Search(string? relativePath, string query, int max = 500, CancellationToken ct = default)
    {
        var words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return new SearchResult([], false);
        var full = Root.Resolve(relativePath);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("Folder not found.");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false,
        };
        var hits = new List<FileEntry>();
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> items;
            try { items = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList(); }
            catch (IOException) { continue; }
            foreach (var info in items)
            {
                if (dir == Root.FullPath && VaultRoot.IsInternalName(info.Name))
                    continue;
                if (info is DirectoryInfo)
                    pending.Push(info.FullName);
                var name = Fold(info.Name);
                if (!words.All(name.Contains))
                    continue;
                hits.Add(ToEntry(info));
                if (hits.Count >= max)
                    return new SearchResult(Sorted(hits), true);
            }
        }
        return new SearchResult(Sorted(hits), false);
    }

    private static List<FileEntry> Sorted(List<FileEntry> hits) =>
        hits.OrderBy(e => e.Kind).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Lower case without Turkish/Latin accents, so a search typed on any keyboard matches.</summary>
    internal static string Fold(string s)
    {
        var lower = s.ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));
        var sb = new System.Text.StringBuilder(lower.Length);
        foreach (var c in lower.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(c switch { 'ı' => 'i', '_' or '-' or '.' => ' ', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>Returns the full path of an existing file for download.</summary>
    public string GetFile(string? relativePath)
    {
        var full = Root.Resolve(relativePath);
        if (!File.Exists(full))
            throw new FileNotFoundException("File not found.");
        return full;
    }

    private FileEntry ToEntry(FileSystemInfo info)
    {
        var isDir = info is DirectoryInfo;
        var rel = Root.ToRelative(info.FullName);
        var stats = isDir ? sizes?.Get(rel) : null;
        return new FileEntry(
            info.Name,
            rel,
            isDir ? EntryKind.Folder : EntryKind.File,
            isDir ? stats?.Size : ((FileInfo)info).Length,
            info.LastWriteTimeUtc,
            isDir ? null : NullIfEmpty(info.Extension.TrimStart('.').ToLowerInvariant()),
            info.Name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden),
            stats?.Files,
            stats?.Folders);
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
