using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiskHaven.Api.Tests;

/// <summary>
/// Disk drives through the real API. A "disk" is a folder under the factory's disks folder; it counts as mounted
/// only when the fake mount table lists it, with the read-only flag the test chooses. Registration is written to
/// disks.json like the server does. No real disk and no disk agent are involved.
/// </summary>
public sealed partial class ApiEndpointsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Lock MountLock = new();

    /// <summary>Registers a disk in config/disks.json and, when <paramref name="mounted"/>, lists it in the mount table.</summary>
    private string AddDisk(bool settingReadOnly, bool? mounted = true, bool kernelReadOnly = false, string diskId = "")
    {
        var id = "test-" + Guid.NewGuid().ToString("N")[..12];
        var dir = Path.Combine(_factory.DisksDir, id);
        Directory.CreateDirectory(dir);
        lock (MountLock)
        {
            var file = Path.Combine(_factory.ConfigDir, "disks.json");
            var list = File.Exists(file) ? JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(file))! : [];
            var entry = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["FsId"] = id, ["DiskId"] = diskId, ["Name"] = "Test " + id[5..9], ["Folders"] = Array.Empty<string>(),
                ["Schedule"] = new Dictionary<string, object?> { ["Enabled"] = false, ["Time"] = "03:00", ["LastRun"] = null },
                ["After"] = "StayOn", ["LastSync"] = null, ["ReadOnly"] = settingReadOnly,
            });
            list.Add(entry);
            File.WriteAllText(file, JsonSerializer.Serialize(list));
            if (mounted == true)
                File.AppendAllText(_factory.MountInfo,
                    $"40 25 8:33 / {dir} {(kernelReadOnly ? "ro" : "rw")},nosuid,nodev,noexec,noatime shared:1 - ext4 /dev/sdz1 rw\n");
        }
        return id;
    }

    private string DiskPath(string id, string rel = "") => Path.Combine(_factory.DisksDir, id, rel);

    private static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    private async Task<HttpStatusCode> Status(Task<HttpResponseMessage> call)
    {
        using var r = await call;
        return r.StatusCode;
    }

    [Fact]
    public async Task Unknown_drive_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, await Status(_admin.GetAsync("/api/files?drive=yok-boyle-disk", Ct)));
    }

    [Fact]
    public async Task Disk_that_is_not_mounted_is_refused_and_nothing_lands_on_the_main_disk()
    {
        var id = AddDisk(settingReadOnly: false, mounted: false);
        Assert.Equal(HttpStatusCode.Conflict, await Status(_admin.GetAsync($"/api/files?drive={id}", Ct)));
        Assert.Equal(HttpStatusCode.Conflict, await Status(_admin.PostAsJsonAsync("/api/files/folder",
            new { parent = "", name = "Yeni", drive = id }, Ct)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(DiskPath(id)));
    }

    [Fact]
    public async Task Read_only_disk_can_be_read_but_never_changed()
    {
        var id = AddDisk(settingReadOnly: true);
        Directory.CreateDirectory(DiskPath(id, "Arşiv/2019"));
        File.WriteAllText(DiskPath(id, "Arşiv/2019/not.txt"), "eski");
        var before = Snapshot(DiskPath(id));

        using (var r = await _admin.GetAsync($"/api/files?drive={id}&path=Ar%C5%9Fiv", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("2019", (await Json(r)).GetProperty("entries")[0].GetProperty("name").GetString());
        }
        using (var r = await _admin.GetAsync($"/api/files/content?drive={id}&path=Ar%C5%9Fiv/2019/not.txt", Ct))
            Assert.Equal("eski", await r.Content.ReadAsStringAsync(Ct));

        var refused = new[]
        {
            _admin.PostAsJsonAsync("/api/files/folder", new { parent = "", name = "Yeni", drive = id }, Ct),
            _admin.PostAsJsonAsync("/api/files/rename", new { path = "Arşiv", newName = "B", drive = id }, Ct),
            _admin.PostAsJsonAsync("/api/files/move", new { paths = new[] { "Arşiv/2019/not.txt" }, destination = "", drive = id }, Ct),
            _admin.PostAsJsonAsync("/api/files/copy", new { paths = new[] { "Arşiv/2019/not.txt" }, destination = "", drive = id }, Ct),
            _admin.PostAsJsonAsync("/api/files/delete", new { paths = new[] { "Arşiv" }, drive = id }, Ct),
            _admin.PostAsJsonAsync("/api/files/hidden", new { paths = new[] { "Arşiv" }, hidden = true, drive = id }, Ct),
            _admin.PostAsJsonAsync("/api/files/compress", new { paths = new[] { "Arşiv" }, drive = id }, Ct),
            _admin.DeleteAsync($"/api/trash?drive={id}", Ct),
            _admin.PostAsJsonAsync("/api/transfers", new { fromDrive = "", paths = new[] { "x" }, toDrive = id, destination = "", move = false }, Ct),
        };
        foreach (var call in refused)
            Assert.Equal(HttpStatusCode.Conflict, await Status(call));
        Assert.Equal(before, Snapshot(DiskPath(id)));
        Assert.False(Directory.Exists(DiskPath(id, ".diskhaven")));
    }

    [Fact]
    public async Task Kernel_read_only_wins_over_a_writable_setting()
    {
        var id = AddDisk(settingReadOnly: false, kernelReadOnly: true);
        Assert.Equal(HttpStatusCode.Conflict, await Status(_admin.PostAsJsonAsync("/api/files/folder",
            new { parent = "", name = "Yeni", drive = id }, Ct)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(DiskPath(id)));
    }

    [Fact]
    public async Task Writable_disk_works_like_the_main_storage_with_its_own_recycle_bin()
    {
        var id = AddDisk(settingReadOnly: false);
        using (var r = await _admin.PostAsJsonAsync("/api/files/folder", new { parent = "", name = "Belgeler", drive = id }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        File.WriteAllText(DiskPath(id, "Belgeler/a.txt"), "disk");
        using (var r = await _admin.PostAsJsonAsync("/api/files/rename", new { path = "Belgeler/a.txt", newName = "b.txt", drive = id }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("disk", File.ReadAllText(DiskPath(id, "Belgeler/b.txt")));

        var mainTrashBefore = (await Json(await _admin.GetAsync("/api/trash", Ct))).GetArrayLength();
        using (var r = await _admin.PostAsJsonAsync("/api/files/delete", new { paths = new[] { "Belgeler/b.txt" }, drive = id }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(File.Exists(DiskPath(id, "Belgeler/b.txt")));
        // the item is in the disk's own recycle bin, on the disk, not in the main one
        Assert.Equal(mainTrashBefore, (await Json(await _admin.GetAsync("/api/trash", Ct))).GetArrayLength());
        var trash = await Json(await _admin.GetAsync($"/api/trash?drive={id}", Ct));
        var item = Assert.Single(trash.EnumerateArray());
        Assert.True(Directory.Exists(DiskPath(id, ".diskhaven/trash")));
        using (var r = await _admin.PostAsync($"/api/trash/{item.GetProperty("id").GetString()}/restore?drive={id}", null, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("disk", File.ReadAllText(DiskPath(id, "Belgeler/b.txt")));

        // DiskHaven's own folder on the disk stays hidden and reserved
        var names = (await Json(await _admin.GetAsync($"/api/files?drive={id}", Ct))).GetProperty("entries")
            .EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Belgeler"], names);
        Assert.Equal(HttpStatusCode.BadRequest, await Status(_admin.GetAsync($"/api/files?drive={id}&path=.diskhaven", Ct)));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(_admin.GetAsync($"/api/files?drive={id}&path=../", Ct)));
    }

    [Fact]
    public async Task Only_admins_see_and_open_disks()
    {
        var id = AddDisk(settingReadOnly: false);
        var userName = NewName("aile");
        using (var r = await _admin.PostAsJsonAsync("/api/users", new { userName, password = "aile-parolasi-123", isAdmin = false }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using var user = NewClient();
        using (var r = await user.PostAsJsonAsync("/api/auth/login", new { userName, password = "aile-parolasi-123" }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        // nothing is shared with this person: every drive answers like one that does not exist
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.GetAsync($"/api/files?drive={id}", Ct)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.PostAsJsonAsync("/api/files/folder", new { parent = "", name = "x", drive = id }, Ct)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.PostAsJsonAsync("/api/transfers",
            new { fromDrive = "", paths = new[] { "x" }, toDrive = id, destination = "", move = false }, Ct)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.GetAsync("/api/files", Ct)));
        var drives = await Json(await user.GetAsync("/api/drives", Ct));
        Assert.Equal(0, drives.GetArrayLength());

        // an admin's transfer is neither visible to nor stoppable by another user
        using (var r = await _admin.PostAsJsonAsync("/api/transfers", new { fromDrive = "", paths = new[] { await CreateFolder(NewName("Is")) }, toDrive = id, destination = "", move = false }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(await user.GetAsync("/api/transfers/job", Ct))).ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.PostAsync("/api/transfers/job/cancel", null, Ct)));
        await WaitForTransfer();
    }

    [Fact]
    public async Task Upload_into_a_disk_lands_on_the_disk_verified()
    {
        var id = AddDisk(settingReadOnly: false);
        var data = new byte[3 * 1024 * 1024 + 17];
        Random.Shared.NextBytes(data);
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));

        // tus: create, send all bytes in one PATCH, then the verified commit
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/tus");
        create.Headers.Add("Tus-Resumable", "1.0.0");
        create.Headers.Add("Upload-Length", data.Length.ToString());
        using var created = await _admin.SendAsync(create, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var location = created.Headers.Location!.ToString();
        var uploadId = location.TrimEnd('/').Split('/')[^1];
        using var patch = new HttpRequestMessage(HttpMethod.Patch, location) { Content = new ByteArrayContent(data) };
        patch.Headers.Add("Tus-Resumable", "1.0.0");
        patch.Headers.Add("Upload-Offset", "0");
        patch.Content.Headers.ContentType = new("application/offset+octet-stream");
        using (var r = await _admin.SendAsync(patch, Ct))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        using (var r = await _admin.PostAsJsonAsync($"/api/uploads/{uploadId}/commit",
            new { destination = "", relativeName = "Videolar/tatil.mp4", sha256 = sha, lastModified = 1_600_000_000_000L, drive = id }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("Videolar/tatil.mp4", (await Json(r)).GetProperty("path").GetString());
        }
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(DiskPath(id, "Videolar/tatil.mp4")))));
        Assert.False(File.Exists(Path.Combine(_factory.DataRoot, "Videolar", "tatil.mp4")));
        Assert.Empty(Directory.EnumerateFiles(DiskPath(id, ".diskhaven/tmp")));
    }

    private async Task<JsonElement> WaitForTransfer()
    {
        for (var i = 0; i < 200; i++)
        {
            var job = await Json(await _admin.GetAsync("/api/transfers/job", Ct));
            if (job.ValueKind == JsonValueKind.Object && job.GetProperty("status").GetString() != "Running")
                return job;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException("transfer did not finish");
    }

    [Fact]
    public async Task Transfer_copies_main_to_disk_and_moves_disk_to_main_verified()
    {
        var a = AddDisk(settingReadOnly: false);
        var folder = NewName("Aktarim");
        Directory.CreateDirectory(Path.Combine(_factory.DataRoot, folder, "Alt"));
        for (var i = 0; i < 5; i++)
        {
            var b = new byte[100_000 * (i + 1)];
            Random.Shared.NextBytes(b);
            File.WriteAllBytes(Path.Combine(_factory.DataRoot, folder, i % 2 == 0 ? "Alt" : "", $"dosya{i}.bin"), b);
        }
        var source = Snapshot(Path.Combine(_factory.DataRoot, folder));

        using (var r = await _admin.PostAsJsonAsync("/api/transfers", new { fromDrive = "", paths = new[] { folder }, toDrive = a, destination = "", move = false }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var job = await WaitForTransfer();
        Assert.Equal("Done", job.GetProperty("status").GetString());
        Assert.Equal(source, Snapshot(DiskPath(a, folder)));
        Assert.Equal(source, Snapshot(Path.Combine(_factory.DataRoot, folder)));   // copy: source untouched

        // move it back into a new main folder: arrives intact, the disk's copy goes to the disk's recycle bin
        var back = await CreateFolder(NewName("Geri"));
        using (var r = await _admin.PostAsJsonAsync("/api/transfers", new { fromDrive = a, paths = new[] { folder }, toDrive = "", destination = back, move = true }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        job = await WaitForTransfer();
        Assert.Equal("Done", job.GetProperty("status").GetString());
        Assert.Equal(source, Snapshot(Path.Combine(_factory.DataRoot, back, folder)));
        Assert.False(Directory.Exists(DiskPath(a, folder)));
        Assert.Single((await Json(await _admin.GetAsync($"/api/trash?drive={a}", Ct))).EnumerateArray());
    }

    [Fact]
    public async Task Drive_list_shows_main_and_needs_no_agent()
    {
        var drives = await Json(await _admin.GetAsync("/api/drives", Ct));
        Assert.Equal("main", drives[0].GetProperty("kind").GetString());
    }
}
