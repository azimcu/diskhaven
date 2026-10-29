namespace DiskHaven.Core.Storage;

/// <summary>
/// A directory on disk that DiskHaven serves. Files stay plain files in plain folders,
/// so the disk is readable without DiskHaven. All client paths are relative to this root
/// and use '/' as separator.
/// </summary>
public sealed class VaultRoot
{
    /// <summary>Folder DiskHaven keeps its own data in (temp uploads, trash). Hidden from clients.</summary>
    public const string InternalFolderName = ".diskhaven";
    /// <summary>Internal folder name before the rename from HomeVault (2026-10); moved on start.</summary>
    public const string LegacyInternalFolderName = ".homevault";

    public string FullPath { get; }

    public VaultRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Vault root path is empty.", nameof(path));
        FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(FullPath))
            throw new DirectoryNotFoundException($"Vault root does not exist: {FullPath}");
        var legacy = Path.Combine(FullPath, LegacyInternalFolderName);
        if (Directory.Exists(legacy) && !Directory.Exists(Path.Combine(FullPath, InternalFolderName)))
            Directory.Move(legacy, Path.Combine(FullPath, InternalFolderName));
    }

    /// <summary>
    /// Normalizes a client path ("", "/", "Docs/a.txt", "\\Docs\\a.txt") to "Docs/a.txt" form.
    /// Rejects traversal, empty segments, control characters and the internal folder.
    /// </summary>
    public static string Normalize(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return "";
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new VaultPathException("Path must not contain '.' or '..' segments.");
            if (segment.Any(char.IsControl) || segment.Contains(':'))
                throw new VaultPathException("Path contains invalid characters.");
            if (segment.Equals(InternalFolderName, StringComparison.OrdinalIgnoreCase))
                throw new VaultPathException("Path is reserved.");
        }
        return string.Join('/', segments);
    }

    /// <summary>
    /// Maps a client path to a full path inside the root. Every existing component is checked
    /// so that a symbolic link cannot lead outside the vault.
    /// </summary>
    public string Resolve(string? relativePath)
    {
        var normalized = Normalize(relativePath);
        var current = FullPath;
        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            if (info.Exists || Directory.Exists(current))
            {
                if (info.LinkTarget is not null)
                    throw new VaultPathException("Symbolic links are not served.");
            }
        }
        var full = Path.GetFullPath(current);
        if (full != FullPath && !full.StartsWith(FullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new VaultPathException("Path is outside the vault.");
        return full;
    }

    /// <summary>Converts a full path inside the root back to the client form.</summary>
    public string ToRelative(string fullPath) =>
        Path.GetRelativePath(FullPath, fullPath) is var rel && rel == "." ? "" : rel.Replace(Path.DirectorySeparatorChar, '/');
}
