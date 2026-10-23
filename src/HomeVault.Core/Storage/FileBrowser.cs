namespace HomeVault.Core.Storage;

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
            if (info.Name.Equals(VaultRoot.InternalFolderName, StringComparison.OrdinalIgnoreCase) && full == Root.FullPath)
                continue;
            entries.Add(ToEntry(info));
        }
        return entries
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Name, StringComparer.Create(System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering))
            .ToList();
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
