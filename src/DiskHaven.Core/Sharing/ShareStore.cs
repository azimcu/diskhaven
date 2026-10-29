using System.Security.Cryptography;
using System.Text.Json;
using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Sharing;

/// <summary>A read-only link to one file or folder. Anyone with the token can view and download it until it expires.</summary>
public sealed record Share(
    string Id,
    string Token,
    string Path,
    EntryKind Kind,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    int Downloads = 0);

/// <summary>
/// Share links in a small JSON file in the config folder. Tokens are 32 random bytes (base64url),
/// so links cannot be guessed. A link only ever gives read access to the path it was created for.
/// </summary>
public sealed class ShareStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();

    public ShareStore(string configDir, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(configDir);
        _file = System.IO.Path.Combine(configDir, "shares.json");
        _clock = clock ?? TimeProvider.System;
    }

    public Share Create(string path, EntryKind kind, string createdBy, TimeSpan? lifetime)
    {
        var normalized = VaultRoot.Normalize(path);
        if (normalized.Length == 0)
            throw new VaultPathException("The root folder cannot be shared.");
        if (lifetime is { } l && l <= TimeSpan.Zero)
            throw new ArgumentException("Expiry must be in the future.");
        var now = _clock.GetUtcNow();
        var share = new Share(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            Base64Url(RandomNumberGenerator.GetBytes(32)),
            normalized, kind, createdBy, now, lifetime is { } life ? now + life : null);
        lock (_lock)
        {
            var all = Load();
            all.Add(share);
            Save(all);
        }
        return share;
    }

    /// <summary>Links that have not expired, newest first. Expired links are dropped from the file.</summary>
    public IReadOnlyList<Share> List()
    {
        lock (_lock)
        {
            var all = Load();
            var live = all.Where(IsLive).ToList();
            if (live.Count != all.Count)
                Save(live);
            return live.OrderByDescending(s => s.CreatedAt).ToList();
        }
    }

    /// <summary>Returns the share for a token if it exists and has not expired.</summary>
    public Share? Find(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64)
            return null;
        var wanted = System.Text.Encoding.ASCII.GetBytes(token);
        foreach (var s in Load())
        {
            if (CryptographicOperations.FixedTimeEquals(wanted, System.Text.Encoding.ASCII.GetBytes(s.Token)))
                return IsLive(s) ? s : null;
        }
        return null;
    }

    public void Revoke(string id)
    {
        lock (_lock)
        {
            var all = Load();
            if (all.RemoveAll(s => s.Id == id) == 0)
                throw new KeyNotFoundException("Share not found.");
            Save(all);
        }
    }

    public void CountDownload(string id)
    {
        lock (_lock)
        {
            var all = Load();
            var i = all.FindIndex(s => s.Id == id);
            if (i < 0) return;
            all[i] = all[i] with { Downloads = all[i].Downloads + 1 };
            Save(all);
        }
    }

    /// <summary>
    /// Maps a path inside a shared folder ("" = the shared item itself) to a vault path.
    /// The result is always the shared path or below it.
    /// </summary>
    public static string Inside(Share share, string? subPath)
    {
        var sub = VaultRoot.Normalize(subPath);
        if (sub.Length == 0)
            return share.Path;
        if (share.Kind != EntryKind.Folder)
            throw new VaultPathException("Path is outside the share.");
        return share.Path + "/" + sub;
    }

    private bool IsLive(Share s) => s.ExpiresAt is not { } e || e > _clock.GetUtcNow();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private List<Share> Load()
    {
        if (!File.Exists(_file))
            return [];
        return JsonSerializer.Deserialize<List<Share>>(File.ReadAllText(_file), Json) ?? [];
    }

    private void Save(List<Share> shares)
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(shares, Json));
        File.Move(temp, _file, overwrite: true);
    }
}
