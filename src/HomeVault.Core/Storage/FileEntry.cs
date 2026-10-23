namespace HomeVault.Core.Storage;

public enum EntryKind { Folder, File }

/// <summary>One row of a folder listing, as the file explorer shows it.</summary>
public sealed record FileEntry(
    string Name,
    string Path,
    EntryKind Kind,
    long? Size,
    DateTimeOffset Modified,
    string? Extension,
    bool Hidden);
