using System.Text.Json;

namespace HomeVault.Core.Sync;

public enum AfterSync { PowerOff, Standby, StayOn }

public sealed record SyncSchedule(bool Enabled, string Time, DateOnly? LastRun)
{
    public static readonly SyncSchedule Off = new(false, "03:00", null);
}

public sealed record SyncSummary(DateTimeOffset Finished, bool Success, bool Canceled, int Copied, int Updated, int Deleted,
    int Verified, int Errors, long BytesCopied, string? Message);

/// <summary>Settings of a backup disk HomeVault has adopted. <see cref="FsId"/> is the filesystem UUID.</summary>
public sealed record DiskSettings(
    string FsId,
    string DiskId,
    string Name,
    IReadOnlyList<string> Folders,
    SyncSchedule Schedule,
    AfterSync After,
    SyncSummary? LastSync);

/// <summary>Adopted disks in config/disks.json, plus a history of sync reports per disk.</summary>
public sealed class DiskSettingsStore(string configDir)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
    private readonly string _file = Path.Combine(configDir, "disks.json");
    private readonly string _history = Path.Combine(configDir, "sync-history");
    private readonly Lock _lock = new();

    public IReadOnlyList<DiskSettings> All()
    {
        lock (_lock) return Load();
    }

    public DiskSettings? Get(string fsId) => All().FirstOrDefault(d => d.FsId == fsId);

    public void Save(DiskSettings settings)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(d => d.FsId == settings.FsId);
            list.Add(settings);
            Write(list);
        }
    }

    public void Update(string fsId, Func<DiskSettings, DiskSettings> change)
    {
        lock (_lock)
        {
            var list = Load();
            var i = list.FindIndex(d => d.FsId == fsId);
            if (i < 0)
                throw new KeyNotFoundException("Disk is not adopted.");
            list[i] = change(list[i]);
            Write(list);
        }
    }

    public void Remove(string fsId)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(d => d.FsId == fsId);
            Write(list);
        }
    }

    public void AddReport(string fsId, SyncReport report)
    {
        var dir = Path.Combine(_history, fsId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{report.Started:yyyyMMdd-HHmmss}.json"), JsonSerializer.Serialize(report, Json));
        // keep the last 100 reports per disk
        foreach (var old in Directory.GetFiles(dir, "*.json").Order().Reverse().Skip(100))
            File.Delete(old);
    }

    public IReadOnlyList<SyncReport> History(string fsId, int take = 20)
    {
        var dir = Path.Combine(_history, fsId);
        if (!Directory.Exists(dir))
            return [];
        return Directory.GetFiles(dir, "*.json").Order().Reverse().Take(take)
            .Select(f => JsonSerializer.Deserialize<SyncReport>(File.ReadAllText(f), Json)!)
            .ToList();
    }

    private List<DiskSettings> Load() =>
        File.Exists(_file) ? JsonSerializer.Deserialize<List<DiskSettings>>(File.ReadAllText(_file), Json) ?? [] : [];

    private void Write(List<DiskSettings> list)
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        File.Move(temp, _file, overwrite: true);
    }
}
