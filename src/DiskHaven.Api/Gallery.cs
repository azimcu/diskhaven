using System.Collections.Concurrent;
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

    /// <summary>
    /// Folders chosen in the album tree (only these are albums; each shows only its own files, not its subfolders').
    /// Null until the tree is first used: then every album that was visible becomes selected, so nothing disappears.
    /// </summary>
    public HashSet<string>? Selected => plugins.Settings(Id)["albums"] is JsonArray ? Read("albums").ToHashSet(StringComparer.Ordinal) : null;

    /// <summary>Selected albums, creating the list from the old hidden-album setting the first time (after a scan).</summary>
    public HashSet<string>? EnsureSelected(MediaIndex index)
    {
        var sel = Selected;
        if (sel is not null || index.LastScan is null)
            return sel;
        var hidden = Hidden;
        sel = index.AlbumCounts().Keys.Where(a => !hidden.Contains(a)).ToHashSet(StringComparer.Ordinal);
        SaveAlbums(sel);
        return sel;
    }

    /// <summary>What the gallery leaves out: unselected albums (or, before the tree is used, the old hidden list).</summary>
    public HashSet<string> EffectiveHidden(MediaIndex index)
    {
        var sel = EnsureSelected(index);
        if (sel is null)
            return Hidden;
        return index.AlbumCounts().Keys.Where(a => !sel.Contains(a)).ToHashSet(StringComparer.Ordinal);
    }

    public void SaveAlbums(IEnumerable<string> albums)
    {
        var s = plugins.Settings(Id);
        s["albums"] = new JsonArray(albums.Select(VaultRoot.Normalize).Distinct().Order(StringComparer.Ordinal).Select(x => (JsonNode)x!).ToArray());
        plugins.SaveSettings(Id, s);
    }

    private List<string> Read(string key) =>
        plugins.Settings(Id)[key] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0 || key is "hidden" or "albums").Distinct().ToList() : [];

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
    public bool PdfAvailable { get; }

    private static readonly IReadOnlySet<string> AudioTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".flac" };
    public static bool IsPdf(string name) => Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    public static bool IsAudio(string name) => AudioTypes.Contains(Path.GetExtension(name));

    /// <summary>Files the explorer shows a small picture of: photos, videos (a frame), PDFs (the first page)
    /// and music (the album cover inside the file, when it has one).</summary>
    public static bool CanPreview(string name) => MediaIndex.IsMedia(name) || IsPdf(name) || IsAudio(name);

    private bool ToolFor(string full) =>
        IsPdf(full) ? PdfAvailable : MediaIndex.IsVideo(full) || IsAudio(full) ? FfmpegAvailable : VipsAvailable;

    public Thumbnails(string configDir, ILogger<Thumbnails> log)
    {
        _cache = Path.Combine(configDir, "gallery", "thumbs");
        _log = log;
        VipsAvailable = OnPath("vipsthumbnail");
        FfmpegAvailable = OnPath("ffmpeg");
        PdfAvailable = OnPath("pdftoppm");
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => d.Length > 0 && File.Exists(Path.Combine(d, tool)));

    // Previews someone is looking at right now always go first; the warm-up queue (a folder's remaining
    // photos, made ahead so scrolling down finds them ready) only runs while nobody is waiting.
    private int _interactive;
    private readonly System.Threading.Channels.Channel<string> _warm =
        System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(50_000)
            { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _queued = new();

    /// <summary>Queues grid previews ("s") to be made in the background. Already made or queued ones are skipped.</summary>
    public int Warm(IEnumerable<string> fullPaths)
    {
        var n = 0;
        foreach (var full in fullPaths)
        {
            if (!CanPreview(full) || !ToolFor(full) || SpecialFiles.IsSpecial(full))
                continue;
            var target = Target(full, "s");
            if (target is null || File.Exists(target) || !_queued.TryAdd(full, 0))
                continue;
            if (_warm.Writer.TryWrite(full)) n++;
            else _queued.TryRemove(full, out _);
        }
        return n;
    }

    /// <summary>Runs by the <see cref="ThumbnailWarmer"/>: one preview at a time, pausing while previews are being asked for.</summary>
    public async Task WarmLoopAsync(CancellationToken ct)
    {
        await foreach (var full in _warm.Reader.ReadAllAsync(ct))
        {
            try
            {
                while (Volatile.Read(ref _interactive) > 0)
                    await Task.Delay(250, ct);
                await MakeAsync(full, "s", ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { _log.LogDebug(e, "Preview warm-up failed for {Path}", full); }
            finally { _queued.TryRemove(full, out _); }
        }
    }

    public int Queued => _queued.Count;

    private string? Target(string full, string variant)
    {
        var info = new FileInfo(full);
        if (!info.Exists)
            return null;
        var key = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{variant}")));
        return Path.Combine(_cache, key[..2], key + ".jpg");
    }

    /// <summary>"s" = 360 px square for grids, "m" = up to 1600 px for the viewer. Null when no preview can be made.</summary>
    public async Task<string?> GetAsync(string full, string variant, CancellationToken ct)
    {
        Interlocked.Increment(ref _interactive);
        try { return await MakeAsync(full, variant, ct); }
        finally { Interlocked.Decrement(ref _interactive); }
    }

    private async Task<string?> MakeAsync(string full, string variant, CancellationToken ct)
    {
        var info = new FileInfo(full);
        // a FIFO / device named .jpg would block the header read below forever: only regular files get a preview
        if (!info.Exists || SpecialFiles.IsSpecial(full))
            return null;
        var key = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{full}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{variant}")));
        var target = Path.Combine(_cache, key[..2], key + ".jpg");
        if (File.Exists(target))
            return target;
        if (!CanPreview(full) || !ToolFor(full))
            return null;
        var video = MediaIndex.IsVideo(full);
        var pdf = IsPdf(full);
        var audio = IsAudio(full);
        // stored files come from anyone with an account (or a drop-box link): only hand real photo / video / PDF / music
        // files to the native decoders, never an SVG, playlist or script renamed to .jpg / .mp4 / .pdf
        if (pdf ? !LooksLikePdf(full) : audio ? !LooksLikeAudio(full) : !LooksLikeMedia(full, video))
            return null;
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target))
                return target;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".tmp.jpg";
            var ok = pdf ? await PdfPage(full, tmp, variant, ct)
                : audio ? await AudioCover(full, tmp, variant, ct)
                : video ? await VideoFrame(full, tmp, variant, ct) : await Photo(full, tmp, variant, ct);
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
        if (SpecialFiles.IsSpecial(full))
            return false;   // a FIFO or device named .jpg would block the read below forever
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

    /// <summary>"%PDF-" within the first kilobyte (where the standard allows it to start).</summary>
    public static bool LooksLikePdf(string full)
    {
        if (SpecialFiles.IsSpecial(full))
            return false;
        var h = new byte[1024];
        int n;
        try { using var f = File.OpenRead(full); n = f.ReadAtLeast(h, h.Length, throwOnEndOfStream: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        return h.AsSpan(0, n).IndexOf("%PDF-"u8) >= 0;
    }

    /// <summary>MP3 (ID3 tag or a frame header), MP4 audio (ISO BMFF) or FLAC.</summary>
    public static bool LooksLikeAudio(string full)
    {
        if (SpecialFiles.IsSpecial(full))
            return false;
        var h = new byte[12];
        int n;
        try { using var f = File.OpenRead(full); n = f.ReadAtLeast(h, 12, throwOnEndOfStream: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        if (n < 12)
            return false;
        return h.AsSpan(0, 3).SequenceEqual("ID3"u8) || (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0)
               || h.AsSpan(4, 4).SequenceEqual("ftyp"u8) || h.AsSpan(0, 4).SequenceEqual("fLaC"u8);
    }

    // the first page, as a JPEG, by poppler (no fonts or scripts are run: it only draws the page)
    private async Task<bool> PdfPage(string full, string output, string variant, CancellationToken ct)
    {
        var stem = output[..^4];   // pdftoppm adds ".jpg" itself
        var ok = await Run("pdftoppm", ["-f", "1", "-l", "1", "-singlefile", "-jpeg", "-jpegopt", "quality=80",
            "-scale-to", variant == "s" ? "360" : "1600", full, stem], ct);
        return ok && File.Exists(output);
    }

    // the cover picture stored inside a music file; none = no preview (the explorer shows the icon)
    private async Task<bool> AudioCover(string full, string output, string variant, CancellationToken ct)
    {
        var scale = variant == "s" ? "scale=360:360:force_original_aspect_ratio=increase,crop=360:360" : "scale='min(1600,iw)':-2";
        return await Run("ffmpeg", ["-v", "error", "-y", "-protocol_whitelist", "file", "-format_whitelist", "mp3,mov,mp4,m4a,flac",
                "-i", "file:" + full, "-map", "0:v:0", "-frames:v", "1", "-vf", scale, "-q:v", "4", output], ct)
            && File.Exists(output) && new FileInfo(output).Length > 0;
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

/// <summary>Makes the queued previews in the background (see <see cref="Thumbnails.Warm"/>).</summary>
public sealed class ThumbnailWarmer(Thumbnails thumbs) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => thumbs.WarmLoopAsync(stoppingToken);
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

public sealed record GallerySelectRequest(string[]? Add, string[]? Remove);
public sealed record GallerySettingsRequest(string[]? Hidden, string[]? Pinned, string? NewAlbumParent);
public sealed record GalleryForgetRequest(string[] Paths);

public static class GalleryEndpoints
{
    public static void MapGallery(this WebApplication app)
    {
        // the gallery shows the main storage as a whole (the admin's photos): admins only for now; a person a disk is
        // shared with sees their photos in the explorer, which has the full-screen viewer too
        var g = app.MapGroup("/api/gallery").RequireAuthorization("admin").RequiresPlugin(GallerySettings.Id);

        g.MapGet("/albums", (MediaIndex index, GallerySettings settings) =>
            Results.Ok(new
            {
                albums = index.Albums(settings.EffectiveHidden(index), settings.Pinned),
                scanning = index.Scanning,
                lastScan = index.LastScan,
                newAlbumParent = settings.NewAlbumParent,
            }));

        // album omitted = all photos of the visible albums (the "Photos" tab)
        g.MapGet("/media", (string? album, int? offset, int? limit, MediaIndex index, GallerySettings settings) =>
        {
            var (total, items) = index.Media(album is null ? null : VaultRoot.Normalize(album), settings.EffectiveHidden(index), offset ?? 0, limit ?? 300);
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

        // album tree: every folder that holds media (directly or deeper down), with its own count and whether it is an album
        g.MapGet("/tree", (MediaIndex index, GallerySettings settings) =>
        {
            var counts = index.AlbumCounts();
            var sel = settings.EnsureSelected(index) ?? counts.Keys.Where(a => !settings.Hidden.Contains(a)).ToHashSet(StringComparer.Ordinal);
            var folders = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var (path, n) in counts)
            {
                folders[path] = n;
                for (var p = path; p.Contains('/');)   // the folders above it, so the tree can be opened down to it
                {
                    p = p[..p.LastIndexOf('/')];
                    folders.TryAdd(p, 0);
                }
                if (path.Length > 0) folders.TryAdd("", counts.GetValueOrDefault(""));
            }
            return Results.Ok(new
            {
                folders = folders.Select(f => new { path = f.Key, count = f.Value, selected = sel.Contains(f.Key) }),
                scanning = index.Scanning,
            });
        });

        // tick / untick one or more folders in the album tree
        g.MapPost("/albums/select", (GallerySelectRequest req, MediaIndex index, GallerySettings settings) =>
        {
            var sel = settings.EnsureSelected(index) ?? [];
            foreach (var p in req.Add ?? []) sel.Add(VaultRoot.Normalize(p));
            foreach (var p in req.Remove ?? []) sel.Remove(VaultRoot.Normalize(p));
            settings.SaveAlbums(sel);
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
