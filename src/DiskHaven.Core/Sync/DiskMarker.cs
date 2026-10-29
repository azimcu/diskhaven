using System.Text.Json;

namespace DiskHaven.Core.Sync;

/// <summary>
/// Small file at the root of every DiskHaven backup disk. Identifies the disk regardless of
/// USB port or device name, and stops a sync from writing to the wrong disk.
/// </summary>
public sealed record DiskMarker(string DiskId, string Name, DateTimeOffset AdoptedAt)
{
    public const string FileName = ".diskhaven-disk.json";
    /// <summary>Name used before the project was renamed from HomeVault (2026-10); renamed on first read.</summary>
    public const string LegacyFileName = ".homevault-disk.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static DiskMarker? Read(string diskRoot)
    {
        var file = Path.Combine(diskRoot, FileName);
        MigrateLegacy(diskRoot);
        if (!File.Exists(file))
            return null;
        try { return JsonSerializer.Deserialize<DiskMarker>(File.ReadAllText(file)); }
        catch (JsonException) { return null; }
    }

    /// <summary>Disks adopted under the old name keep working: marker and trash folder get the new names.</summary>
    public static void MigrateLegacy(string diskRoot)
    {
        try
        {
            var old = Path.Combine(diskRoot, LegacyFileName);
            if (File.Exists(old) && !File.Exists(Path.Combine(diskRoot, FileName)))
                File.Move(old, Path.Combine(diskRoot, FileName));
            var oldTrash = Path.Combine(diskRoot, SyncExecutor.LegacyTrashFolder);
            if (Directory.Exists(oldTrash) && !Directory.Exists(Path.Combine(diskRoot, SyncExecutor.TrashFolder)))
                Directory.Move(oldTrash, Path.Combine(diskRoot, SyncExecutor.TrashFolder));
        }
        catch (IOException) { /* read-only disk: keep using it as it is */ }
        catch (UnauthorizedAccessException) { }
    }

    public void Write(string diskRoot)
    {
        var file = Path.Combine(diskRoot, FileName);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, file, overwrite: true);
    }
}
