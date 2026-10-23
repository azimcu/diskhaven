namespace HomeVault.Core.Sync;

public enum SyncActionKind { CreateFolder, Copy, Update, Delete, DeleteFolder, FixCase, Move }

/// <summary>
/// One step of a sync. Paths are relative to both roots and use '/'.
/// For <see cref="SyncActionKind.Move"/>, <c>Path</c> is the new place and <c>TargetPath</c> the old place on the backup disk.
/// <c>MtimeUtc</c> is the modification time of the file the action is about (used to detect moves).
/// </summary>
public sealed record SyncAction(SyncActionKind Kind, string Path, long Size = 0, string? TargetPath = null, DateTime MtimeUtc = default);

public sealed record SyncPlan(
    IReadOnlyList<string> Folders,
    IReadOnlyList<SyncAction> Actions,
    IReadOnlyList<string> Skipped,
    DateTimeOffset CreatedAt)
{
    public int Count(SyncActionKind kind) => Actions.Count(a => a.Kind == kind);
    /// <summary>Bytes that will be written to the target.</summary>
    public long BytesToCopy => Actions.Where(a => a.Kind is SyncActionKind.Copy or SyncActionKind.Update).Sum(a => a.Size);
    public bool IsEmpty => Actions.Count == 0;
}

public sealed record SyncProgress(string Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string? Current);

public sealed record SyncError(string Path, string Message);

public sealed record SyncReport(
    DateTimeOffset Started,
    DateTimeOffset Finished,
    int Created,
    int Copied,
    int Updated,
    int Deleted,
    int Verified,
    long BytesCopied,
    IReadOnlyList<SyncError> Errors,
    IReadOnlyList<string> Skipped,
    bool Canceled,
    int Moved = 0)
{
    public bool Success => !Canceled && Errors.Count == 0;
}
