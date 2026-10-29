namespace DiskHaven.Core.Storage;

/// <summary>
/// File name rules. Disks must stay readable on Windows, so names Windows cannot
/// store (reserved characters, CON/NUL..., trailing dot or space) are rejected.
/// </summary>
public static class FileNames
{
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public const int MaxLength = 255;

    /// <summary>Returns the trimmed name or throws <see cref="VaultPathException"/> with a user-facing reason.</summary>
    public static string Validate(string? name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0)
            throw new VaultPathException("Name is empty.");
        if (n.Length > MaxLength)
            throw new VaultPathException($"Name is longer than {MaxLength} characters.");
        if (n is "." or ".." || n.EndsWith('.'))
            throw new VaultPathException("Name must not end with a dot.");
        if (n.IndexOfAny(Invalid) >= 0 || n.Any(char.IsControl))
            throw new VaultPathException("Name must not contain < > : \" / \\ | ? *");
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(n)))
            throw new VaultPathException("This name is reserved by Windows.");
        if (n.Equals(VaultRoot.InternalFolderName, StringComparison.OrdinalIgnoreCase))
            throw new VaultPathException("Name is reserved.");
        return n;
    }

    /// <summary>
    /// Returns <paramref name="name"/> if nothing with that name exists in <paramref name="folder"/>,
    /// otherwise "name (2).ext", "name (3).ext"... Comparison ignores case because NTFS and exFAT do.
    /// </summary>
    public static string Unique(string folder, string name)
    {
        if (!Exists(folder, name))
            return name;
        var isDirLike = !Path.HasExtension(name) || Directory.Exists(Path.Combine(folder, name));
        var stem = isDirLike ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDirLike ? "" : Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!Exists(folder, candidate))
                return candidate;
        }
    }

    /// <summary>Case-insensitive existence check, matching how the target disks behave.</summary>
    public static bool Exists(string folder, string name) =>
        Directory.Exists(folder) &&
        Directory.EnumerateFileSystemEntries(folder)
            .Any(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase));
}
