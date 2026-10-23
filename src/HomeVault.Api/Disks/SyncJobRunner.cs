using HomeVault.Core.Storage;
using HomeVault.Core.Sync;

namespace HomeVault.Api.Disks;

public enum JobStatus { Running, Done, Failed, Canceled }

public sealed record SyncJob(
    string Id, string FsId, string DiskName, bool Scheduled, DateTimeOffset Started,
    JobStatus Status, string Phase, SyncProgress? Progress, SyncReport? Report, string? Error,
    SyncPlanSummary? Plan);

public sealed record SyncPlanSummary(int CreateFolders, int Copy, int Update, int Delete, long Bytes, int Skipped);

/// <summary>Runs one sync at a time in the background and keeps its live state for the UI.</summary>
public sealed class SyncJobRunner(DiskService disks, DiskSettingsStore settings, VaultRoot vault, TimeProvider clock,
    ILogger<SyncJobRunner> log)
{
    private readonly Lock _lock = new();
    private SyncJob? _job;
    private CancellationTokenSource? _cts;

    public SyncJob? Current { get { lock (_lock) return _job; } }

    public static SyncPlanSummary Summarize(SyncPlan p) => new(
        p.Count(SyncActionKind.CreateFolder), p.Count(SyncActionKind.Copy), p.Count(SyncActionKind.Update),
        p.Count(SyncActionKind.Delete) + p.Count(SyncActionKind.DeleteFolder), p.BytesToCopy, p.Skipped.Count);

    public SyncJob Start(string fsId, bool scheduled = false)
    {
        var s = settings.Get(fsId) ?? throw new VaultConflictException("Disk is not adopted.");
        if (s.Folders.Count == 0)
            throw new VaultConflictException("No folders are assigned to this disk.");
        lock (_lock)
        {
            if (_job?.Status == JobStatus.Running)
                throw new VaultConflictException("A sync is already running.");
            _cts = new CancellationTokenSource();
            _job = new SyncJob(Guid.NewGuid().ToString("N"), fsId, s.Name, scheduled, clock.GetUtcNow(),
                JobStatus.Running, "prepare", null, null, null, null);
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(s, token));
            return _job;
        }
    }

    public void Cancel()
    {
        lock (_lock) _cts?.Cancel();
    }

    private void Set(Func<SyncJob, SyncJob> change)
    {
        lock (_lock) _job = change(_job!);
    }

    private async Task RunAsync(DiskSettings s, CancellationToken ct)
    {
        try
        {
            var root = await disks.EnsureMountedAsync(s.FsId, ct);
            var marker = DiskMarker.Read(root);
            if (marker?.DiskId != s.DiskId)
                throw new InvalidOperationException("Bu disk, ayarlardaki disk değil (kimlik dosyası tutmuyor). Güvenlik için eşitleme yapılmadı.");

            Set(j => j with { Phase = "plan" });
            var plan = await Task.Run(() => SyncPlanner.Plan(vault.FullPath, s.Folders, root, ct), ct);
            var summary = Summarize(plan);
            Set(j => j with { Plan = summary });

            var free = new DriveInfo(root).AvailableFreeSpace;
            const long reserve = 100L << 20;
            if (plan.BytesToCopy > free - reserve)
                throw new InvalidOperationException(
                    $"Diskte yer yetmiyor: {Gb(plan.BytesToCopy)} yazılacak, {Gb(free)} boş. Atanan klasörleri azalt ya da diskteki .homevault-trash klasörünü boşalt.");

            Set(j => j with { Phase = "copy" });
            var progress = new Progress<SyncProgress>(p => Set(j => j with { Progress = p }));
            var report = await new SyncExecutor(clock).ExecuteAsync(plan, vault.FullPath, root, progress, ct);
            settings.AddReport(s.FsId, report);
            var message = report.Canceled ? "İptal edildi" : report.Errors.Count > 0 ? $"{report.Errors.Count} dosyada hata" : null;
            settings.Update(s.FsId, x => x with
            {
                LastSync = new SyncSummary(report.Finished, report.Success, report.Canceled, report.Copied, report.Updated,
                    report.Deleted, report.Verified, report.Errors.Count, report.BytesCopied, message),
            });

            Set(j => j with { Phase = "finish" });
            if (!report.Canceled)
                await disks.FinishAsync(s.FsId, s.After, CancellationToken.None);
            Set(j => j with { Status = report.Canceled ? JobStatus.Canceled : report.Success ? JobStatus.Done : JobStatus.Failed, Report = report, Phase = "done" });
            log.LogInformation("Sync to {Disk}: {Copied} copied, {Updated} updated, {Deleted} deleted, {Errors} errors",
                s.Name, report.Copied, report.Updated, report.Deleted, report.Errors.Count);
        }
        catch (OperationCanceledException)
        {
            Set(j => j with { Status = JobStatus.Canceled, Phase = "done" });
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Sync to {Disk} failed", s.Name);
            settings.Update(s.FsId, x => x with
            {
                LastSync = new SyncSummary(clock.GetUtcNow(), false, false, 0, 0, 0, 0, 0, 0, e.Message),
            });
            Set(j => j with { Status = JobStatus.Failed, Error = e.Message, Phase = "done" });
        }
    }

    private static string Gb(long bytes) => $"{bytes / 1073741824.0:0.##} GB";
}

/// <summary>Starts scheduled syncs: once a day, at the disk's configured time (server local time).</summary>
public sealed class SyncScheduler(DiskSettingsStore settings, SyncJobRunner runner, TimeProvider clock, ILogger<SyncScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = clock.GetLocalNow();
            var today = DateOnly.FromDateTime(now.DateTime);
            foreach (var s in settings.All())
            {
                if (!s.Schedule.Enabled || s.Folders.Count == 0 || s.Schedule.LastRun == today)
                    continue;
                if (!TimeOnly.TryParse(s.Schedule.Time, out var at) || TimeOnly.FromDateTime(now.DateTime) < at)
                    continue;
                if (runner.Current?.Status == JobStatus.Running)
                    break;
                settings.Update(s.FsId, x => x with { Schedule = x.Schedule with { LastRun = today } });
                try
                {
                    runner.Start(s.FsId, scheduled: true);
                    log.LogInformation("Scheduled sync started for {Disk}", s.Name);
                }
                catch (Exception e) { log.LogWarning(e, "Scheduled sync for {Disk} could not start", s.Name); }
                break; // one at a time; the next disk is picked up on a later tick
            }
        }
    }
}
