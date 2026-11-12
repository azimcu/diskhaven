using DiskHaven.Api;

namespace DiskHaven.Api.Tests;

public sealed class ThumbnailGuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diskhaven-thumb-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name, byte[] head)
    {
        var p = Path.Combine(_dir, name);
        var data = new byte[64];
        head.CopyTo(data, 0);
        System.IO.File.WriteAllBytes(p, data);
        return p;
    }

    [Fact]
    public void Real_photo_and_video_headers_pass()
    {
        Assert.True(Thumbnails.LooksLikeMedia(File("a.jpg", [0xFF, 0xD8, 0xFF, 0xE0]), video: false));
        Assert.True(Thumbnails.LooksLikeMedia(File("a.png", [0x89, 0x50, 0x4E, 0x47]), video: false));
        Assert.True(Thumbnails.LooksLikeMedia(File("a.heic", [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c']), video: false));
        Assert.True(Thumbnails.LooksLikeMedia(File("a.mp4", [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m']), video: true));
        Assert.True(Thumbnails.LooksLikeMedia(File("a.mkv", [0x1A, 0x45, 0xDF, 0xA3]), video: true));
    }

    [Fact]
    public void Renamed_svg_pdf_and_playlists_are_refused()
    {
        Assert.False(Thumbnails.LooksLikeMedia(File("x.jpg", "<svg xmlns=\"http://www.w3.org/2000/svg\">"u8.ToArray()), video: false));
        Assert.False(Thumbnails.LooksLikeMedia(File("x.png", "%PDF-1.7\n%"u8.ToArray()), video: false));
        Assert.False(Thumbnails.LooksLikeMedia(File("x.mp4", "#EXTM3U\n#EXT-X-"u8.ToArray()), video: true));
        Assert.False(Thumbnails.LooksLikeMedia(File("x.mp4", [0xFF, 0xD8, 0xFF, 0xE0]), video: true));
        Assert.False(Thumbnails.LooksLikeMedia(File("x.jpg", [0x1A, 0x45, 0xDF, 0xA3]), video: false));
    }
}
