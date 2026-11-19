using System.Text.Json;
using System.Text.Json.Serialization;
using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Accounts;

/// <summary>What a person may do with an item. Each level includes the ones below it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessLevel
{
    /// <summary>Nothing: the item does not exist for this person (it is not listed, and opening it answers "not found").</summary>
    None = 0,
    /// <summary>See and open (download, preview, copy elsewhere).</summary>
    Read = 1,
    /// <summary>Also create, upload, rename, move and delete inside it.</summary>
    Write = 2,
    /// <summary>Also share it with other people. A user folder or a whole disk given to someone.</summary>
    Owner = 3,
}

/// <summary>
/// One permission: <see cref="Principal"/> (a user name, or <see cref="PermissionStore.Everyone"/>) has
/// <see cref="Level"/> on the item at <see cref="Path"/> of <see cref="Drive"/> ("" = main storage, otherwise the disk's
/// file-system id) and on everything inside it. A path of "" is the whole drive.
/// </summary>
public sealed record Grant(string Id, string Drive, string Path, string Principal, AccessLevel Level, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>
/// Who may see and change what, for accounts that are not admins (admins can do everything everywhere).
/// The model is the one home NAS systems and Windows file servers use:
/// <list type="bullet">
/// <item>permissions are given to a folder (or file) and hold for everything inside it;</item>
/// <item>the highest permission that applies wins (there are no "deny" rules to reason about);</item>
/// <item>access-based enumeration: a folder lists only what the person may see, and a folder that leads to something
/// shared with them shows only the way there. Things they may not see are never listed and answer "not found", so
/// nobody learns that they exist ("access denied" would tell them);</item>
/// <item>changing an item itself (rename, move, delete, hide) needs Write on the folder that contains it, like on any
/// Unix or Windows file system; so a folder shared with Write can be filled, but the shared folder itself stays put.</item>
/// </list>
/// Stored as a small JSON file next to the accounts.
/// </summary>
public sealed class PermissionStore
{
    /// <summary>Principal that stands for every account (a shared "Public" folder).</summary>
    public const string Everyone = "*";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly Lock _lock = new();
    private List<Grant> _grants;

    public PermissionStore(string configDir)
    {
        Directory.CreateDirectory(configDir);
        _file = System.IO.Path.Combine(configDir, "permissions.json");
        _grants = File.Exists(_file) ? JsonSerializer.Deserialize<List<Grant>>(File.ReadAllText(_file), Json) ?? [] : [];
    }

    public IReadOnlyList<Grant> All() { lock (_lock) return _grants.ToList(); }

    private static bool Applies(Grant g, string user) =>
        g.Principal == Everyone || g.Principal.Equals(user, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="ancestor"/> is <paramref name="path"/> or a folder above it ("" is above everything).</summary>
    public static bool Covers(string ancestor, string path) =>
        ancestor.Length == 0 || path == ancestor || (path.Length > ancestor.Length && path[ancestor.Length] == '/' && path.StartsWith(ancestor, StringComparison.Ordinal));

    /// <summary>The permissions that apply to this person (their own and the ones given to everyone).</summary>
    public IReadOnlyList<Grant> For(string user) { lock (_lock) return _grants.Where(g => Applies(g, user)).ToList(); }

    /// <summary>The person's level on an item: the highest permission on it or on a folder above it.</summary>
    public AccessLevel LevelOf(string user, string drive, string? path)
    {
        var p = VaultRoot.Normalize(path);
        var level = AccessLevel.None;
        lock (_lock)
            foreach (var g in _grants)
                if (g.Drive == drive && g.Level > level && Applies(g, user) && Covers(g.Path, p))
                    level = g.Level;
        return level;
    }

    /// <summary>
    /// Which entries of a folder the person may see: null = all of them (they may read the folder); otherwise only
    /// the names of the sub-folders that lead to something shared with them (empty = the folder is not theirs at all).
    /// </summary>
    public HashSet<string>? VisibleIn(string user, string drive, string? folder)
    {
        var f = VaultRoot.Normalize(folder);
        if (LevelOf(user, drive, f) >= AccessLevel.Read)
            return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        lock (_lock)
            foreach (var g in _grants)
                if (g.Drive == drive && g.Level >= AccessLevel.Read && Applies(g, user) && g.Path != f && Covers(f, g.Path))
                    names.Add(g.Path[(f.Length == 0 ? 0 : f.Length + 1)..].Split('/')[0]);
        return names;
    }

    /// <summary>True when the person may see the item: they can read it, or it is a folder on the way to something they can.</summary>
    public bool CanSee(string user, string drive, string? path)
    {
        var p = VaultRoot.Normalize(path);
        if (LevelOf(user, drive, p) >= AccessLevel.Read)
            return true;
        lock (_lock)
            return _grants.Any(g => g.Drive == drive && g.Level >= AccessLevel.Read && Applies(g, user) && Covers(p, g.Path));
    }

    /// <summary>True when anything on the drive is shared with the person (the drive then shows up in their list).</summary>
    public bool HasAny(string user, string drive) { lock (_lock) return _grants.Any(g => g.Drive == drive && g.Level >= AccessLevel.Read && Applies(g, user)); }

    /// <summary>Permissions given exactly on this item (not inherited).</summary>
    public IReadOnlyList<Grant> On(string drive, string? path)
    {
        var p = VaultRoot.Normalize(path);
        lock (_lock) return _grants.Where(g => g.Drive == drive && g.Path == p).ToList();
    }

    /// <summary>Permissions that come from the folders above the item.</summary>
    public IReadOnlyList<Grant> Above(string drive, string? path)
    {
        var p = VaultRoot.Normalize(path);
        lock (_lock) return _grants.Where(g => g.Drive == drive && g.Path != p && Covers(g.Path, p)).ToList();
    }

    /// <summary>Names (in this folder) of the items that carry a permission themselves, for a "shared" mark.</summary>
    public HashSet<string> SharedIn(string drive, string? folder)
    {
        var f = VaultRoot.Normalize(folder);
        var names = new HashSet<string>(StringComparer.Ordinal);
        lock (_lock)
            foreach (var g in _grants)
                if (g.Drive == drive && g.Path != f && Covers(f, g.Path) && !g.Path[(f.Length == 0 ? 0 : f.Length + 1)..].Contains('/'))
                    names.Add(g.Path[(f.Length == 0 ? 0 : f.Length + 1)..]);
        return names;
    }

    /// <summary>
    /// Gives <paramref name="principal"/> a level on an item, replacing what it had there; <see cref="AccessLevel.None"/>
    /// takes the permission away. Returns the stored permission, or null when it was removed.
    /// </summary>
    public Grant? Set(string drive, string? path, string principal, AccessLevel level, string by)
    {
        var p = VaultRoot.Normalize(path);
        if (string.IsNullOrWhiteSpace(principal))
            throw new ArgumentException("Choose a person.");
        lock (_lock)
        {
            var list = _grants.ToList();
            var old = list.FindIndex(g => g.Drive == drive && g.Path == p && g.Principal.Equals(principal, StringComparison.OrdinalIgnoreCase));
            Grant? stored = null;
            if (level == AccessLevel.None)
            {
                if (old >= 0) list.RemoveAt(old);
            }
            else if (old >= 0)
                list[old] = stored = list[old] with { Level = level };
            else
                list.Add(stored = new Grant(Guid.NewGuid().ToString("N"), drive, p, principal, level, by, DateTimeOffset.UtcNow));
            Save(list);
            return stored;
        }
    }

    public Grant? Find(string id) { lock (_lock) return _grants.FirstOrDefault(g => g.Id == id); }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var list = _grants.Where(g => g.Id != id).ToList();
            if (list.Count != _grants.Count) Save(list);
        }
    }

    /// <summary>An item was renamed or moved inside its drive: its permissions (and those inside it) follow it.</summary>
    public void Moved(string drive, string? from, string? to)
    {
        var f = VaultRoot.Normalize(from);
        var t = VaultRoot.Normalize(to);
        if (f.Length == 0 || f == t)
            return;
        lock (_lock)
        {
            var changed = false;
            var list = _grants.Select(g =>
            {
                if (g.Drive != drive || !Covers(f, g.Path)) return g;
                changed = true;
                return g with { Path = t + g.Path[f.Length..] };
            }).ToList();
            if (changed) Save(list);
        }
    }

    /// <summary>
    /// An item was deleted (or moved to another drive): permissions on it and inside it are removed, so a new item
    /// that later gets the same name is not shared by accident.
    /// </summary>
    public void Deleted(string drive, string? path)
    {
        var p = VaultRoot.Normalize(path);
        if (p.Length == 0)
            return;
        lock (_lock)
        {
            var list = _grants.Where(g => !(g.Drive == drive && Covers(p, g.Path))).ToList();
            if (list.Count != _grants.Count) Save(list);
        }
    }

    /// <summary>An account was removed: its permissions go with it.</summary>
    public void RemoveUser(string user)
    {
        lock (_lock)
        {
            var list = _grants.Where(g => !g.Principal.Equals(user, StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count != _grants.Count) Save(list);
        }
    }

    private void Save(List<Grant> list)
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        File.Move(temp, _file, overwrite: true);
        _grants = list;
    }
}
