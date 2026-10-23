namespace HomeVault.Core.Storage;

/// <summary>Total size and contents of a folder, including everything below it.</summary>
public readonly record struct FolderStats(long Size, int Files, int Folders)
{
    public static FolderStats operator +(FolderStats a, FolderStats b) => new(a.Size + b.Size, a.Files + b.Files, a.Folders + b.Folders);
    public static FolderStats operator -(FolderStats a, FolderStats b) => new(a.Size - b.Size, a.Files - b.Files, a.Folders - b.Folders);
}

/// <summary>Told about every change HomeVault makes, so derived data (folder sizes) stays current.</summary>
public interface IVaultChangeListener
{
    /// <summary>Called just before a file or folder leaves its place (delete, move, rename).</summary>
    void Removing(string fullPath);
    /// <summary>Called after a file or folder appeared (create, copy, move, upload, restore).</summary>
    void Added(string fullPath);
}

/// <summary>
/// Keeps the recursive size of every folder in memory so listings can show folder sizes
/// without walking the disk. A full scan runs at start-up and periodically (to pick up changes
/// made outside HomeVault, e.g. over SMB); HomeVault's own changes are applied incrementally.
/// </summary>
public sealed class FolderSizeIndex(VaultRoot root) : IVaultChangeListener
{
    private readonly Lock _lock = new();
    private Dictionary<string, FolderStats> _stats = new(StringComparer.Ordinal);
    private long _changesDuringScan;

    public DateTimeOffset? LastScan { get; private set; }
    public bool Ready => LastScan is not null;

    /// <summary>Stats for a folder (client path), or null while the first scan is still running.</summary>
    public FolderStats? Get(string relativePath)
    {
        lock (_lock)
            return _stats.TryGetValue(VaultRoot.Normalize(relativePath), out var s) ? s : null;
    }

    /// <summary>Walks the whole vault. Safe to call while the app is in use.</summary>
    public void Scan(CancellationToken ct = default)
    {
        long before;
        lock (_lock) before = _changesDuringScan;
        var fresh = new Dictionary<string, FolderStats>(StringComparer.Ordinal);
        Measure(root.FullPath, fresh, ct);
        lock (_lock)
        {
            _stats = fresh;
            LastScan = DateTimeOffset.UtcNow;
            if (_changesDuringScan != before)
                LastScan = null; // something changed mid-scan: caller should scan again soon
        }
    }

    public void Removing(string fullPath)
    {
        var rel = root.ToRelative(fullPath);
        lock (_lock)
        {
            _changesDuringScan++;
            FolderStats removed;
            if (Directory.Exists(fullPath))
            {
                removed = _stats.GetValueOrDefault(rel) + new FolderStats(0, 0, 1);
                var prefix = rel + "/";
                foreach (var key in _stats.Keys.Where(k => k == rel || k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                    _stats.Remove(key);
            }
            else if (File.Exists(fullPath))
                removed = new FolderStats(new FileInfo(fullPath).Length, 1, 0);
            else
                return;
            ApplyToAncestors(rel, f => f - removed);
        }
    }

    public void Added(string fullPath)
    {
        var rel = root.ToRelative(fullPath);
        FolderStats added;
        Dictionary<string, FolderStats>? subtree = null;
        if (Directory.Exists(fullPath))
        {
            subtree = new Dictionary<string, FolderStats>(StringComparer.Ordinal);
            added = Measure(fullPath, subtree, CancellationToken.None) + new FolderStats(0, 0, 1);
        }
        else if (File.Exists(fullPath))
            added = new FolderStats(new FileInfo(fullPath).Length, 1, 0);
        else
            return;
        lock (_lock)
        {
            _changesDuringScan++;
            if (subtree is not null)
                foreach (var (k, v) in subtree)
                    _stats[k] = v;
            ApplyToAncestors(rel, f => f + added);
        }
    }

    private void ApplyToAncestors(string rel, Func<FolderStats, FolderStats> change)
    {
        var parent = rel;
        do
        {
            parent = parent.Contains('/') ? parent[..parent.LastIndexOf('/')] : "";
            if (_stats.TryGetValue(parent, out var s))
                _stats[parent] = change(s);
        } while (parent.Length > 0);
    }

    private FolderStats Measure(string dir, Dictionary<string, FolderStats> into, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var total = new FolderStats();
        try
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (info.LinkTarget is not null)
                    continue;
                if (dir == root.FullPath && info.Name.Equals(VaultRoot.InternalFolderName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (info is FileInfo f)
                    total += new FolderStats(f.Length, 1, 0);
                else
                    total += Measure(info.FullName, into, ct) + new FolderStats(0, 0, 1);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // unreadable folder: count what we could read
        }
        into[root.ToRelative(dir)] = total;
        return total;
    }
}
