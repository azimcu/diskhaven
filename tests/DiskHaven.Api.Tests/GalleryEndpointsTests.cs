using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DiskHaven.Api.Tests;

/// <summary>DiskHaven Gallery album tree (18.11): only ticked folders are albums; the first use keeps every album that was visible.</summary>
public sealed partial class ApiEndpointsTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1];

    private void Photo(string relative)
    {
        var p = Path.Combine(_factory.DataRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, Jpeg);
    }

    private async Task<JsonElement> Get(string url)
    {
        using var r = await _admin.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).Clone();
    }

    private async Task<string[]> VisibleAlbums() =>
        (await Get("/api/gallery/albums")).GetProperty("albums").EnumerateArray()
            .Where(a => !a.GetProperty("hidden").GetBoolean()).Select(a => a.GetProperty("path").GetString()!).ToArray();

    [Fact]
    public async Task Gallery_album_tree_keeps_visible_albums_and_ticks_folders_one_layer_at_a_time()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var r = await _admin.PostAsJsonAsync("/api/admin/plugins/gallery", new { installed = true }, ct))
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        try
        {
            Photo("GaleriTest/Tatil/a.jpg");
            Photo("GaleriTest/Tatil/2025/b.jpg");
            Photo("GaleriTest/Belgeler/c.jpg");
            // the old way: every folder with photos is an album unless hidden
            using (var r = await _admin.PutAsJsonAsync("/api/gallery/settings", new { hidden = new[] { "GaleriTest/Belgeler" } }, ct))
                Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
            using (var r = await _admin.PostAsync("/api/gallery/scan", null, ct))
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);

            // first use: the albums that were visible become the ticked ones, the hidden one is not ticked
            var visible = await VisibleAlbums();
            Assert.Contains("GaleriTest/Tatil", visible);
            Assert.Contains("GaleriTest/Tatil/2025", visible);
            Assert.DoesNotContain("GaleriTest/Belgeler", visible);

            var tree = (await Get("/api/gallery/tree")).GetProperty("folders").EnumerateArray()
                .ToDictionary(f => f.GetProperty("path").GetString()!, f => (Count: f.GetProperty("count").GetInt32(), Selected: f.GetProperty("selected").GetBoolean()));
            Assert.Equal((0, false), tree["GaleriTest"]);            // a folder above the albums: in the tree so it can be opened
            Assert.Equal((1, true), tree["GaleriTest/Tatil"]);       // its own photo only, not its subfolder's
            Assert.Equal((1, true), tree["GaleriTest/Tatil/2025"]);
            Assert.Equal((1, false), tree["GaleriTest/Belgeler"]);

            // untick a parent: its subfolder stays an album (layers are independent)
            using (var r = await _admin.PostAsJsonAsync("/api/gallery/albums/select", new { remove = new[] { "GaleriTest/Tatil" }, add = new[] { "GaleriTest/Belgeler" } }, ct))
                Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
            visible = await VisibleAlbums();
            Assert.DoesNotContain("GaleriTest/Tatil", visible);
            Assert.Contains("GaleriTest/Tatil/2025", visible);
            Assert.Contains("GaleriTest/Belgeler", visible);

            // the Photos tab shows only ticked albums
            var photos = (await Get("/api/gallery/media?limit=2000")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()).ToArray();
            Assert.DoesNotContain("GaleriTest/Tatil/a.jpg", photos);
            Assert.Contains("GaleriTest/Tatil/2025/b.jpg", photos);
            Assert.Contains("GaleriTest/Belgeler/c.jpg", photos);

            // an album lists only its own layer
            var album = (await Get("/api/gallery/media?album=GaleriTest%2FTatil")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()).ToArray();
            Assert.Equal(["GaleriTest/Tatil/a.jpg"], album);

            // a later old-style "hidden" save no longer overrides the ticks
            using (var r = await _admin.PutAsJsonAsync("/api/gallery/settings", new { hidden = Array.Empty<string>() }, ct))
                Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
            Assert.DoesNotContain("GaleriTest/Tatil", await VisibleAlbums());
        }
        finally
        {
            Directory.Delete(Path.Combine(_factory.DataRoot, "GaleriTest"), recursive: true);
            using var r = await _admin.PostAsJsonAsync("/api/admin/plugins/gallery", new { installed = false }, ct);
        }
    }
}
