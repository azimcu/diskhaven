namespace HomeVault.Core.Sync;

public enum SyncActionKind { CreateFolder, Copy, Update, Delete, DeleteFolder, FixCase }

/// <summary>One step of a sync. Paths are relative to both roots and use '/'.</summary>
public sealed record SyncAction(SyncActionKind Kind, string Path, long Size = 0, string? TargetPath = null);

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
    bool Canceled)
{
    public bool Success => !Canceled && Errors.Count == 0;
}
