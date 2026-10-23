namespace HomeVault.Core.Storage;

public enum EntryKind { Folder, File }

/// <summary>One row of a folder listing, as the file explorer shows it. For folders, Size/Files/Folders
/// are the recursive totals when the size index has them, otherwise null.</summary>
public sealed record FileEntry(
    string Name,
    string Path,
    EntryKind Kind,
    long? Size,
    DateTimeOffset Modified,
    string? Extension,
    bool Hidden,
    int? Files = null,
    int? Folders = null);
