namespace DiskHaven.Core.Sync;

/// <summary>
/// One backup job: chosen folders of a source drive are mirrored into a folder of a target drive.
/// One-way only: the source is never changed, and anything the mirror removes on the target goes to
/// the target's own recycle bin (30 days). A disk can hold more than one job.
/// Drive ids are "" for the main storage or a disk's filesystem id.
/// </summary>
public sealed record BackupJob(
    string Id,
    string Name,
    string SourceDrive,
    IReadOnlyList<string> SourceFolders,
    string TargetDrive,
    string TargetFolder,
    bool Enabled,
    string Time,
    int DeleteThresholdPercent,
    DateTimeOffset CreatedAt,
    BackupSummary? LastRun = null,
    DateTimeOffset? LastSuccessAt = null,
    bool Legacy = false,
    bool AdoptExisting = false)
{
    /// <summary>Paths are compared the way the disks compare names.</summary>
    public bool SameTarget(BackupJob other) =>
        string.Equals(TargetDrive, other.TargetDrive, StringComparison.Ordinal) &&
        string.Equals(TargetFolder, other.TargetFolder, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What the last run did. Enough for the card on the Disks page; the full report is kept in the history.</summary>
public sealed record BackupSummary(
    DateTimeOffset Finished,
    bool Success,
    bool Canceled,
    bool AwaitingApproval,
    int Copied,
    int Updated,
    int Deleted,
    int Skipped,
    int Errors,
    long BytesCopied,
    long DurationSeconds,
    string? Message);
