using System.Text.Json;

namespace DiskHaven.Core.Sync;

/// <summary>
/// Backup jobs in config/backups.json, plus the run history of each job.
/// Settings saved before backup jobs existed (a disk with assigned folders and a daily sync) are turned
/// into equivalent main-storage → disk jobs the first time this file is read, so the owner keeps the
/// backup that was already set up. Neither the disk settings nor anything on a disk is changed by that.
/// </summary>
public sealed class BackupStore(string configDir)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _file = Path.Combine(configDir, "backups.json");
    private readonly string _history = Path.Combine(configDir, "backup-history");
    private readonly Lock _lock = new();

    public IReadOnlyList<BackupJob> All()
    {
        lock (_lock) return Load();
    }

    public BackupJob? Get(string id) => All().FirstOrDefault(j => j.Id == id);

    public void Save(BackupJob job)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(j => j.Id == job.Id);
            list.Add(job);
            Write(list);
        }
    }

    public void Update(string id, Func<BackupJob, BackupJob> change)
    {
        lock (_lock)
        {
            var list = Load();
            var i = list.FindIndex(j => j.Id == id);
            if (i < 0)
                throw new KeyNotFoundException("Backup job not found.");
            list[i] = change(list[i]);
            Write(list);
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(j => j.Id == id);
            Write(list);
        }
        if (!IsPlainId(id))
            return;
        var dir = Path.Combine(_history, id);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Job ids are generated names; anything else (a path from a crafted URL) never reaches the file system.</summary>
    private static bool IsPlainId(string id) =>
        id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public void AddReport(string id, SyncReport report)
    {
        if (!IsPlainId(id))
            throw new KeyNotFoundException("Backup job not found.");
        var dir = Path.Combine(_history, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{report.Started:yyyyMMdd-HHmmss}.json"), JsonSerializer.Serialize(report, Json));
        foreach (var old in Directory.GetFiles(dir, "*.json").Order().Reverse().Skip(100))
            File.Delete(old);
    }

    public IReadOnlyList<SyncReport> History(string id, int take = 20)
    {
        if (!IsPlainId(id))
            return [];
        var dir = Path.Combine(_history, id);
        if (!Directory.Exists(dir))
            return [];
        return Directory.GetFiles(dir, "*.json").Order().Reverse().Take(take)
            .Select(f => JsonSerializer.Deserialize<SyncReport>(File.ReadAllText(f), Json)!)
            .ToList();
    }

    private List<BackupJob> Load()
    {
        if (File.Exists(_file))
            return JsonSerializer.Deserialize<List<BackupJob>>(File.ReadAllText(_file), Json) ?? [];
        // first read after the upgrade: keep every backup the owner had already configured
        var migrated = Migrate();
        Write(migrated);
        return migrated;
    }

    private List<BackupJob> Migrate()
    {
        IReadOnlyList<DiskSettings> disks;
        try { disks = new DiskSettingsStore(configDir).All(); }
        catch (JsonException) { return []; }
        var now = DateTimeOffset.UtcNow;
        return disks
            .Where(d => d.Folders.Count > 0 && d.DiskId.Length > 0)
            .Select(d => new BackupJob(
                Guid.NewGuid().ToString("N"),
                $"{d.Name} yedeği",
                SourceDrive: "",
                d.Folders,
                TargetDrive: d.FsId,
                TargetFolder: "",
                d.Schedule.Enabled,
                string.IsNullOrWhiteSpace(d.Schedule.Time) ? "03:00" : d.Schedule.Time,
                DeleteThresholdPercent: 50,
                CreatedAt: now,
                LastRun: d.LastSync is { } s
                    ? new BackupSummary(s.Finished, s.Success, s.Canceled, false, s.Copied, s.Updated, s.Deleted,
                        0, s.Errors, s.BytesCopied, 0, s.Message)
                    : null,
                LastSuccessAt: d.LastSync is { Success: true } ok ? ok.Finished : null,
                Legacy: true,
                AdoptExisting: true))   // the folders on that disk belong to this backup already
            .ToList();
    }

    private void Write(List<BackupJob> list)
    {
        Directory.CreateDirectory(configDir);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        File.Move(temp, _file, overwrite: true);
    }
}
