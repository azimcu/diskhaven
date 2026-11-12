using DiskHaven.Core.Storage;
using DiskHaven.Core.Sync;

namespace DiskHaven.Api.Disks;

public enum BackupRunStatus { Running, Done, Failed, Canceled, AwaitingApproval }

/// <summary>What a backup does before it writes anything: enough for the card in the UI.</summary>
public sealed record BackupPlanSummary(int CreateFolders, int Copy, int Update, int Delete, long Bytes, int Skipped, int Move = 0,
    bool SourceEmpty = false);

/// <summary>One run of a backup job, live while it runs.</summary>
public sealed record BackupRun(
    string Id, string JobId, string JobName, bool Scheduled, DateTimeOffset Started,
    BackupRunStatus Status, string Phase, SyncProgress? Progress, BackupPlanSummary? Plan,
    SyncReport? Report, string? Error, string? Message);

/// <summary>
/// Runs one backup at a time in the background. A backup is a one-way mirror: it only ever reads the source,
/// and anything it replaces or removes on the target first goes to that target disk's own recycle bin.
/// Before the first byte is written it checks the target disk's identity file, the job's marker in the target
/// folder, that the target is writable, and that the source is not empty; a plan that would remove more than
/// the job's allowed share of the target stops and waits for the owner's approval.
/// </summary>
public sealed class BackupJobRunner(BackupStore store, DiskService disks, DiskSettingsStore diskSettings, VaultRoot main,
    ActiveJobs active, TimeProvider clock, ILogger<BackupJobRunner> log, PluginStore plugins)
{
    /// <summary>Space that must stay free on the target after the backup.</summary>
    private const long Reserve = 256L << 20;

    private readonly Lock _lock = new();
    private BackupRun? _job;
    private CancellationTokenSource? _cts;

    public BackupRun? Current { get { lock (_lock) return _job; } }

    public bool IsRunning => Current is { Status: BackupRunStatus.Running };

    public static BackupPlanSummary Summarize(SyncPlan p) => new(
        p.Count(SyncActionKind.CreateFolder), p.Count(SyncActionKind.Copy), p.Count(SyncActionKind.Update),
        p.DeletedFiles, p.BytesToCopy, p.Skipped.Count,
        p.Count(SyncActionKind.Move), p.SourceEmpty);

    /// <summary>Starts a job now. <paramref name="approveDeletes"/> is the owner's answer to the 50% question.</summary>
    public BackupRun Start(string jobId, bool approveDeletes = false, bool scheduled = false)
    {
        var job = store.Get(jobId) ?? throw new KeyNotFoundException("Yedek işi bulunamadı.");
        Validate(job);
        lock (_lock)
        {
            if (_job is { Status: BackupRunStatus.Running })
                throw new VaultConflictException("Başka bir yedek işi çalışıyor. Bitmesini bekle.");
            _cts = new CancellationTokenSource();
            _job = new BackupRun(Guid.NewGuid().ToString("N"), job.Id, job.Name, scheduled, clock.GetUtcNow(),
                BackupRunStatus.Running, "prepare", null, null, null, null, null);
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(job, approveDeletes, token));
            return _job;
        }
    }

    public void Cancel()
    {
        lock (_lock) _cts?.Cancel();
    }

    /// <summary>Settings that must be right before anything is touched; the same checks for save and run.</summary>
    public static void Validate(BackupJob job)
    {
        if (string.IsNullOrWhiteSpace(job.Name))
            throw new VaultPathException("Yedeğe bir ad ver.");
        FileNames.Validate(job.Name);
        if (job.SourceFolders.Count == 0)
            throw new VaultPathException("Yedeklenecek en az bir klasör seç.");
        if (string.Equals(job.SourceDrive, job.TargetDrive, StringComparison.Ordinal))
            throw new VaultPathException("Kaynak ve hedef aynı disk olamaz.");
        if (job.Time.Length > 0 && !TimeOnly.TryParse(job.Time, out _))
            throw new VaultPathException("Saat geçersiz.");
        if (job.DeleteThresholdPercent is < 0 or > 100)
            throw new VaultPathException("Silme sınırı 0 ile 100 arasında olmalı.");
        VaultRoot.Normalize(job.TargetFolder);
    }

    private void Set(Func<BackupRun, BackupRun> change)
    {
        lock (_lock) _job = change(_job!);
    }

    private async Task RunAsync(BackupJob job, bool approveDeletes, CancellationToken ct)
    {
        IDisposable? lease = null;
        try
        {
            // one job per drive: no transfer, unmount or read-only change may run underneath it
            lease = active.Lease(job.Name, job.SourceDrive, job.TargetDrive);

            var source = await OpenSourceAsync(job, ct);
            var (targetDiskRoot, targetSettings) = await OpenTargetAsync(job, ct);
            var targetFolder = VaultRoot.Normalize(job.TargetFolder);
            var targetBase = targetDiskRoot.Resolve(targetFolder);
            // ownership is checked before anything is planned or written: another job's folder is never touched
            if (Directory.Exists(targetBase))
                CheckMarker(job, targetBase);

            Set(j => j with { Phase = "plan" });
            var plan = await Task.Run(() => SyncPlanner.Plan(source.FullPath, job.SourceFolders, targetBase, ct), ct);
            Set(j => j with { Plan = Summarize(plan) });

            if (plan.SourceEmpty)
            {
                plan = plan.WithoutDeletes("Kaynak boş görünüyor (ya da klasörler bulunamadı); hedefte hiçbir şey silinmedi.");
                Set(j => j with { Plan = Summarize(plan) });
            }

            if (!approveDeletes && plan.NeedsApproval(job.DeleteThresholdPercent))
            {
                var message = $"Hedefteki dosyaların {Percent(plan.DeletedFiles, plan.TargetFiles)} kadarı silinecekti " +
                              $"(izin verilen en fazla %{job.DeleteThresholdPercent}). Hiçbir şey değiştirilmedi. " +
                              "Onaylarsan bu temizlik yapılır.";
                Set(j => j with { Status = BackupRunStatus.AwaitingApproval, Phase = "approve", Message = message });
                SaveSummary(job, new BackupSummary(clock.GetUtcNow(), false, false, true, 0, 0, 0,
                    plan.Skipped.Count, 0, 0, 0, message));
                log.LogInformation("Backup {Job} awaits approval: {Deletes} of {Target} target items would be removed",
                    job.Name, plan.DeletedFiles, plan.TargetFiles);
                return;
            }

            var free = new DriveInfo(targetDiskRoot.FullPath).AvailableFreeSpace;
            if (plan.BytesToCopy > free - Reserve)
                throw new IOException($"Hedef diskte yer yetmiyor: {Gb(plan.BytesToCopy)} yazılacak, {Gb(free)} boş. Hiçbir şey silinmedi.");

            if (!Directory.Exists(targetBase))
                Directory.CreateDirectory(targetBase);
            WriteMarker(job, targetBase);

            Set(j => j with { Phase = "copy" });
            var progress = new Progress<SyncProgress>(p => Set(j => j with { Progress = p }));
            var report = await new SyncExecutor(clock).ExecuteAsync(plan, source.FullPath, targetDiskRoot, targetFolder, progress, ct,
                () => LostDisk(job));

            if (!report.Canceled && LostDisk(job) is null && Drives.IsMainId(job.SourceDrive) && plugins.IsInstalled("vault"))
                await CopyHiddenAsync(job, targetDiskRoot, ct);

            store.AddReport(job.Id, report);
            SaveSummary(job, new BackupSummary(report.Finished, report.Success, report.Canceled, false,
                report.Copied, report.Updated, report.Deleted, report.Skipped.Count, report.Errors.Count,
                report.BytesCopied, (long)report.Duration.TotalSeconds, Message(report)));
            Set(j => j with
            {
                Status = report.Canceled ? BackupRunStatus.Canceled : report.Success ? BackupRunStatus.Done : BackupRunStatus.Failed,
                Report = report, Phase = "done",
            });
            log.LogInformation("Backup {Job}: {Copied} copied, {Updated} updated, {Deleted} trashed, {Errors} errors",
                job.Name, report.Copied, report.Updated, report.Deleted, report.Errors.Count);

            Set(j => j with { Phase = "finish" });
            if (!report.Canceled)
                await disks.FinishAsync(job.TargetDrive, targetSettings.After, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Set(j => j with { Status = BackupRunStatus.Canceled, Phase = "done" });
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Backup {Job} failed", job.Name);
            var message = e is VaultReadOnlyException
                ? "Hedef disk salt okunur. Yedek yazmak için önce diski yazılabilir yap. Hiçbir şey değiştirilmedi."
                : e.Message;
            SaveSummary(job, new BackupSummary(clock.GetUtcNow(), false, false, false, 0, 0, 0, 0, 1, 0, 0, message));
            Set(j => j with { Status = BackupRunStatus.Failed, Error = message, Phase = "done" });
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>Checked against the kernel's mount table before every step: why the job must stop, or null.</summary>
    private string? LostDisk(BackupJob job)
    {
        bool Gone(string id)
        {
            try { return disks.MountOf(id) is null; }
            catch (VaultPathException) { return true; }
        }
        if (Gone(job.TargetDrive))
            return "Hedef disk kayboldu (çıkarıldı ya da bağlantısı koptu). İş durduruldu; hedefteki dosyalar olduğu gibi kaldı.";
        if (!Drives.IsMainId(job.SourceDrive) && Gone(job.SourceDrive))
            return "Kaynak disk kayboldu (çıkarıldı ya da bağlantısı koptu). İş durduruldu; hedefte silme yapılmadı.";
        return null;
    }

    private async Task<VaultRoot> OpenSourceAsync(BackupJob job, CancellationToken ct)
    {
        if (Drives.IsMainId(job.SourceDrive))
            return main;
        var s = diskSettings.Get(job.SourceDrive)
            ?? throw new VaultConflictException("Kaynak disk kayıtlı değil. Önce diskler sayfasından diski tanıt.");
        var mount = await MountAsync(job.SourceDrive, ct);
        // a read-only source is fine: a backup only reads it
        return new VaultRoot(disks.PathOf(job.SourceDrive), readOnly: s.ReadOnly || mount.ReadOnly);
    }

    private async Task<(VaultRoot DiskRoot, DiskSettings Settings)> OpenTargetAsync(BackupJob job, CancellationToken ct)
    {
        var s = diskSettings.Get(job.TargetDrive)
            ?? throw new VaultConflictException("Hedef disk kayıtlı değil. Önce diskler sayfasından diski tanıt.");
        if (s.DiskId.Length == 0)
            throw new VaultConflictException("Hedef disk henüz yedek diski yapılmadı. Diskler sayfasından 'Yedek diski yap' de.");
        var mount = await MountAsync(job.TargetDrive, ct);
        if (s.ReadOnly || mount.ReadOnly)
            throw new VaultReadOnlyException();
        var path = disks.PathOf(job.TargetDrive);
        var marker = DiskMarker.Read(path);
        if (marker?.DiskId != s.DiskId)
            throw new VaultConflictException("Hedef disk, ayarlardaki disk değil (kimlik dosyası tutmuyor). Güvenlik için hiçbir şey yazılmadı.");
        var root = new VaultRoot(path, readOnly: false);
        return (root, s);
    }

    /// <summary>The current mount; a disk that is not mounted yet is mounted in its setting's mode.</summary>
    private async Task<MountTable.Mount> MountAsync(string driveId, CancellationToken ct)
    {
        if (disks.MountOf(driveId) is { } mounted)
            return mounted;
        var path = await disks.EnsureMountedAsync(driveId, ct);
        return disks.MountOf(driveId)
            ?? throw new VaultConflictException($"Disk bağlandı ama DiskHaven içinden görünmüyor ({path}). Güvenlik için işlem yapılmadı.");
    }

    /// <summary>A folder that belongs to another job, or that holds files and was never adopted, is never written to.</summary>
    private static void CheckMarker(BackupJob job, string folder)
    {
        var marker = BackupMarker.Read(folder);
        if (marker is not null && marker.JobId != job.Id)
            throw new VaultConflictException(
                $"Bu hedef klasör '{marker.Name}' adlı başka bir yedeğe ait. Güvenlik için hiçbir şey yazılmadı.");
        if (marker is null && BackupMarker.HasUserContent(folder) && !job.AdoptExisting)
            throw new VaultConflictException(
                "Hedef klasör boş değil ve daha önce bir yedeğe ait değil. İçindeki dosyaların korunması için " +
                "yedeği 'mevcut dosyaları sahiplen' seçeneğiyle kaydet. Hiçbir şey değiştirilmedi.");
    }

    private static void WriteMarker(BackupJob job, string folder)
    {
        var marker = BackupMarker.Read(folder);
        if (marker?.JobId == job.Id)
            return;
        new BackupMarker(job.Id, job.Name, job.SourceDrive, DateTimeOffset.UtcNow).Write(folder);
    }

    /// <summary>
    /// The encrypted hidden folder of the vault plugin travels with a main-storage backup, as before.
    /// It is not part of the user's folders and is never mirrored back.
    /// </summary>
    private async Task CopyHiddenAsync(BackupJob job, VaultRoot target, CancellationToken ct)
    {
        var internalDir = Path.Combine(main.FullPath, VaultRoot.InternalFolderName);
        if (!Directory.Exists(Path.Combine(internalDir, "hideaway")))
            return;
        try
        {
            Set(j => j with { Phase = "copy-hidden" });
            var hiddenRoot = Path.Combine(target.FullPath, ".diskhaven-data");
            var hidden = await Task.Run(() => SyncPlanner.Plan(internalDir, ["hideaway"], hiddenRoot, ct), ct);
            var report = await new SyncExecutor(clock).ExecuteAsync(hidden, internalDir, hiddenRoot,
                new Progress<SyncProgress>(_ => { }), ct);
            if (report.Errors.Count > 0)
                log.LogWarning("Hidden folder copy for {Job}: {Errors} errors", job.Name, report.Errors.Count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or VaultPathException)
        {
            log.LogWarning(e, "Hidden folder copy for {Job} failed", job.Name);
        }
    }

    private void SaveSummary(BackupJob job, BackupSummary summary)
    {
        try
        {
            store.Update(job.Id, j => j with
            {
                LastRun = summary,
                LastSuccessAt = summary.Success ? summary.Finished : j.LastSuccessAt,
            });
        }
        catch (KeyNotFoundException)
        {
            // the owner deleted the job while it was running: the report in the history is enough
        }
    }

    private static string? Message(SyncReport report) =>
        report.Canceled ? "Durduruldu"
        : report.Errors.Count > 0 ? $"{report.Errors.Count} dosyada hata"
        : null;

    private static int Percent(int part, int total) => total == 0 ? 0 : (int)Math.Round(part * 100.0 / total);

    private static string Gb(long bytes) => $"{bytes / 1073741824.0:0.##} GB";
}

/// <summary>Starts each enabled backup once a day at its time (server local time), one at a time.</summary>
public sealed class BackupScheduler(BackupStore store, BackupJobRunner runner, TimeProvider clock, ILogger<BackupScheduler> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var day = DateOnly.MinValue;
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = clock.GetLocalNow();
            var today = DateOnly.FromDateTime(now.DateTime);
            if (today != day)
            {
                day = today;
                attempted.Clear();
            }
            if (runner.IsRunning)
                continue;
            foreach (var job in store.All())
            {
                if (!job.Enabled || job.SourceFolders.Count == 0 || attempted.Contains(job.Id))
                    continue;
                if (job.LastRun is { } last && DateOnly.FromDateTime(last.Finished.ToLocalTime().DateTime) == today)
                    continue;
                if (!TimeOnly.TryParse(job.Time, out var at) || TimeOnly.FromDateTime(now.DateTime) < at)
                    continue;
                attempted.Add(job.Id);
                try
                {
                    runner.Start(job.Id, scheduled: true);
                    log.LogInformation("Scheduled backup started: {Job}", job.Name);
                }
                catch (Exception e)
                {
                    log.LogWarning(e, "Scheduled backup {Job} could not start", job.Name);
                }
                break;   // one at a time; the next job is picked up on a later tick
            }
        }
    }
}
