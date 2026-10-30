using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskHaven.Core.Accounts;
using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Sharing;

/// <summary>What someone holding the link may do.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ShareMode>))]
public enum ShareMode
{
    /// <summary>Browse, preview and download the shared item.</summary>
    View,
    /// <summary>A drop box: send files into the shared folder without seeing what is in it.</summary>
    UploadOnly,
}

/// <summary>
/// A link to one file or folder. Anyone with the token can use it until it expires; if it has a password,
/// only after entering it. A link never allows changing or deleting what is already there.
/// </summary>
public sealed record Share(
    string Id,
    string Token,
    string Path,
    EntryKind Kind,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    int Downloads = 0,
    ShareMode Mode = ShareMode.View,
    string? PasswordHash = null,
    int Uploads = 0)
{
    public bool HasPassword => PasswordHash is not null;
}

/// <summary>
/// Share links in a small JSON file in the config folder. Tokens are 32 random bytes (base64url),
/// so links cannot be guessed. A link only ever reaches the path it was created for.
/// </summary>
public sealed class ShareStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();
    // signs "password entered" cookies; a restart only means entering the password again
    private readonly byte[] _unlockKey = RandomNumberGenerator.GetBytes(32);
    public const int MinPasswordLength = 4;

    public ShareStore(string configDir, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(configDir);
        _file = System.IO.Path.Combine(configDir, "shares.json");
        _clock = clock ?? TimeProvider.System;
    }

    public Share Create(string path, EntryKind kind, string createdBy, TimeSpan? lifetime,
        ShareMode mode = ShareMode.View, string? password = null)
    {
        var normalized = VaultRoot.Normalize(path);
        if (normalized.Length == 0)
            throw new VaultPathException("The root folder cannot be shared.");
        if (lifetime is { } l && l <= TimeSpan.Zero)
            throw new ArgumentException("Expiry must be in the future.");
        if (mode == ShareMode.UploadOnly && kind != EntryKind.Folder)
            throw new VaultPathException("Only a folder can collect uploads.");
        if (password is { Length: < MinPasswordLength })
            throw new ArgumentException("The link password is too short.");
        var now = _clock.GetUtcNow();
        var share = new Share(
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            Base64Url(RandomNumberGenerator.GetBytes(32)),
            normalized, kind, createdBy, now, lifetime is { } life ? now + life : null,
            Mode: mode, PasswordHash: string.IsNullOrEmpty(password) ? null : PasswordHasher.Hash(password));
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

    public void CountDownload(string id) => Update(id, s => s with { Downloads = s.Downloads + 1 });
    public void CountUpload(string id) => Update(id, s => s with { Uploads = s.Uploads + 1 });

    /// <summary>Checks the link password. On success returns the value to keep in a cookie (see <see cref="IsUnlocked"/>).</summary>
    public string? Unlock(Share share, string password) =>
        share.PasswordHash is { } hash && PasswordHasher.Verify(password, hash) ? UnlockValue(share) : null;

    /// <summary>True when the link has no password, or <paramref name="cookie"/> came from <see cref="Unlock"/> for it.
    /// The value depends on the password hash, so it stops working if the link is recreated.</summary>
    public bool IsUnlocked(Share share, string? cookie) =>
        share.PasswordHash is null ||
        (cookie is not null && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(cookie), System.Text.Encoding.ASCII.GetBytes(UnlockValue(share))));

    private string UnlockValue(Share share) =>
        Base64Url(HMACSHA256.HashData(_unlockKey, System.Text.Encoding.UTF8.GetBytes(share.Id + "|" + share.PasswordHash)));

    private void Update(string id, Func<Share, Share> change)
    {
        lock (_lock)
        {
            var all = Load();
            var i = all.FindIndex(s => s.Id == id);
            if (i < 0) return;
            all[i] = change(all[i]);
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
