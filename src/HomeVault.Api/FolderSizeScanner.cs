using System.Diagnostics;
using HomeVault.Core.Storage;

namespace HomeVault.Api;

/// <summary>
/// Full folder-size scan at start-up and every 30 minutes, to include changes made outside
/// HomeVault (SMB, copying onto the disk directly). HomeVault's own changes are applied instantly.
/// </summary>
public sealed class FolderSizeScanner(FolderSizeIndex index, ILogger<FolderSizeScanner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                await Task.Run(() => index.Scan(ct), ct);
                log.LogInformation("Folder sizes scanned in {Ms} ms", sw.ElapsedMilliseconds);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Folder size scan failed");
            }
            // a change during the scan leaves the index "not ready": scan again shortly
            await Task.Delay(index.Ready ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(10), ct);
        }
    }
}
