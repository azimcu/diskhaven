using System.Text.Json;

namespace HomeVault.Core.Sync;

/// <summary>
/// Small file at the root of every HomeVault backup disk. Identifies the disk regardless of
/// USB port or device name, and stops a sync from writing to the wrong disk.
/// </summary>
public sealed record DiskMarker(string DiskId, string Name, DateTimeOffset AdoptedAt)
{
    public const string FileName = ".homevault-disk.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static DiskMarker? Read(string diskRoot)
    {
        var file = Path.Combine(diskRoot, FileName);
        if (!File.Exists(file))
            return null;
        try { return JsonSerializer.Deserialize<DiskMarker>(File.ReadAllText(file)); }
        catch (JsonException) { return null; }
    }

    public void Write(string diskRoot)
    {
        var file = Path.Combine(diskRoot, FileName);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, file, overwrite: true);
    }
}
