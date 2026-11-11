using DiskHaven.Core.Gallery;
using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Tests;

public sealed class MediaIndexTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diskhaven-gallery-").FullName;

    public MediaIndexTests()
    {
        Write("DCIM/Camera/IMG_20230512_141502.jpg");
        Write("DCIM/Camera/VID_20240101_090000.mp4");
        Write("DCIM/Screenshots/Screenshot_2022-03-04-10-11-12.png");
        Write("WhatsApp/Media/WhatsApp Images/IMG-20210705-WA0001.jpg");
        Write("WhatsApp/Media/WhatsApp Images/.Statuses/status.jpg");   // dot folder: hidden, like Android
        Write("Gizli/.nomedia");
        Write("Gizli/foto.jpg");                                        // .nomedia folder: skipped
        Write("Belgeler/not.txt");                                      // not media
        Write(".diskhaven/trash/x/silinen.jpg");                         // internal: never shown
        Write("kok.jpg");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Write(string rel, string content = "x")
    {
        var full = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<MediaIndex> Scanned(string? file = null)
    {
        var index = new MediaIndex(new VaultRoot(_dir), file);
        Assert.True(await index.ScanAsync(TestContext.Current.CancellationToken));
        return index;
    }

    [Fact]
    public async Task Every_folder_with_media_is_an_album_and_skipped_folders_stay_out()
    {
        var index = await Scanned();
        var albums = index.Albums(new HashSet<string>(), []).Select(a => a.Path).Order().ToList();
        Assert.Equal(["", "DCIM/Camera", "DCIM/Screenshots", "WhatsApp/Media/WhatsApp Images"], albums);
        Assert.Equal(5, index.Count);
        Assert.Null(index.Find("Gizli/foto.jpg"));
        Assert.Null(index.Find(".diskhaven/trash/x/silinen.jpg"));
    }

    [Fact]
    public async Task Album_counts_videos_and_root_has_a_name()
    {
        var index = await Scanned();
        var albums = index.Albums(new HashSet<string>(), []);
        var camera = albums.Single(a => a.Path == "DCIM/Camera");
        Assert.Equal(2, camera.Count);
        Assert.Equal(1, camera.Videos);
        Assert.Equal("Camera", camera.Name);
        Assert.Equal("Ana klasör", albums.Single(a => a.Path == "").Name);
    }

    [Fact]
    public async Task Hidden_albums_are_flagged_and_left_out_of_the_photos_tab_but_open_on_their_own()
    {
        var index = await Scanned();
        var hidden = new HashSet<string> { "DCIM/Screenshots" };
        Assert.True(index.Albums(hidden, []).Single(a => a.Path == "DCIM/Screenshots").Hidden);
        var (total, items) = index.Media(null, hidden, 0, 100);
        Assert.Equal(4, total);
        Assert.DoesNotContain(items, i => i.Album == "DCIM/Screenshots");
        Assert.Equal(1, index.Media("DCIM/Screenshots", hidden, 0, 100).Total);
    }

    [Fact]
    public async Task Pinned_albums_come_first_in_the_given_order()
    {
        var index = await Scanned();
        var albums = index.Albums(new HashSet<string>(), ["WhatsApp/Media/WhatsApp Images", "DCIM/Screenshots"]);
        Assert.Equal("WhatsApp/Media/WhatsApp Images", albums[0].Path);
        Assert.Equal("DCIM/Screenshots", albums[1].Path);
        Assert.True(albums[0].Pinned);
    }

    [Fact]
    public async Task Photos_are_sorted_newest_first_by_the_date_in_their_name()
    {
        var index = await Scanned();
        var (_, items) = index.Media(null, new HashSet<string>(), 0, 100);
        var named = items.Where(i => i.Path != "kok.jpg").Select(i => i.Name).ToList();
        Assert.Equal(["VID_20240101_090000.mp4", "IMG_20230512_141502.jpg", "Screenshot_2022-03-04-10-11-12.png", "IMG-20210705-WA0001.jpg"], named);
    }

    [Theory]
    [InlineData("IMG_20230512_141502.jpg", 2023, 5, 12, 14, 15, 2)]
    [InlineData("VID-20230512-WA0003.mp4", 2023, 5, 12, 12, 0, 0)]
    [InlineData("Screenshot_2022-03-04-10-11-12.png", 2022, 3, 4, 10, 11, 12)]
    [InlineData("PXL_20230512_111502123.jpg", 2023, 5, 12, 11, 15, 2)]
    public void Dates_in_android_file_names_are_read(string name, int y, int mo, int d, int h, int mi, int s)
    {
        var ms = MediaIndex.FromName(name);
        Assert.NotNull(ms);
        var local = DateTimeOffset.FromUnixTimeMilliseconds(ms!.Value).ToLocalTime().DateTime;
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), local);
    }

    [Theory]
    [InlineData("tatil.jpg")]
    [InlineData("IMG_99999999.jpg")]
    [InlineData("foto_20991231.jpg")]   // in the future: not a capture date
    public void Names_without_a_real_date_give_nothing(string name) => Assert.Null(MediaIndex.FromName(name));

    [Fact]
    public async Task Forget_drops_deleted_files_and_folders_at_once()
    {
        var index = await Scanned();
        index.Forget(["DCIM"]);
        Assert.Null(index.Find("DCIM/Camera/IMG_20230512_141502.jpg"));
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public async Task Index_is_saved_and_a_rescan_notices_new_and_removed_files()
    {
        var file = Path.Combine(_dir, ".diskhaven", "gallery.json");
        await Scanned(file);
        var reloaded = new MediaIndex(new VaultRoot(_dir), file);
        Assert.Equal(5, reloaded.Count);

        Write("DCIM/Camera/IMG_20250101_000001.jpg");
        File.Delete(Path.Combine(_dir, "kok.jpg"));
        await reloaded.ScanAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded.Find("DCIM/Camera/IMG_20250101_000001.jpg"));
        Assert.Null(reloaded.Find("kok.jpg"));
    }

    [Fact]
    public async Task Scanning_never_changes_the_files()
    {
        var before = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Order()
            .Select(f => (f, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length)).ToList();
        await Scanned();
        var after = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Order()
            .Select(f => (f, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length)).ToList();
        Assert.Equal(before, after);
    }
}
