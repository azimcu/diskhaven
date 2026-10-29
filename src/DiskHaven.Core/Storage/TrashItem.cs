namespace DiskHaven.Core.Storage;

/// <summary>A deleted file or folder waiting in the recycle bin.</summary>
public sealed record TrashItem(
    string Id,
    string Name,
    string OriginalPath,
    EntryKind Kind,
    long? Size,
    DateTimeOffset DeletedAt);
