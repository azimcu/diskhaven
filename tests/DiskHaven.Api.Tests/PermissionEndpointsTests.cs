using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DiskHaven.Api.Tests;

/// <summary>
/// Users and permissions (19.11): admins see and change everything; a user sees only what is shared with them
/// (a whole disk, their own folder, folders shared with Read or Write, the common folder). What they may not see is
/// never listed and answers 404, never "access denied".
/// </summary>
public sealed partial class ApiEndpointsTests
{
    private async Task<(string Name, HttpClient Client)> NewUser(string prefix)
    {
        var userName = NewName(prefix);
        using (var r = await _admin.PostAsJsonAsync("/api/users", new { userName, password = "kullanici-parola-1", isAdmin = false }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var client = NewClient();
        using (var r = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = "kullanici-parola-1" }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (userName, client);
    }

    private async Task Grant(string? drive, string path, string principal, string level, HttpClient? by = null, HttpStatusCode expect = HttpStatusCode.OK)
    {
        using var r = await (by ?? _admin).PutAsJsonAsync("/api/access", new { drive, path, principal, level }, Ct);
        Assert.Equal(expect, r.StatusCode);
    }

    private async Task<List<string>> Names(HttpClient c, string path, string? drive = null)
    {
        using var r = await c.GetAsync($"/api/files?path={Uri.EscapeDataString(path)}{(drive is null ? "" : "&drive=" + drive)}", Ct);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()!).OrderBy(n => n).ToList();
    }

    private void FileAt(string relative, string text = "x")
    {
        var p = Path.Combine(_factory.DataRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private static string Q(string s) => Uri.EscapeDataString(s);

    [Fact]
    public async Task A_folder_shared_to_read_shows_only_the_way_there_and_hides_everything_else()
    {
        var top = NewName("Aile");
        FileAt($"{top}/Paylasilan/not.txt", "paylasilan");
        FileAt($"{top}/Gizli/sir.txt", "gizli");
        FileAt($"{top}/kok.txt", "kok");
        var (name, user) = await NewUser("okur");
        using var _ = user;
        await Grant(null, $"{top}/Paylasilan", name, "Read");

        // the main storage now shows up, but only the way to the shared folder: no other folder, no file next to it
        Assert.Single((await Json(await user.GetAsync("/api/drives", Ct))).EnumerateArray());
        Assert.Equal([top], await Names(user, ""));
        Assert.Equal(["Paylasilan"], await Names(user, top));
        Assert.Equal(["not.txt"], await Names(user, $"{top}/Paylasilan"));
        using (var r = await user.GetAsync($"/api/files/content?path={Q($"{top}/Paylasilan/not.txt")}", Ct))
            Assert.Equal("paylasilan", await r.Content.ReadAsStringAsync(Ct));

        // what is not shared does not exist for them: 404, never 403
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.GetAsync($"/api/files?path={Q($"{top}/Gizli")}", Ct)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.GetAsync($"/api/files/content?path={Q($"{top}/Gizli/sir.txt")}", Ct)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.GetAsync($"/api/files/content?path={Q($"{top}/kok.txt")}", Ct)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.GetAsync($"/api/files/archive?paths={Q($"{top}/Gizli")}", Ct)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.GetAsync($"/api/files/properties?path={Q($"{top}/Gizli")}", Ct)));
        // a folder on the way can be seen but not downloaded as a whole (it holds things they may not see)
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.GetAsync($"/api/files/archive?paths={Q(top)}", Ct)));

        // search walks everything but only returns what they may open
        var hits = (await Json(await user.GetAsync($"/api/files/search?path={Q(top)}&q=t", Ct))).GetProperty("entries")
            .EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToList();
        Assert.Contains($"{top}/Paylasilan/not.txt", hits);
        Assert.DoesNotContain($"{top}/Gizli/sir.txt", hits);
        Assert.DoesNotContain($"{top}/kok.txt", hits);

