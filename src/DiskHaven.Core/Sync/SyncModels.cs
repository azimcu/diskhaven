namespace DiskHaven.Core.Sync;

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
    DateTimeOffset CreatedAt,
    int SourceFiles = 0,
    int TargetFiles = 0,
    int DeletedFiles = 0)
{
    public int Count(SyncActionKind kind) => Actions.Count(a => a.Kind == kind);
    /// <summary>Bytes that will be written to the target.</summary>
    public long BytesToCopy => Actions.Where(a => a.Kind is SyncActionKind.Copy or SyncActionKind.Update).Sum(a => a.Size);
    public bool IsEmpty => Actions.Count == 0;

    /// <summary>Delete actions: a folder is one, whatever it holds. See <see cref="DeletedFiles"/> for the real amount.</summary>
    public int Deletes => Count(SyncActionKind.Delete) + Count(SyncActionKind.DeleteFolder);

    /// <summary>No file in the source: the target must not be emptied (an unplugged disk looks empty too).</summary>
    public bool SourceEmpty => SourceFiles == 0;

    /// <summary>True when the plan would remove more of the target than the owner allowed without asking.</summary>
    public bool NeedsApproval(int thresholdPercent) =>
        !SourceEmpty && TargetFiles > 0 && DeletedFiles > 0 && DeletedFiles * 100L > (long)TargetFiles * thresholdPercent;

    /// <summary>The same plan without any delete: used when the source looks empty or not connected.</summary>
    public SyncPlan WithoutDeletes(string reason)
    {
        var skipped = Skipped.ToList();
        skipped.Add(reason);
        return this with
        {
            Actions = Actions.Where(a => a.Kind is not (SyncActionKind.Delete or SyncActionKind.DeleteFolder)).ToList(),
            Skipped = skipped,
            DeletedFiles = 0,
        };
    }
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
    int Moved = 0,
    IReadOnlyList<string>? Renamed = null)
{
    public bool Success => !Canceled && Errors.Count == 0;
    public TimeSpan Duration => Finished - Started;
}
