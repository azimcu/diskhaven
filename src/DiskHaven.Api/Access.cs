using System.Security.Claims;
using DiskHaven.Api.Disks;
using DiskHaven.Core.Accounts;
using DiskHaven.Core.Storage;

namespace DiskHaven.Api;

/// <summary>The person may see the item but not do this with it (answered with 403).</summary>
public sealed class PermissionDeniedException(string message = "You do not have permission for this.") : Exception(message);

/// <summary>
/// Applies <see cref="PermissionStore"/> to requests. Admins pass every check (the drive's own read-only setting still
/// holds). For everyone else an item they may not see answers exactly like a missing one (404), so they cannot even
/// learn that it exists; an item they can see but not change answers 403.
/// </summary>
public sealed class Access(PermissionStore perms)
{
    public static bool IsAdmin(ClaimsPrincipal u) => u.IsInRole("admin");
    private static string Name(ClaimsPrincipal u) => u.Identity?.Name ?? "";
    private static string ParentOf(string p) => p.Contains('/') ? p[..p.LastIndexOf('/')] : "";

    public PermissionStore Store => perms;

    public AccessLevel Level(ClaimsPrincipal u, DriveContext d, string? path) =>
        IsAdmin(u) ? AccessLevel.Owner : perms.LevelOf(Name(u), d.Id, path);

    /// <summary>The item must be visible to the person (otherwise "not found").</summary>
    public void See(ClaimsPrincipal u, DriveContext d, string? path)
    {
        if (!IsAdmin(u) && !perms.CanSee(Name(u), d.Id, path))
            throw new FileNotFoundException("Item not found.");
    }

    /// <summary>Open, download, preview or copy the item.</summary>
    public void Read(ClaimsPrincipal u, DriveContext d, string? path)
    {
        if (IsAdmin(u) || perms.LevelOf(Name(u), d.Id, path) >= AccessLevel.Read)
            return;
        See(u, d, path);
        throw new PermissionDeniedException();
    }

    /// <summary>Create, upload or paste into the folder.</summary>
    public void WriteInto(ClaimsPrincipal u, DriveContext d, string? folder)
    {
        if (IsAdmin(u) || perms.LevelOf(Name(u), d.Id, folder) >= AccessLevel.Write)
            return;
        See(u, d, folder);
        throw new PermissionDeniedException("You can only look at this folder; you cannot change it.");
    }

    /// <summary>Rename, move away, delete or hide the item: needs Write on the folder that holds it.</summary>
    public void Modify(ClaimsPrincipal u, DriveContext d, string? path)
    {
        if (IsAdmin(u))
            return;
        var p = VaultRoot.Normalize(path);
        See(u, d, p);
        if (p.Length == 0 || perms.LevelOf(Name(u), d.Id, ParentOf(p)) < AccessLevel.Write)
            throw new PermissionDeniedException(perms.LevelOf(Name(u), d.Id, p) >= AccessLevel.Write
                ? "This folder was shared with you: you can change what is inside it, not the folder itself."
                : "You can only look at this item; you cannot change it.");
    }

    /// <summary>A folder listing with only what the person may see (all of it for admins and readers).</summary>
    public IReadOnlyList<FileEntry> Filter(ClaimsPrincipal u, DriveContext d, string? folder, IReadOnlyList<FileEntry> entries)
    {
        if (IsAdmin(u))
            return entries;
        var visible = perms.VisibleIn(Name(u), d.Id, folder);
        if (visible is null)
            return entries;
        if (visible.Count == 0)
            throw new FileNotFoundException("Folder not found.");
        return entries.Where(e => visible.Contains(e.Name)).ToList();
    }

    /// <summary>Search hits the person may open (search walks the whole folder, so hidden areas are dropped here).</summary>
    public IReadOnlyList<FileEntry> Readable(ClaimsPrincipal u, DriveContext d, IReadOnlyList<FileEntry> entries) =>
        IsAdmin(u) ? entries : entries.Where(e => perms.LevelOf(Name(u), d.Id, e.Path) >= AccessLevel.Read).ToList();

    /// <summary>Admins anywhere; owners (a user folder, a disk given to them) inside what they own.</summary>
    public bool CanManage(ClaimsPrincipal u, DriveContext d, string? path) =>
        IsAdmin(u) || perms.LevelOf(Name(u), d.Id, path) == AccessLevel.Owner;
}
