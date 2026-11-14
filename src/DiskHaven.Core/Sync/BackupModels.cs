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
    /// <summary>
    /// The folders this job writes into on the target: each source folder keeps its path below
    /// <see cref="TargetFolder"/> (TargetFolder/Belgeler), and the mirror only adds or trashes inside those.
    /// </summary>
    public IEnumerable<string> MirrorFolders() =>
        SourceFolders.Select(f => string.Join('/', new[] { TargetFolder, f }.Where(x => x.Length > 0)).Trim('/'));

    /// <summary>
    /// True when the two jobs would write into the same folder, or one into a folder inside the other's.
    /// Such jobs would keep trashing each other's files on every run. Names compare case-insensitively,
    /// as exFAT, NTFS and FAT do.
    /// </summary>
    public bool SameTarget(BackupJob other)
    {
        if (!string.Equals(TargetDrive, other.TargetDrive, StringComparison.Ordinal))
            return false;
        static bool Inside(string a, string b) =>
            a.Length == 0 || b.Length == 0 || string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase);
        return string.Equals(TargetFolder, other.TargetFolder, StringComparison.OrdinalIgnoreCase) ||
               MirrorFolders().Any(a => other.MirrorFolders().Any(b => Inside(a, b)));
    }
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
