using System.Security.Claims;
using DiskHaven.Core.Storage;
using DiskHaven.Core.Sync;

namespace DiskHaven.Api.Disks;

/// <summary>A drive as the file endpoints use it: the main storage or one mounted disk.</summary>
public sealed record DriveContext(string Id, bool IsMain, VaultRoot Root, FileBrowser Browser, FileOperations Ops,
    Archives Archives, UploadCommitter Committer);

/// <summary>A drive cannot be used right now (not registered, not mounted, no permission).</summary>
public sealed class DriveUnavailableException(string message, int status = StatusCodes.Status409Conflict) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Turns the "drive" of a request into the folder to work in. "" (or missing) is the main storage, as before.
/// Any other value is a disk id; such a drive is only for admins, must be registered and must be mounted right now
/// (checked against the kernel's mount table on every request). Its root is read-only when either the owner's
/// setting or the kernel says so; with both layers a mistake in one is caught by the other.
/// </summary>
public sealed class Drives(VaultRoot main, FileBrowser mainBrowser, FileOperations mainOps, Archives mainArchives,
    UploadCommitter mainCommitter, DiskService disks, DiskSettingsStore settings, PluginStore plugins, TimeProvider clock)
{
    public const string MainId = "";

    public static bool IsMainId(string? drive) => string.IsNullOrEmpty(drive) || drive == "main";

    public DriveContext Main => new(MainId, true, main, mainBrowser, mainOps, mainArchives, mainCommitter);

    public DriveContext Resolve(string? drive, ClaimsPrincipal user)
    {
        if (IsMainId(drive))
            return Main;
        if (!user.IsInRole("admin"))
            throw new DriveUnavailableException("Only admins can open disks.", StatusCodes.Status403Forbidden);
        if (!plugins.IsInstalled("disks"))
            throw new DriveUnavailableException("This feature is not installed.", StatusCodes.Status404NotFound);
        var s = settings.Get(drive!) ?? throw new DriveUnavailableException("Disk not found.", StatusCodes.Status404NotFound);
        var mount = disks.MountOf(s.FsId)
            ?? throw new DriveUnavailableException("The disk is not mounted. Open it from the drive list first.");
        var root = new VaultRoot(disks.PathOf(s.FsId), readOnly: s.ReadOnly || mount.ReadOnly);
        return new DriveContext(s.FsId, false, root, new FileBrowser(root), new FileOperations(root, clock),
            new Archives(root), new UploadCommitter(root));
    }

    /// <summary>Null while the drive is still there; otherwise why it is not (checked against the kernel's mount table).</summary>
    public string? Missing(DriveContext drive) =>
        drive.IsMain || disks.MountOf(drive.Id) is not null ? null : "The disk was disconnected or unmounted. The transfer was stopped.";

    /// <summary>Disk drives that are mounted now (for housekeeping), each with its read-only state.</summary>
    public IEnumerable<(DiskSettings Settings, VaultRoot Root)> Mounted()
    {
        foreach (var s in settings.All())
        {
            MountTable.Mount? m;
            try { m = disks.MountOf(s.FsId); } catch (VaultPathException) { continue; }
            if (m is null)
                continue;
            VaultRoot root;
            try { root = new VaultRoot(disks.PathOf(s.FsId), readOnly: s.ReadOnly || m.ReadOnly); }
            catch (DirectoryNotFoundException) { continue; }
            yield return (s, root);
        }
    }
}