        // Read is read: no new folder, no rename, no delete, no paste, no public link
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.PostAsJsonAsync("/api/files/folder", new { parent = $"{top}/Paylasilan", name = "yeni" }, Ct)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.PostAsJsonAsync("/api/files/rename", new { path = $"{top}/Paylasilan/not.txt", newName = "b.txt" }, Ct)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.PostAsJsonAsync("/api/files/delete", new { paths = new[] { $"{top}/Paylasilan/not.txt" } }, Ct)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.PostAsJsonAsync("/api/files/copy", new { paths = new[] { $"{top}/Paylasilan/not.txt" }, destination = $"{top}/Paylasilan" }, Ct)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.PostAsJsonAsync("/api/files/delete", new { paths = new[] { $"{top}/Gizli" } }, Ct)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.PostAsJsonAsync("/api/shares", new { path = $"{top}/Paylasilan", days = 1 }, Ct)));
        Assert.True(File.Exists(Path.Combine(_factory.DataRoot, top, "Paylasilan", "not.txt")));

        // the listing tells the explorer what they may do here
        Assert.Equal("Read", (await Json(await user.GetAsync($"/api/files?path={Q($"{top}/Paylasilan")}", Ct))).GetProperty("access").GetString());
        Assert.Equal("None", (await Json(await user.GetAsync($"/api/files?path={Q(top)}", Ct))).GetProperty("access").GetString());
        // and the gallery (the admin's whole photo library) stays closed
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.GetAsync("/api/gallery/albums", Ct)));
    }

    [Fact]
    public async Task A_folder_shared_to_write_can_be_filled_but_the_shared_folder_itself_stays_put()
    {
        var top = NewName("Ortak");
        FileAt($"{top}/Is/eski.txt");
        var (name, user) = await NewUser("yazar");
        using var _ = user;
        await Grant(null, $"{top}/Is", name, "Write");

        using (var r = await user.PostAsJsonAsync("/api/files/folder", new { parent = $"{top}/Is", name = "Yeni" }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await user.PostAsJsonAsync("/api/files/rename", new { path = $"{top}/Is/eski.txt", newName = "yeni.txt" }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await user.PostAsJsonAsync("/api/files/delete", new { paths = new[] { $"{top}/Is/yeni.txt" } }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // they see what they deleted in the recycle bin and can bring it back
        var bin = (await Json(await user.GetAsync("/api/trash", Ct))).EnumerateArray().ToList();
        var mine = Assert.Single(bin, i => i.GetProperty("originalPath").GetString() == $"{top}/Is/yeni.txt");
        using (var r = await user.PostAsync($"/api/trash/{mine.GetProperty("id").GetString()}/restore", null, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        // the shared folder itself: not renamed, not deleted, not moved away by them
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.PostAsJsonAsync("/api/files/rename", new { path = $"{top}/Is", newName = "Baska" }, Ct)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.PostAsJsonAsync("/api/files/delete", new { paths = new[] { $"{top}/Is" } }, Ct)));
        Assert.True(Directory.Exists(Path.Combine(_factory.DataRoot, top, "Is")));
        Assert.Equal("Write", (await Json(await user.GetAsync($"/api/files?path={Q($"{top}/Is")}", Ct))).GetProperty("access").GetString());

        // the admin's own deletions elsewhere stay out of their recycle bin
        var other = await CreateFolder(NewName("AdminSilinen"));
        using (var r = await _admin.PostAsJsonAsync("/api/files/delete", new { paths = new[] { other } }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotContain((await Json(await user.GetAsync("/api/trash", Ct))).EnumerateArray(), i => i.GetProperty("originalPath").GetString() == other);
    }

    [Fact]
    public async Task A_disk_with_user_folders_and_a_common_folder_works_like_a_family_nas()
    {
        var id = AddDisk(settingReadOnly: false);
        var (ali, aliClient) = await NewUser("ali");
        var (ayse, ayseClient) = await NewUser("ayse");
        using var a = aliClient;
        using var b = ayseClient;
        Directory.CreateDirectory(DiskPath(id, "Arsiv"));   // the admin's own folder on the same disk

        using (var r = await _admin.PostAsJsonAsync("/api/access/user-folders", new { drive = id, parent = "", users = new[] { ali, ayse }, common = "Ortak" }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(Directory.Exists(DiskPath(id, ali)));
        Assert.True(Directory.Exists(DiskPath(id, "Ortak")));

        // each one sees their own folder and the common folder, nothing else; the admin sees everything
        Assert.Equal(new[] { ali, "Ortak" }.OrderBy(n => n), await Names(a, "", id));
        Assert.Equal(new[] { ayse, "Ortak" }.OrderBy(n => n), await Names(b, "", id));
        Assert.Equal(new[] { ali, ayse, "Arsiv", "Ortak" }.OrderBy(n => n), await Names(_admin, "", id));
        Assert.Equal(HttpStatusCode.NotFound, await Status(a.GetAsync($"/api/files?drive={id}&path={Q(ayse)}", Ct)));
        Assert.Equal(HttpStatusCode.NotFound, await Status(a.GetAsync($"/api/files?drive={id}&path=Arsiv", Ct)));
        // the drive list shows the disk to them exactly when it shows it to the admin (it needs the disk agent, absent here)
        bool Lists(JsonElement list) => list.EnumerateArray().Any(d => d.GetProperty("id").GetString() == id);
        Assert.Equal(Lists(await Json(await _admin.GetAsync("/api/drives", Ct))), Lists(await Json(await a.GetAsync("/api/drives", Ct))));
        Assert.DoesNotContain((await Json(await a.GetAsync("/api/drives", Ct))).EnumerateArray(), d => d.GetProperty("id").GetString() == "");

        // their folder is theirs; the common folder is everyone's
        using (var r = await a.PostAsJsonAsync("/api/files/folder", new { parent = ali, name = "Belgeler", drive = id }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        using (var r = await b.PostAsJsonAsync("/api/files/folder", new { parent = "Ortak", name = "Tatil", drive = id }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Tatil", await Names(a, "Ortak", id));
        Assert.Equal(HttpStatusCode.NotFound, await Status(b.PostAsJsonAsync("/api/files/folder", new { parent = ali, name = "x", drive = id }, Ct)));
        // nobody but the admin changes the disk's top level
        Assert.Equal(HttpStatusCode.Forbidden, await Status(a.PostAsJsonAsync("/api/files/folder", new { parent = "", name = "x", drive = id }, Ct)));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(a.PostAsJsonAsync("/api/files/rename", new { path = ali, newName = "benim", drive = id }, Ct)));

        // an owner shares inside their folder (Read / Write), but cannot hand out ownership
        await Grant(id, $"{ali}/Belgeler", ayse, "Read", by: a);
        Assert.Contains(ali, await Names(b, "", id));
        Assert.Equal(["Belgeler"], await Names(b, ali, id));
        await Grant(id, $"{ali}/Belgeler", ayse, "Owner", by: a, expect: HttpStatusCode.Forbidden);
        // ...and nothing outside it
        await Grant(id, "Arsiv", ayse, "Read", by: a, expect: HttpStatusCode.NotFound);
        await Grant(id, ayse, ali, "Read", by: a, expect: HttpStatusCode.NotFound);
        // a user who owns nothing here cannot share at all
        await Grant(id, "Ortak", ali, "Read", by: b, expect: HttpStatusCode.Forbidden);

        // "shared with me" lists what others gave
        var mine = (await Json(await b.GetAsync("/api/access/mine", Ct))).EnumerateArray().ToList();
        Assert.Contains(mine, g => g.GetProperty("path").GetString() == $"{ali}/Belgeler" && g.GetProperty("level").GetString() == "Read");
        Assert.Contains(mine, g => g.GetProperty("path").GetString() == ayse && g.GetProperty("level").GetString() == "Owner");
    }

    [Fact]
    public async Task Permissions_follow_a_renamed_folder_and_end_with_a_deleted_one()
    {
        var top = await CreateFolder(NewName("Tasinan"));
        var (name, user) = await NewUser("takip");
        using var _ = user;
        await Grant(null, top, name, "Read");
        Assert.Contains(top, await Names(user, ""));

        var renamed = top + "Yeni";
        using (var r = await _admin.PostAsJsonAsync("/api/files/rename", new { path = top, newName = renamed }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(renamed, await Names(user, ""));

        using (var r = await _admin.PostAsJsonAsync("/api/files/delete", new { paths = new[] { renamed } }, Ct))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        // a new folder that happens to get the same name is not shared by accident
        await CreateFolder(renamed);
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.GetAsync("/api/files", Ct)));
    }

    [Fact]
    public async Task Taking_a_permission_away_and_deleting_the_user_close_the_door()
    {
        var top = await CreateFolder(NewName("Gecici"));
        var (name, user) = await NewUser("gecici");
        using var _ = user;
        await Grant(null, top, name, "Write");
        Assert.Contains(top, await Names(user, ""));
        await Grant(null, top, name, "None");
        Assert.Equal(HttpStatusCode.NotFound, await Status(user.GetAsync($"/api/files?path={Q(top)}", Ct)));

        // everyone ("*") reaches a common folder, also people added later
        await Grant(null, top, "*", "Read");
        Assert.Contains(top, await Names(user, ""));
        await Grant(null, top, "*", "None");

        await Grant(null, top, name, "Read");
        using (var r = await _admin.DeleteAsync($"/api/users/{name}", Ct))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.DoesNotContain((await Json(await _admin.GetAsync($"/api/access?path={Q(top)}", Ct))).GetProperty("own").EnumerateArray(),
            g => g.GetProperty("principal").GetString() == name);
        // unknown people and the share dialog's inputs are checked
        await Grant(null, top, "kimse-yok", "Read", expect: HttpStatusCode.NotFound);
        await Grant(null, NewName("olmayan"), "*", "Read", expect: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Roles_can_change_but_the_last_admin_stays()
    {
        var (name, user) = await NewUser("rol");
        using var _ = user;
        Assert.Equal(HttpStatusCode.Forbidden, await Status(user.GetAsync("/api/users", Ct)));
        using (var r = await _admin.PutAsJsonAsync($"/api/users/{name}/role", new { isAdmin = true }, Ct))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        // a changed role ends the old session: they sign in again and get the new rights
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(user.GetAsync("/api/files", Ct)));
        using (var r = await _admin.PutAsJsonAsync($"/api/users/{name}/role", new { isAdmin = false }, Ct))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        using (var r = await _admin.PutAsJsonAsync($"/api/users/{DiskHavenFactory.UserName}/role", new { isAdmin = false }, Ct))
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);   // not yourself
        Assert.Equal(HttpStatusCode.NotFound, await Status(_admin.PutAsJsonAsync("/api/users/yok-boyle/role", new { isAdmin = true }, Ct)));
    }
}
