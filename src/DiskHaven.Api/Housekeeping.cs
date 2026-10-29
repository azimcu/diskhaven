using DiskHaven.Core.Storage;
using tusdotnet.Stores;

namespace DiskHaven.Api;

/// <summary>Hourly cleanup: recycle bin items older than 30 days, abandoned uploads.</summary>
public sealed class Housekeeping(FileOperations ops, TusDiskStore uploads, ILogger<Housekeeping> log) : BackgroundService
{
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var purged = ops.PurgeOlderThan(TrashRetention);
                var expired = await uploads.RemoveExpiredFilesAsync(ct);
                if (purged + expired > 0)
                    log.LogInformation("Housekeeping: {Purged} recycle bin items, {Expired} abandoned uploads removed", purged, expired);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Housekeeping failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }
}
