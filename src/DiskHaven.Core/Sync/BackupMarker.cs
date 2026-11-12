using System.Text.Json;
using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Sync;

/// <summary>
/// Written into the target folder of a backup job before anything else is copied. It says which job
/// this folder belongs to; a job refuses to write (and so refuses to delete) in a folder that carries
/// another job's marker, or in a non-empty folder that has never been adopted by a job. A wrong drive
/// letter or a mistake in the settings can therefore not empty the wrong folder.
/// </summary>
public sealed record BackupMarker(string JobId, string Name, string SourceDrive, DateTimeOffset CreatedAt)
{
    public const string FileName = ".diskhaven-backup.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static BackupMarker? Read(string folder)
    {
        var file = Path.Combine(folder, FileName);
        if (!File.Exists(file))
            return null;
        try { return JsonSerializer.Deserialize<BackupMarker>(File.ReadAllText(file)); }
        catch (JsonException) { return null; }
    }

    public void Write(string folder)
    {
        var file = Path.Combine(folder, FileName);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>True when the folder holds something a user would care about (anything but DiskHaven's own files).</summary>
    public static bool HasUserContent(string folder)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            if (!VaultRoot.IsInternalName(Path.GetFileName(path)))
                return true;
        return false;
    }
}
