using DiskHaven.Core.Storage;

namespace DiskHaven.Api.Disks;

/// <summary>
/// Which disk is busy right now. A backup or transfer holds a lease on every drive it reads from or writes to
/// (the main storage is ""); a second job that wants the same drive is refused, and so is unmounting, powering
/// off or switching a drive to read-only while a job is using it. One small lock, no disk access.
/// </summary>
public sealed class ActiveJobs
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _drives = new(StringComparer.Ordinal);

    /// <summary>Why the drive cannot be used now, or null when it is free.</summary>
    public string? BusyWith(string driveId)
    {
        lock (_lock) return _drives.TryGetValue(driveId, out var what) ? what : null;
    }

    public bool Uses(string driveId) => BusyWith(driveId) is not null;

    /// <summary>
    /// Reserves every drive for one job. All-or-nothing: when one of them is busy the call throws and nothing
    /// is reserved. Dispose the lease when the job ends.
    /// </summary>
    public IDisposable Lease(string what, params string[] driveIds)
    {
        lock (_lock)
        {
            foreach (var id in driveIds.Distinct(StringComparer.Ordinal))
                if (_drives.TryGetValue(id, out var busy))
                    throw new VaultConflictException($"Bu disk şu an başka bir işte kullanılıyor: {busy}.");
            foreach (var id in driveIds.Distinct(StringComparer.Ordinal))
                _drives[id] = what;
        }
        return new Reservation(this, driveIds);
    }

    private void Release(IEnumerable<string> ids)
    {
        lock (_lock)
            foreach (var id in ids)
                _drives.Remove(id);
    }

    private sealed class Reservation(ActiveJobs owner, IEnumerable<string> ids) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(ids);
        }
    }
}
