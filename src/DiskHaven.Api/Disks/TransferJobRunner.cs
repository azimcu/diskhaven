using System.Security.Claims;
using DiskHaven.Core.Storage;

namespace DiskHaven.Api.Disks;

public enum JobStatus { Running, Done, Failed, Canceled }

public sealed record TransferJob(
    string Id, string FromDrive, string ToDrive, bool Move, IReadOnlyList<string> Paths, string Destination,
    DateTimeOffset Started, JobStatus Status, TransferProgress? Progress, TransferReport? Report, string? Error, string User);

/// <summary>
/// Runs one copy / move between drives at a time in the background (large folders take hours), with live progress
/// for the UI. Everything is checked when the job is started, so a request that would fail fails right away.
/// </summary>
public sealed class TransferJobRunner(Drives drives, ActiveJobs active, TimeProvider clock, ILogger<TransferJobRunner> log, ActivityLog activity,
    PluginStore plugins)
{
    private readonly Lock _lock = new();
    private TransferJob? _job;
    private CancellationTokenSource? _cts;

    public TransferJob? Current { get { lock (_lock) return _job; } }

    /// <summary>True while a running job reads from or writes to this disk.</summary>
    public bool Uses(string driveId) => active.Uses(driveId);

    public TransferJob Start(string? fromDrive, IReadOnlyList<string> paths, string? toDrive, string? destination, bool move, ClaimsPrincipal user)
    {
        var from = drives.Resolve(fromDrive, user);
        var to = drives.Resolve(toDrive, user);
        if (paths.Count == 0)
            throw new VaultPathException("Nothing to transfer.");
        to.Root.EnsureWritable();
        if (move)
            from.Root.EnsureWritable();
        foreach (var p in paths)
            if (from.Root.Resolve(p) == from.Root.FullPath)
                throw new VaultPathException("The root folder cannot be changed.");
        if (!Directory.Exists(to.Root.Resolve(destination)))
            throw new DirectoryNotFoundException("Destination folder not found.");
        lock (_lock)
        {
            if (_job?.Status == JobStatus.Running)
                throw new VaultConflictException("A transfer is already running.");
            _cts = new CancellationTokenSource();
            var name = user.Identity?.Name ?? "";
            // no backup or other transfer may read or write these drives at the same time
            var lease = active.Lease(move ? "taşıma" : "kopyalama", from.Id, to.Id);
            _job = new TransferJob(Guid.NewGuid().ToString("N"), from.Id, to.Id, move, paths.ToList(), VaultRoot.Normalize(destination),
                clock.GetUtcNow(), JobStatus.Running, null, null, null, name);
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(from, to, _job, lease, token));
            return _job;
        }
    }

    public void Cancel()
    {
        lock (_lock) _cts?.Cancel();
    }

    private void Set(Func<TransferJob, TransferJob> change)
    {
        lock (_lock) _job = change(_job!);
    }

    private async Task RunAsync(DriveContext from, DriveContext to, TransferJob job, IDisposable lease, CancellationToken ct)
    {
        try
        {
            var progress = new Progress<TransferProgress>(p => Set(j => j with { Progress = p }));
            var report = await new DriveTransfer(clock).RunAsync(from.Root, job.Paths, to.Root, job.Destination, job.Move, progress, ct,
                () => drives.Missing(from) ?? drives.Missing(to));
            Set(j => j with
            {
                Status = report.Canceled ? JobStatus.Canceled : report.Success ? JobStatus.Done : JobStatus.Failed,
                Report = report,
            });
            log.LogInformation("Transfer {From} -> {To} ({Mode}): {Files} files, {Bytes} bytes, {Errors} errors",
                job.FromDrive, job.ToDrive, job.Move ? "move" : "copy", report.FilesCopied, report.BytesCopied, report.Errors.Count);
            try
            {
                if (plugins.IsInstalled("activity"))
                    activity.Add(new ActivityLog.Item(clock.GetUtcNow(), job.User, job.Move ? "move" : "copy",
                        job.Paths.Select(p => Label(job.FromDrive, p)).ToArray(), "→ " + Label(job.ToDrive, "/" + job.Destination), null));
            }
            catch (Exception e) { log.LogWarning(e, "Activity log write failed"); }
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Transfer {From} -> {To} failed", job.FromDrive, job.ToDrive);
            Set(j => j with { Status = JobStatus.Failed, Error = e.Message });
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static string Label(string drive, string path) => Drives.IsMainId(drive) ? path : $"[{drive}] {path}";
}
