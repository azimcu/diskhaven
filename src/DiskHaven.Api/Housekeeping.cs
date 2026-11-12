using DiskHaven.Api.Disks;
using DiskHaven.Core.Storage;
using tusdotnet.Stores;

namespace DiskHaven.Api;

/// <summary>
/// Hourly cleanup: recycle bin items and previous versions older than 30 days, abandoned uploads. The recycle bin of
/// each writable disk that is mounted is cleaned the same way, with the temporary files of copies that were cut off;
/// read-only disks are never touched.
/// </summary>
public sealed class Housekeeping(FileOperations ops, VersionStore versions, TusDiskStore uploads, Drives drives, TimeProvider clock,
    ILogger<Housekeeping> log) : BackgroundService
{
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    /// <summary>A temporary copy file nobody wrote to for this long belongs to a copy that was cut off.</summary>
    public static readonly TimeSpan StaleTemp = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var purged = ops.PurgeOlderThan(TrashRetention) + versions.PurgeOlderThan(TrashRetention);
                foreach (var (disk, root) in drives.Mounted())
                {
                    if (root.ReadOnly)
                        continue;
                    try
                    {
                        purged += new FileOperations(root, clock).PurgeOlderThan(TrashRetention);
                        purged += VerifiedCopy.CleanStale(Path.Combine(root.FullPath, VaultRoot.InternalFolderName, "tmp"), StaleTemp, clock.GetUtcNow().UtcDateTime);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { log.LogWarning(e, "Recycle bin cleanup on disk {Disk} failed", disk.Name); }
                }
                var expired = await uploads.RemoveExpiredFilesAsync(ct);
                if (purged + expired > 0)
                    log.LogInformation("Housekeeping: {Purged} recycle bin items and versions, {Expired} abandoned uploads removed", purged, expired);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Housekeeping failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
