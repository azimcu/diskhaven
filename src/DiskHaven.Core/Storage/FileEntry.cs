namespace DiskHaven.Core.Storage;

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
    int? Folders = null,
    string? Problem = null);

/// <summary>Why an item is listed but cannot be opened (<see cref="FileEntry.Problem"/>).</summary>
public static class EntryProblem
{
    /// <summary>The name is not valid UTF-8 (old Windows/Linux systems); the item cannot be reached by that name.</summary>
    public const string BadName = "bad-name";
    /// <summary>A named pipe, socket or device: no data.</summary>
    public const string Special = "special";
}

/// <summary>Search hits; <c>Truncated</c> is true when the hit limit was reached.</summary>
public sealed record SearchResult(IReadOnlyList<FileEntry> Entries, bool Truncated);

/// <summary>Details for the Properties dialog.</summary>
public sealed record ItemProperties(FileEntry Entry, DateTimeOffset? Created, DateTimeOffset Accessed, bool ReadOnly,
    long? SizeOnDisk, long? DiskFree);
