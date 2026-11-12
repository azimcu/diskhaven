using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DiskHaven.Core.Gallery;
using DiskHaven.Core.Storage;

namespace DiskHaven.Api;

/// <summary>
/// Gallery plugin: an Android-style photo and video gallery over the vault's own folders. Albums are folders, so
/// creating, moving, copying and deleting in the gallery are ordinary file operations (the existing /api/files and
/// recycle bin); this file adds only what a gallery needs on top: the media index, thumbnails and album settings.
/// </summary>
public sealed class GallerySettings(PluginStore plugins)
{
    public const string Id = "gallery";

    public HashSet<string> Hidden => Read("hidden").ToHashSet(StringComparer.Ordinal);
    public List<string> Pinned => Read("pinned");
    public string NewAlbumParent => plugins.Settings(Id)["newAlbumParent"]?.GetValue<string>() ?? "DCIM";

    private List<string> Read(string key) =>
        plugins.Settings(Id)[key] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0 || key == "hidden").Distinct().ToList() : [];

    public void Save(IEnumerable<string>? hidden, IEnumerable<string>? pinned, string? newAlbumParent)
    {
        var s = plugins.Settings(Id);
        if (hidden is not null) s["hidden"] = new JsonArray(hidden.Select(VaultRoot.Normalize).Distinct().Select(x => (JsonNode)x!).ToArray());
        if (pinned is not null) s["pinned"] = new JsonArray(pinned.Select(VaultRoot.Normalize).Distinct().Select(x => (JsonNode)x!).ToArray());
        if (newAlbumParent is not null) s["newAlbumParent"] = VaultRoot.Normalize(newAlbumParent);
        plugins.SaveSettings(Id, s);
    }
}

/// <summary>Makes and caches small JPEG previews with vipsthumbnail (photos, including HEIC) and ffmpeg (videos).</summary>
public sealed class Thumbnails
{
    private readonly string _cache;
    private readonly SemaphoreSlim _gate = new(3, 3);
    private readonly ILogger<Thumbnails> _log;
    public bool VipsAvailable { get; }
    public bool FfmpegAvailable { get; }

    public Thumbnails(string configDir, ILogger<Thumbnails> log)
    {
        _cache = Path.Combine(configDir, "gallery", "thumbs");
        _log = log;
        VipsAvailable = OnPath("vipsthumbnail");
        FfmpegAvailable = OnPath("ffmpeg");
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => d.Length > 0 && File.Exists(Path.Combine(d, tool)));

    /// <summary>"s" = 360 px square for grids, "m" = up to 1600 px for the viewer. Null when no preview can be made.</summary>
    public async Task<string?> GetAsync(string full, string variant, CancellationToken ct)
    {
        var info = new FileInfo(full);
        if (!info.Exists)
            return null;
        var key = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{variant}")));
        var target = Path.Combine(_cache, key[..2], key + ".jpg");
        if (File.Exists(target))
            return target;
        var video = MediaIndex.IsVideo(full);
        if (video ? !FfmpegAvailable : !VipsAvailable)
            return null;
        // stored files come from anyone with an account (or a drop-box link): only hand real photo / video
        // containers to the native decoders, never an SVG, PDF, playlist or script renamed to .jpg / .mp4
        if (!LooksLikeMedia(full, video))
            return null;
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target))
                return target;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".tmp.jpg";
            var ok = video ? await VideoFrame(full, tmp, variant, ct) : await Photo(full, tmp, variant, ct);
            if (!ok || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                File.Delete(tmp);
                return null;
            }
            File.Move(tmp, target, overwrite: true);
            return target;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Checks the file's first bytes: JPEG, PNG, GIF, WebP, BMP, TIFF/DNG, HEIF/AVIF for photos;
    /// MP4/MOV/3GP (ISO BMFF), Matroska/WebM, AVI for videos.</summary>
    public static bool LooksLikeMedia(string full, bool video)
    {
        var h = new byte[16];
        int n;
        try { using var f = File.OpenRead(full); n = f.ReadAtLeast(h, 16, throwOnEndOfStream: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        if (n < 12)
            return false;
        bool At(int i, ReadOnlySpan<byte> m) => h.AsSpan(i, m.Length).SequenceEqual(m);
        var isoBmff = At(4, "ftyp"u8);
        var riff = At(0, "RIFF"u8);
        if (video)
            return isoBmff || At(4, "moov"u8) || At(4, "mdat"u8) || At(4, "wide"u8) || At(4, "free"u8)
                   || At(0, [0x1A, 0x45, 0xDF, 0xA3]) || (riff && At(8, "AVI "u8));
        return At(0, [0xFF, 0xD8, 0xFF]) || At(0, [0x89, 0x50, 0x4E, 0x47]) || At(0, "GIF8"u8) || (riff && At(8, "WEBP"u8))
               || At(0, "BM"u8) || At(0, [0x49, 0x49, 0x2A, 0x00]) || At(0, [0x4D, 0x4D, 0x00, 0x2A]) || isoBmff;
    }

    private Task<bool> Photo(string full, string output, string variant, CancellationToken ct) =>
        variant == "s"
            ? Run("vipsthumbnail", [full, "--size", "360x360", "--smartcrop", "attention", "-o", output + "[Q=78,strip]"], ct)
            : Run("vipsthumbnail", [full, "--size", "1600x1600>", "-o", output + "[Q=84,strip]"], ct);

    private async Task<bool> VideoFrame(string full, string output, string variant, CancellationToken ct)
    {
        var scale = variant == "s"
            ? "scale=360:360:force_original_aspect_ratio=increase,crop=360:360"
            : "scale='min(1600,iw)':-2";
        foreach (var at in new[] { "1", "0" })
            if (await Run("ffmpeg", ["-v", "error", "-y", "-protocol_whitelist", "file",
                    "-format_whitelist", "mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi", "-ss", at, "-i", "file:" + full, "-frames:v", "1", "-vf", scale, "-q:v", "4", output], ct)
                && File.Exists(output) && new FileInfo(output).Length > 0)
                return true;
        return false;
    }

    private async Task<bool> Run(string tool, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["VIPS_CONCURRENCY"] = "1";
        psi.Environment["VIPS_BLOCK_UNTRUSTED"] = "1";   // no ImageMagick / SVG / PDF / matrix loaders
        using var p = Process.Start(psi);
        if (p is null)
            return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var err = p.StandardError.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            if (p.ExitCode != 0)
                _log.LogDebug("{Tool} failed: {Error}", tool, (await err).Trim());
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return false;
        }
    }
}

