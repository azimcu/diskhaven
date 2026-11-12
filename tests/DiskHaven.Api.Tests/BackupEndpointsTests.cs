using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DiskHaven.Core.Sync;

namespace DiskHaven.Api.Tests;

/// <summary>
/// Backup jobs through the real API, with a "disk" that is a folder and a fake mount table (no agent, no real
/// hardware). Every run is a one-way mirror: the source is never changed and removed target items land in the
/// target disk's own recycle bin.
/// </summary>
public sealed partial class ApiEndpointsTests
{
    private (string Id, string DiskId) AddBackupDisk(bool readOnly = false)
    {
        var diskId = "id-" + Guid.NewGuid().ToString("N")[..12];
        var id = AddDisk(settingReadOnly: readOnly, diskId: diskId);
        File.WriteAllText(DiskPath(id, DiskMarker.FileName),
            JsonSerializer.Serialize(new DiskMarker(diskId, "Test yedek", DateTimeOffset.UtcNow)));
        return (id, diskId);
    }

    private async Task<string> CreateBackup(string[] folders, string target, string targetFolder, bool adopt = false)
    {
        using var r = await _admin.PostAsJsonAsync("/api/backups", new
        {
            name = NewName("Yedek"),
            sourceDrive = "",
            sourceFolders = folders,
            targetDrive = target,
            targetFolder,
            enabled = false,
            time = "03:00",
            deleteThresholdPercent = 50,
            adoptExisting = adopt,
        }, Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> RunBackup(string id, bool approve = false)
    {
        using (var r = await _admin.PostAsJsonAsync($"/api/backups/{id}/run", new { approveDeletes = approve }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await WaitForBackup();
    }

    private async Task<JsonElement> WaitForBackup()
    {
        for (var i = 0; i < 400; i++)
        {
            var job = await Json(await _admin.GetAsync("/api/backups/job", Ct));
            if (job.ValueKind == JsonValueKind.Object && job.GetProperty("status").GetString() != "Running")
                return job;
            await Task.Delay(25, Ct);
        }
        throw new TimeoutException("backup did not finish");
    }

    [Fact]
    public async Task Backup_mirrors_main_to_a_disk_and_removed_target_items_go_to_its_own_trash()
    {
        var (disk, _) = AddBackupDisk();
        var folder = NewName("Kaynak");
        Directory.CreateDirectory(Path.Combine(_factory.DataRoot, folder, "Alt"));
        File.WriteAllText(Path.Combine(_factory.DataRoot, folder, "a.txt"), "bir");
        File.WriteAllText(Path.Combine(_factory.DataRoot, folder, "Alt", "b.txt"), "iki");
        var source = Snapshot(Path.Combine(_factory.DataRoot, folder));

        var jobId = await CreateBackup([folder], disk, "Yedek");
        var job = await RunBackup(jobId);
        Assert.Equal("Done", job.GetProperty("status").GetString());
        Assert.Equal("bir", File.ReadAllText(DiskPath(disk, $"Yedek/{folder}/a.txt")));
        Assert.Equal("iki", File.ReadAllText(DiskPath(disk, $"Yedek/{folder}/Alt/b.txt")));
        Assert.Equal(source, Snapshot(Path.Combine(_factory.DataRoot, folder)));   // one-way: source untouched
        Assert.True(File.Exists(DiskPath(disk, "Yedek/.diskhaven-backup.json")));

        // change one file and remove the other in the source: the old target versions go to the disk's recycle bin
        File.WriteAllText(Path.Combine(_factory.DataRoot, folder, "a.txt"), "yeni");
        File.Delete(Path.Combine(_factory.DataRoot, folder, "Alt", "b.txt"));
        job = await RunBackup(jobId);
        Assert.Equal("Done", job.GetProperty("status").GetString());
        Assert.Equal("yeni", File.ReadAllText(DiskPath(disk, $"Yedek/{folder}/a.txt")));
        Assert.False(File.Exists(DiskPath(disk, $"Yedek/{folder}/Alt/b.txt")));
        var trash = await Json(await _admin.GetAsync($"/api/trash?drive={disk}", Ct));
        var items = trash.EnumerateArray().ToList();
        var names = items.Select(e => e.GetProperty("name").GetString()).Order().ToArray();
        Assert.Equal(["a.txt", "b.txt"], names);
        var oldA = items.Single(e => e.GetProperty("name").GetString() == "a.txt");
        Assert.Equal("bir", File.ReadAllText(DiskPath(disk, $".diskhaven/trash/{oldA.GetProperty("id").GetString()}/item")));
    }

    [Fact]
    public async Task Empty_source_never_deletes_anything_on_the_target()
    {
        var (disk, _) = AddBackupDisk();
        var folder = NewName("Bosalan");
        Directory.CreateDirectory(Path.Combine(_factory.DataRoot, folder));
        File.WriteAllText(Path.Combine(_factory.DataRoot, folder, "tek.txt"), "koru");
        var jobId = await CreateBackup([folder], disk, "Yedek");
        Assert.Equal("Done", (await RunBackup(jobId)).GetProperty("status").GetString());

        File.Delete(Path.Combine(_factory.DataRoot, folder, "tek.txt"));   // folder stays, empty
        var job = await RunBackup(jobId);
        Assert.Equal("Done", job.GetProperty("status").GetString());
        Assert.Equal("koru", File.ReadAllText(DiskPath(disk, $"Yedek/{folder}/tek.txt")));
        Assert.Empty((await Json(await _admin.GetAsync($"/api/trash?drive={disk}", Ct))).EnumerateArray());
    }

    [Fact]
    public async Task A_folder_that_belongs_to_another_backup_is_never_written_to()
    {
        var (disk, _) = AddBackupDisk();
        var folder = NewName("Kaynak");
        Directory.CreateDirectory(Path.Combine(_factory.DataRoot, folder));
        File.WriteAllText(Path.Combine(_factory.DataRoot, folder, "yeni.txt"), "yeni");
        Directory.CreateDirectory(DiskPath(disk, "Ortak"));
        File.WriteAllText(DiskPath(disk, "Ortak/eski.txt"), "dokunma");
        new BackupMarker("baska-bir-is", "Başka yedek", "", DateTimeOffset.UtcNow).Write(DiskPath(disk, "Ortak"));

        var jobId = await CreateBackup([folder], disk, "Ortak");
        var job = await RunBackup(jobId);
        Assert.Equal("Failed", job.GetProperty("status").GetString());
        Assert.Contains("başka bir yedeğe ait", job.GetProperty("error").GetString());
        Assert.Equal("dokunma", File.ReadAllText(DiskPath(disk, "Ortak/eski.txt")));
        Assert.False(File.Exists(DiskPath(disk, $"Ortak/{folder}/yeni.txt")));
    }

    [Fact]
    public async Task Read_only_target_is_refused_and_nothing_is_written()
    {
        var (disk, _) = AddBackupDisk(readOnly: true);
        var folder = NewName("Kaynak");
        Directory.CreateDirectory(Path.Combine(_factory.DataRoot, folder));
        File.WriteAllText(Path.Combine(_factory.DataRoot, folder, "a.txt"), "a");

        var jobId = await CreateBackup([folder], disk, "Yedek");
        var job = await RunBackup(jobId);
        Assert.Equal("Failed", job.GetProperty("status").GetString());
        Assert.Contains("salt okunur", job.GetProperty("error").GetString());
        Assert.False(Directory.Exists(DiskPath(disk, "Yedek")));
    }

    [Fact]
    public async Task Removing_most_of_the_target_stops_and_waits_for_approval()
    {
        var (disk, _) = AddBackupDisk();
        var folder = NewName("CokSil");
        Directory.CreateDirectory(Path.Combine(_factory.DataRoot, folder));
        for (var i = 1; i <= 4; i++)
            File.WriteAllText(Path.Combine(_factory.DataRoot, folder, $"d{i}.txt"), $"icerik{i}");
        var jobId = await CreateBackup([folder], disk, "Yedek");
        Assert.Equal("Done", (await RunBackup(jobId)).GetProperty("status").GetString());
        var before = Snapshot(DiskPath(disk, "Yedek"));

        for (var i = 2; i <= 4; i++)
            File.Delete(Path.Combine(_factory.DataRoot, folder, $"d{i}.txt"));
        File.WriteAllText(Path.Combine(_factory.DataRoot, folder, "yeni.txt"), "yeni");

        var job = await RunBackup(jobId);
        Assert.Equal("AwaitingApproval", job.GetProperty("status").GetString());
        Assert.Contains("Onaylarsan", job.GetProperty("message").GetString());
        Assert.Equal(before, Snapshot(DiskPath(disk, "Yedek")));   // nothing was deleted

        job = await RunBackup(jobId, approve: true);
        Assert.Equal("Done", job.GetProperty("status").GetString());
        Assert.True(File.Exists(DiskPath(disk, $"Yedek/{folder}/d1.txt")));
        Assert.True(File.Exists(DiskPath(disk, $"Yedek/{folder}/yeni.txt")));
        Assert.False(File.Exists(DiskPath(disk, $"Yedek/{folder}/d2.txt")));
        Assert.Equal(3, (await Json(await _admin.GetAsync($"/api/trash?drive={disk}", Ct))).GetArrayLength());
    }

    [Fact]
    public async Task Backup_jobs_are_admin_only_and_two_jobs_cannot_share_a_target()
    {
        var (disk, _) = AddBackupDisk();
        var folder = NewName("Kaynak");
        Directory.CreateDirectory(Path.Combine(_factory.DataRoot, folder));
        var jobId = await CreateBackup([folder], disk, "Tek");

        using var refusal = await _admin.PostAsJsonAsync("/api/backups", new
        {
            name = "İkinci", sourceDrive = "", sourceFolders = new[] { folder }, targetDrive = disk,
            targetFolder = "Tek", enabled = false, time = "03:00", deleteThresholdPercent = 50, adoptExisting = false,
        }, Ct);
        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);

        var userName = NewName("aile");
        using (var r = await _admin.PostAsJsonAsync("/api/users", new { userName, password = "aile-parolasi-123", isAdmin = false }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var user = NewClient();
        using (var r = await user.PostAsJsonAsync("/api/auth/login", new { userName, password = "aile-parolasi-123" }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await user.GetAsync("/api/backups", Ct))
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);

        using (var r = await _admin.DeleteAsync($"/api/backups/{jobId}", Ct))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.DoesNotContain((await Json(await _admin.GetAsync("/api/backups", Ct))).GetProperty("jobs").EnumerateArray().ToList(),
            j => j.GetProperty("id").GetString() == jobId);
    }
}
