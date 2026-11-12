using DiskHaven.Core.Storage;
using DiskHaven.Core.Sync;

namespace DiskHaven.Api.Disks;

public sealed record DiskUsage(long Total, long Free);

/// <summary>Everything the UI shows for one disk.</summary>
/// <param name="System">Shown only: in use by the server (system, photos, server backup) or protected. Never touched.</param>
/// <param name="ReadOnly">The owner's setting. New disks start read-only.</param>
/// <param name="MountedReadOnly">What the kernel reports for the current mount; null when not mounted.</param>
public sealed record DiskView(
    string Id, string Kind, string Label, string Name, string FsType, long Size, string Model,
    string State, string Power, bool Adopted, bool Foreign, DiskUsage? Usage,
    IReadOnlyList<string> Folders, SyncSchedule? Schedule, AfterSync? After, SyncSummary? LastSync,
    bool System = false, string? SystemReason = null, string? SystemMount = null,
    bool ReadOnly = true, bool? MountedReadOnly = null, bool Registered = false);

/// <summary>Combines what the disk agent sees with DiskHaven's own settings for each disk.</summary>
public sealed class DiskService(DiskAgentClient agent, DiskSettingsStore settings, VaultRoot vault, IConfiguration config,
    MountTable mounts, ILogger<DiskService> log)
{
    /// <summary>Where the agent's mount folder appears inside the container.</summary>
    private readonly string _disksPath = config["DiskHaven:DisksPath"] ?? "/disks";

    public string PathOf(string fsId)
    {
        // the id becomes a folder name: only what a filesystem UUID can contain
        if (fsId.Length is < 4 or > 64 || !fsId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new VaultPathException("Invalid disk id.");
        return Path.Combine(_disksPath, fsId);
    }

    /// <summary>The disk's folder when a filesystem is really mounted there, with the kernel's read-only flag.</summary>
    public MountTable.Mount? MountOf(string fsId) => mounts.At(PathOf(fsId));

    public async Task<IReadOnlyList<DiskView>> ListAsync(CancellationToken ct = default)
    {
        var live = await agent.ListAsync(ct);
        RegisterNew(live);
        var known = settings.All();
        var result = new List<DiskView>();
        foreach (var d in live)
        {
            if (d.System)
            {
                // shown with what the system uses it for; nothing else is read from it
                result.Add(new DiskView(d.Id, d.Kind, d.Label, d.Label.Length > 0 ? d.Label : d.Model, d.FsType, d.Size, d.Model,
                    "system", d.Power, false, false, null, [], null, null, null,
                    System: true, SystemReason: d.SystemReason, SystemMount: d.MountPoint, ReadOnly: true));
                continue;
            }
            var s = known.FirstOrDefault(a => a.FsId == d.Id);
            DiskUsage? usage = null;
            var foreign = false;
            var mount = d.State == "mounted" ? MountOf(d.Id) : null;
            if (mount is not null)
            {
                var drive = new DriveInfo(PathOf(d.Id));
                usage = new DiskUsage(drive.TotalSize, drive.AvailableFreeSpace);
                var marker = DiskMarker.Read(PathOf(d.Id));
                // a marker from another DiskHaven install, or a different disk with a cloned UUID
                foreign = s is { DiskId.Length: > 0 } && marker is not null && marker.DiskId != s.DiskId;
            }
            result.Add(new DiskView(d.Id, d.Kind, d.Label, s?.Name ?? (d.Label.Length > 0 ? d.Label : d.Model), d.FsType, d.Size,
                d.Model, d.State, d.Power, s is { DiskId.Length: > 0 }, foreign, usage, s?.Folders ?? [], s?.Schedule, s?.After, s?.LastSync,
                ReadOnly: s?.ReadOnly ?? true, MountedReadOnly: mount?.ReadOnly, Registered: s is not null));
        }
        return result.OrderBy(d => d.System).ThenByDescending(d => d.Registered).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Every disk seen for the first time is remembered as read-only. Nothing is written to the disk itself.</summary>
    private void RegisterNew(IReadOnlyList<AgentDisk> live)
    {
        foreach (var d in live)
        {
            if (d.System || d.State == "off" || settings.Get(d.Id) is not null)
                continue;
            var name = d.Label.Length > 0 ? d.Label : d.Model.Length > 0 ? d.Model : "Disk";
            try { name = FileNames.Validate(name); } catch (VaultPathException) { name = "Disk"; }
            settings.Save(new DiskSettings(d.Id, "", name, [], SyncSchedule.Off, AfterSync.StayOn, null, ReadOnly: true));
            log.LogInformation("New disk {Id} ({Model}, {FsType}) registered read-only", d.Id, d.Model, d.FsType);
        }
    }

    /// <summary>
    /// Makes sure the disk is powered and mounted in the mode its settings ask for, and that the kernel agrees;
    /// returns its path inside the container.
    /// </summary>
    public async Task<string> EnsureMountedAsync(string fsId, CancellationToken ct = default)
    {
        var disk = (await agent.ListAsync(ct)).FirstOrDefault(d => d.Id == fsId)
            ?? throw new DiskAgentException("Disk bulunamadı.");
        if (disk.System)
            throw new DiskAgentException("Bu bir sistem diski. DiskHaven bu diske hiçbir işlem yapmaz.");
        RegisterNew([disk]);
        var s = settings.Get(fsId)!;
        if (disk.State == "off")
            await agent.PowerOnAsync(fsId, ct);
        await agent.MountAsync(fsId, s.ReadOnly, ct);
        var path = PathOf(fsId);
        // the mount must be visible inside the container (bind mount with rslave propagation)
        MountTable.Mount? mount = null;
        for (var i = 0; i < 20 && (mount = MountOf(fsId)) is null; i++)
            await Task.Delay(200, ct);
        if (mount is null)
            throw new DiskAgentException("Disk bağlandı ama DiskHaven içinden görünmüyor. Güvenlik için işlem yapılmadı.");
        if (s.ReadOnly && !mount.ReadOnly)
            throw new DiskAgentException("Disk salt okunur bağlanamadı. Güvenlik için işlem yapılmadı.");
        return path;
    }

    /// <summary>
    /// Switches a disk between read-only and writable. A mounted disk is first mounted again in the new mode; the
    /// setting changes only when that worked. The first time a disk becomes writable it gets its marker file.
    /// </summary>
    public async Task<DiskSettings> SetReadOnlyAsync(string fsId, bool readOnly, CancellationToken ct = default)
    {
        var s = settings.Get(fsId) ?? throw new KeyNotFoundException("Disk is not registered.");
        var disk = (await agent.ListAsync(ct)).FirstOrDefault(d => d.Id == fsId);
        if (disk is { System: true })
            throw new DiskAgentException("Bu bir sistem diski. DiskHaven bu diske hiçbir işlem yapmaz.");
        if (disk is { State: "mounted" })
        {
            var actual = await agent.MountAsync(fsId, readOnly, ct);
            if (actual != readOnly)
                throw new DiskAgentException("Disk istenen modda bağlanamadı; ayar değiştirilmedi.");
        }
        settings.Update(fsId, x => x with { ReadOnly = readOnly });
        if (!readOnly && disk is { State: "mounted" } && MountOf(fsId) is { ReadOnly: false })
        {
            try { EnsureMarker(fsId); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { log.LogWarning(e, "Disk {Id}: marker file could not be written; it is written when the disk becomes a backup disk", fsId); }
        }
        log.LogInformation("Disk {Id} set to {Mode}", fsId, readOnly ? "read-only" : "writable");
        return settings.Get(fsId)!;
    }

    /// <summary>Writes the disk's marker file once, so syncs can make sure they write to the right disk.</summary>
    private void EnsureMarker(string fsId)
    {
        var s = settings.Get(fsId)!;
        var root = PathOf(fsId);
        var marker = DiskMarker.Read(root);
        if (marker is null)
        {
            if (s.DiskId.Length > 0)
                return;   // known disk lost its marker: do not invent a new identity silently
            marker = new DiskMarker(Guid.NewGuid().ToString("N"), s.Name, DateTimeOffset.UtcNow);
            marker.Write(root);
        }
        if (s.DiskId.Length == 0)
            settings.Update(fsId, x => x with { DiskId = marker.DiskId });
    }

    /// <summary>Names the disk and makes it a backup disk (marker file). Needs a writable disk.</summary>
    public async Task<DiskSettings> AdoptAsync(string fsId, string name, CancellationToken ct = default)
    {
        name = FileNames.Validate(name);
        var root = await EnsureMountedAsync(fsId, ct);
        var s = settings.Get(fsId)!;
        if (s.ReadOnly)
            throw new VaultReadOnlyException();
        var marker = DiskMarker.Read(root);
        if (marker is null)
        {
            marker = new DiskMarker(Guid.NewGuid().ToString("N"), name, DateTimeOffset.UtcNow);
            marker.Write(root);
        }
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
