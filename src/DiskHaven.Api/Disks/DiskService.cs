using DiskHaven.Core.Storage;
using DiskHaven.Core.Sync;

namespace DiskHaven.Api.Disks;

public sealed record DiskUsage(long Total, long Free);

/// <summary>Everything the UI shows for one disk.</summary>
public sealed record DiskView(
    string Id, string Kind, string Label, string Name, string FsType, long Size, string Model,
    string State, string Power, bool Adopted, bool Foreign, DiskUsage? Usage,
    IReadOnlyList<string> Folders, SyncSchedule? Schedule, AfterSync? After, SyncSummary? LastSync);

/// <summary>Combines what the disk agent sees with DiskHaven's own settings for each disk.</summary>
public sealed class DiskService(DiskAgentClient agent, DiskSettingsStore settings, VaultRoot vault, IConfiguration config)
{
    /// <summary>Where the agent's mount folder appears inside the container.</summary>
    private readonly string _disksPath = config["DiskHaven:DisksPath"] ?? "/disks";

    public string PathOf(string fsId) => Path.Combine(_disksPath, fsId);

    public async Task<IReadOnlyList<DiskView>> ListAsync(CancellationToken ct = default)
    {
        var live = await agent.ListAsync(ct);
        var adopted = settings.All();
        var result = new List<DiskView>();
        foreach (var d in live)
        {
            var s = adopted.FirstOrDefault(a => a.FsId == d.Id);
            var mounted = d.State == "mounted";
            DiskUsage? usage = null;
            var foreign = false;
            if (mounted && Directory.Exists(PathOf(d.Id)))
            {
                var drive = new DriveInfo(PathOf(d.Id));
                usage = new DiskUsage(drive.TotalSize, drive.AvailableFreeSpace);
                var marker = DiskMarker.Read(PathOf(d.Id));
                // a marker from another DiskHaven install, or a different disk with a cloned UUID
                foreign = s is not null && marker is not null && marker.DiskId != s.DiskId;
            }
            result.Add(new DiskView(d.Id, d.Kind, d.Label, s?.Name ?? (d.Label.Length > 0 ? d.Label : d.Model), d.FsType, d.Size,
                d.Model, d.State, d.Power, s is not null, foreign, usage, s?.Folders ?? [], s?.Schedule, s?.After, s?.LastSync));
        }
        return result.OrderByDescending(d => d.Adopted).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Makes sure the disk is powered and mounted; returns its path inside the container.</summary>
    public async Task<string> EnsureMountedAsync(string fsId, CancellationToken ct = default)
    {
        var disk = (await agent.ListAsync(ct)).FirstOrDefault(d => d.Id == fsId)
            ?? throw new DiskAgentException("Disk bulunamadı.");
        if (disk.State == "off")
            await agent.PowerOnAsync(fsId, ct);
        await agent.MountAsync(fsId, ct);
        var path = PathOf(fsId);
        // the mount must be visible inside the container (bind mount with rslave propagation)
        for (var i = 0; i < 10 && !File.Exists(Path.Combine(path, DiskMarker.FileName)) && !Directory.EnumerateFileSystemEntries(path).Any(); i++)
            await Task.Delay(200, ct);
        return path;
    }

    public async Task<DiskSettings> AdoptAsync(string fsId, string name, CancellationToken ct = default)
    {
        name = FileNames.Validate(name);
        var root = await EnsureMountedAsync(fsId, ct);
        var marker = DiskMarker.Read(root);
        if (marker is null)
        {
            marker = new DiskMarker(Guid.NewGuid().ToString("N"), name, DateTimeOffset.UtcNow);
            marker.Write(root);
        }
        var s = settings.Get(fsId) ?? new DiskSettings(fsId, marker.DiskId, name, [], SyncSchedule.Off, AfterSync.PowerOff, null);
        s = s with { Name = name, DiskId = marker.DiskId };
        settings.Save(s);
        return s;
    }

    public DiskSettings UpdateSettings(string fsId, string? name, IReadOnlyList<string>? folders, SyncSchedule? schedule, AfterSync? after)
    {
        if (folders is not null)
        {
            folders = SyncPlanner.NormalizeFolders(folders);
            foreach (var f in folders)
                if (!Directory.Exists(vault.Resolve(f)))
                    throw new VaultPathException($"Folder not found: {f}");
        }
        if (schedule is not null && !TimeOnly.TryParse(schedule.Time, out _))
            throw new VaultPathException("Invalid time.");
        settings.Update(fsId, s => s with
        {
            Name = name is null ? s.Name : FileNames.Validate(name),
            Folders = folders ?? s.Folders,
            Schedule = schedule is null ? s.Schedule : schedule with { LastRun = s.Schedule.LastRun },
            After = after ?? s.After,
        });
        return settings.Get(fsId)!;
    }

    /// <summary>Applies the disk's "after sync" setting: power off (safe to unplug), spin down, or leave on.</summary>
    public async Task FinishAsync(string fsId, AfterSync after, CancellationToken ct = default)
    {
        switch (after)
        {
            case AfterSync.PowerOff:
                await agent.PowerOffAsync(fsId, ct);
                break;
            case AfterSync.Standby:
                await agent.UnmountAsync(fsId, ct);
                await agent.StandbyAsync(fsId, ct);
                break;
        }
    }
}