/// <summary>Keeps the media index fresh while the gallery plugin is installed: at start-up and every few minutes.</summary>
public sealed class GalleryScanner(MediaIndex index, PluginStore plugins, ILogger<GalleryScanner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (plugins.IsInstalled(GallerySettings.Id))
            {
                try { await index.ScanAsync(stoppingToken); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { log.LogWarning(e, "Gallery scan failed"); }
            }
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        }
    }
}

public sealed record GallerySettingsRequest(string[]? Hidden, string[]? Pinned, string? NewAlbumParent);
public sealed record GalleryForgetRequest(string[] Paths);

public static class GalleryEndpoints
{
    public static void MapGallery(this WebApplication app)
    {
        var g = app.MapGroup("/api/gallery").RequireAuthorization().RequiresPlugin(GallerySettings.Id);

        g.MapGet("/albums", (MediaIndex index, GallerySettings settings) =>
            Results.Ok(new
            {
                albums = index.Albums(settings.Hidden, settings.Pinned),
                scanning = index.Scanning,
                lastScan = index.LastScan,
                newAlbumParent = settings.NewAlbumParent,
            }));

        // album omitted = all photos of the visible albums (the "Photos" tab)
        g.MapGet("/media", (string? album, int? offset, int? limit, MediaIndex index, GallerySettings settings) =>
        {
            var (total, items) = index.Media(album is null ? null : VaultRoot.Normalize(album), settings.Hidden, offset ?? 0, limit ?? 300);
            return Results.Ok(new { total, items });
        });

        g.MapGet("/thumb", async (string path, string? size, HttpContext ctx, VaultRoot root, Thumbnails thumbs, CancellationToken ct) =>
        {
            string full;
            try { full = root.Resolve(path); }
            catch (VaultPathException e) { return Results.Problem(e.Message, statusCode: 400); }
            if (!MediaIndex.IsMedia(full) || !File.Exists(full))
                return Results.NotFound();
            var file = await thumbs.GetAsync(full, size == "m" ? "m" : "s", ct);
            if (file is null)
                return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=604800";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(file, "image/jpeg", lastModified: File.GetLastWriteTimeUtc(file));
        });

        g.MapPost("/scan", async (MediaIndex index, CancellationToken ct) =>
        {
            if (!await index.ScanAsync(ct))
                await Task.Delay(500, ct);   // a scan is already running; the client simply asks again
            return Results.Ok(new { count = index.Count, scanning = index.Scanning });
        });

        // after a delete or move made in the gallery: drop the old paths at once instead of waiting for the next scan
        g.MapPost("/forget", (GalleryForgetRequest req, MediaIndex index) =>
        {
            index.Forget(req.Paths ?? []);
            return Results.NoContent();
        });

        g.MapPut("/settings", (GallerySettingsRequest req, GallerySettings settings) =>
        {
            settings.Save(req.Hidden, req.Pinned, req.NewAlbumParent);
            return Results.NoContent();
        });

        g.MapGet("/status", (MediaIndex index, Thumbnails thumbs) =>
            Results.Ok(new { count = index.Count, scanning = index.Scanning, lastScan = index.LastScan, photoPreviews = thumbs.VipsAvailable, videoPreviews = thumbs.FfmpegAvailable }));
    }
}
