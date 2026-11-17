using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiskHaven.Core.Storage;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace DiskHaven.Core.Gallery;

/// <summary>One photo or video. Paths are vault-relative ("DCIM/Camera/IMG_1.jpg"); times are Unix milliseconds.</summary>
public sealed record MediaItem(string Path, string Album, string Name, long Size, long Modified, long Taken, bool Video);

/// <summary>A folder that directly contains photos or videos, shown as an album like an Android gallery does.</summary>
public sealed record Album(string Path, string Name, int Count, int Videos, string Cover, long Latest, bool Hidden, bool Pinned);

/// <summary>
/// Android-style media index: every folder that directly holds photos or videos is an album (its subfolders are albums
/// of their own), folders holding a ".nomedia" file are skipped, and hidden or internal items never appear.
/// The index only reads the vault: it never writes, moves or deletes a user file. It is saved to the config folder so a
/// restart does not re-read every file; a rescan only opens files that are new or changed.
/// </summary>
public sealed class MediaIndex
{
    public static readonly IReadOnlySet<string> ImageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".heic", ".heif", ".avif", ".bmp", ".tif", ".tiff", ".dng" };
    public static readonly IReadOnlySet<string> VideoTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".3gp", ".avi" };

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly VaultRoot _root;
    private readonly string? _file;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _scan = new(1, 1);
    private Dictionary<string, MediaItem> _items = new(StringComparer.Ordinal);

    public DateTimeOffset? LastScan { get; private set; }
    public bool Scanning { get; private set; }

    public MediaIndex(VaultRoot root, string? indexFile = null)
    {
        _root = root;
        _file = indexFile;
        if (_file is not null && File.Exists(_file))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<MediaItem>>(File.ReadAllText(_file), Json) ?? [];
                _items = list.ToDictionary(i => i.Path, StringComparer.Ordinal);
            }
            catch (JsonException) { /* a damaged index is rebuilt by the next scan */ }
        }
    }

    public static bool IsMedia(string name) => IsVideo(name) || ImageTypes.Contains(System.IO.Path.GetExtension(name));
    public static bool IsVideo(string name) => VideoTypes.Contains(System.IO.Path.GetExtension(name));

    public int Count { get { lock (_lock) return _items.Count; } }

    /// <summary>Walks the vault and brings the index up to date. Returns false when another scan is already running.</summary>
    public async Task<bool> ScanAsync(CancellationToken ct = default)
    {
        if (!await _scan.WaitAsync(0, ct))
            return false;
        Scanning = true;
        try
        {
            await Task.Run(() => Scan(ct), ct);
            return true;
        }
        finally
        {
            Scanning = false;
            _scan.Release();
        }
    }

    private void Scan(CancellationToken ct)
    {
        Dictionary<string, MediaItem> old;
        lock (_lock) old = _items;
        var found = new Dictionary<string, MediaItem>(StringComparer.Ordinal);
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(_root.FullPath));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            FileSystemInfo[] children;
            try { children = dir.GetFileSystemInfos(); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }
            if (children.Any(c => c.Name.Equals(".nomedia", StringComparison.OrdinalIgnoreCase)))
                continue;
            var atRoot = dir.FullName.TrimEnd(System.IO.Path.DirectorySeparatorChar) == _root.FullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar);
            foreach (var c in children)
            {
                if (c.LinkTarget is not null || (atRoot && VaultRoot.IsInternalName(c.Name)) || HiddenFlag.IsHidden(c))
                    continue;
                if (c is DirectoryInfo sub)
                {
                    pending.Push(sub);
                    continue;
                }
                if (c is not FileInfo f || !IsMedia(f.Name))
                    continue;
                var rel = _root.ToRelative(f.FullName);
                var size = f.Length;
                var modified = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                if (old.TryGetValue(rel, out var known) && known.Size == size && known.Modified == modified)
                {
                    found[rel] = known;
                    continue;
                }
                var album = rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";
                found[rel] = new MediaItem(rel, album, f.Name, size, modified, TakenAt(f.FullName, f.Name, modified), IsVideo(f.Name));
            }
        }
        lock (_lock) _items = found;
        LastScan = DateTimeOffset.UtcNow;
        Save();
    }

    private void Save()
    {
        if (_file is null)
            return;
        List<MediaItem> list;
        lock (_lock) list = _items.Values.ToList();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
        File.Move(tmp, _file, overwrite: true);
    }

    /// <summary>Every folder that directly holds photos or videos, with how many.</summary>
    public IReadOnlyDictionary<string, int> AlbumCounts()
    {
        lock (_lock)
            return _items.Values.GroupBy(i => i.Album, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    /// <summary>Albums, newest first; pinned albums lead. Hidden albums are included and flagged.</summary>
    public IReadOnlyList<Album> Albums(ISet<string> hidden, IList<string> pinned)
    {
        List<MediaItem> all;
        lock (_lock) all = _items.Values.ToList();
        return all.GroupBy(i => i.Album, StringComparer.Ordinal)
            .Select(g =>
            {
                var newest = g.MaxBy(i => i.Taken)!;
                return new Album(g.Key, g.Key.Length == 0 ? "Ana klasör" : g.Key[(g.Key.LastIndexOf('/') + 1)..], g.Count(),
                    g.Count(i => i.Video), newest.Path, newest.Taken, hidden.Contains(g.Key), pinned.Contains(g.Key));
            })
            .OrderBy(a => a.Pinned ? pinned.IndexOf(a.Path) : int.MaxValue)
            .ThenByDescending(a => a.Latest)
            .ToList();
    }

    /// <summary>Media of one album (exactly that folder), or of every visible album when <paramref name="album"/> is null.
    /// Newest first.</summary>
    public (int Total, IReadOnlyList<MediaItem> Items) Media(string? album, ISet<string> hidden, int offset, int limit)
    {
        List<MediaItem> all;
        lock (_lock) all = _items.Values.ToList();
        var q = album is null
            ? all.Where(i => !hidden.Contains(i.Album))
            : all.Where(i => i.Album == album);
        var sorted = q.OrderByDescending(i => i.Taken).ThenBy(i => i.Path, StringComparer.Ordinal).ToList();
        return (sorted.Count, sorted.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 2000)).ToList());
    }

    public MediaItem? Find(string path)
    {
        string key;
        try { key = VaultRoot.Normalize(path); }
        catch (VaultPathException) { return null; }
        lock (_lock) return _items.GetValueOrDefault(key);
    }

    /// <summary>Drops paths that no longer exist right away (after a delete or move), before the next full scan.</summary>
    public void Forget(IEnumerable<string> paths)
    {
        lock (_lock)
        {
            var next = new Dictionary<string, MediaItem>(_items, StringComparer.Ordinal);
            foreach (var p in paths.Select(VaultRoot.Normalize))
            {
                next.Remove(p);
                foreach (var k in next.Keys.Where(k => k.StartsWith(p + "/", StringComparison.Ordinal)).ToList())
                    next.Remove(k);
            }
            _items = next;
        }
    }

    // ---- capture time -------------------------------------------------------------------------------------------

    // IMG_20230512_141502.jpg, VID-20230512-WA0003.mp4, Screenshot_2023-05-12-14-15-02.png, PXL_20230512_111502123.jpg
    private static readonly Regex NameDate = new(
        @"(?<!\d)(?<y>(?:19|20)\d{2})[-_.]?(?<m>0[1-9]|1[0-2])[-_.]?(?<d>0[1-9]|[12]\d|3[01])(?:[-_ T.]?(?<H>[01]\d|2[0-3])[-_.:]?(?<M>[0-5]\d)[-_.:]?(?<S>[0-5]\d))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>When the photo or video was taken: the camera's own date (EXIF / QuickTime), else a date in the file
    /// name (Android and WhatsApp style), else the file's modification time.</summary>
    public static long TakenAt(string fullPath, string name, long modified)
    {
        var fromFile = TryMetadata(fullPath);
        if (fromFile is { } m)
            return m;
        return FromName(name) ?? modified;
    }

    public static long? FromName(string name)
    {
        var match = NameDate.Match(name);
        if (!match.Success)
            return null;
        int G(string k, int fallback) => match.Groups[k].Success ? int.Parse(match.Groups[k].Value, CultureInfo.InvariantCulture) : fallback;
        try
        {
            var local = new DateTime(G("y", 0), G("m", 1), G("d", 1), G("H", 12), G("M", 0), G("S", 0), DateTimeKind.Unspecified);
            if (local > DateTime.Now.AddDays(2))
                return null;
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeMilliseconds();
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static long? TryMetadata(string fullPath)
    {
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(fullPath);
            foreach (var d in dirs.OfType<ExifSubIfdDirectory>())
                if (d.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt) && Plausible(dt))
                    return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(dt)).ToUnixTimeMilliseconds();
            foreach (var d in dirs.OfType<QuickTimeMovieHeaderDirectory>())
                if (d.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var dt) && Plausible(dt))
                    return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            foreach (var d in dirs.OfType<ExifIfd0Directory>())
                if (d.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt) && Plausible(dt))
                    return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(dt)).ToUnixTimeMilliseconds();
        }
        catch (Exception e) when (e is ImageProcessingException or IOException or UnauthorizedAccessException or ArgumentException
                                      or InvalidOperationException or OverflowException or FormatException or IndexOutOfRangeException) { }
        return null;
    }

    private static bool Plausible(DateTime dt) => dt.Year >= 1990 && dt <= DateTime.Now.AddDays(2);
}
