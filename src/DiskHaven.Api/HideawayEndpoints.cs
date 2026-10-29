using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DiskHaven.Core.Hideaway;
using DiskHaven.Core.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

namespace DiskHaven.Api;

/// <summary>
/// Unlocked hidden folders, kept only in memory. Unlocking sets a random cookie (dh_h) bound to the user; the key
/// is forgotten on "Lock", on sign-out, after the idle time, and when the server restarts.
/// </summary>
public sealed class HideawaySessions(TimeProvider clock)
{
    private sealed record Entry(HideawayStore.Opened Vault, DateTimeOffset LastUse);
    private readonly ConcurrentDictionary<string, Entry> _open = new();
    public const string Cookie = "dh_h";
    public TimeSpan Idle { get; set; } = TimeSpan.FromMinutes(10);

    public string Add(HideawayStore.Opened v)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _open[token] = new Entry(v, clock.GetUtcNow());
        return token;
    }

    public HideawayStore.Opened? Get(HttpContext ctx)
    {
        Sweep();
        if (ctx.Request.Cookies[Cookie] is not { } token || !_open.TryGetValue(token, out var e)) return null;
        if (e.Vault.User != ctx.User.Identity?.Name) return null;
        _open[token] = e with { LastUse = clock.GetUtcNow() };
        return e.Vault;
    }

    public void Remove(HttpContext ctx) { if (ctx.Request.Cookies[Cookie] is { } t) _open.TryRemove(t, out _); }
    public void LockUser(string user) { foreach (var kv in _open.Where(kv => kv.Value.Vault.User == user)) _open.TryRemove(kv.Key, out _); }

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var kv in _open.Where(kv => now - kv.Value.LastUse > Idle)) _open.TryRemove(kv.Key, out _);
    }
}

public static class HideawayEndpoints
{
    private static readonly FileExtensionContentTypeProvider Types = new();

    private record SetupBody(string Password, string? DecoyPassword);
    private record SecretBody(string Password);
    private record FolderBody(string? Parent, string Name);
    private record RenameBody(string Id, string Name);
    private record IdsBody(string[] Ids, string? Parent);
    private record PasswordBody(string CurrentPassword, string NewPassword);
    private record DecoyBody(string DecoyPassword, string CurrentPassword);
    private record LabelBody(string Label);
    private record CommitBody(string UploadId, string? Parent, string RelativeName, string Sha256, long? LastModified);

    /// <summary>Suggested menu names: things nobody opens out of curiosity.</summary>
    public static readonly string[] LabelSuggestions =
        ["Sistem önbelleği", "Sürücü günlükleri", "Eski yedek kopyaları", "Bakım kayıtları", "Geçici dosyalar", "Dizin önbelleği", "Uygulama verileri"];

    public static string LabelOf(PluginStore plugins, string user) =>
        plugins.Settings("vault")["labels"]?[HideawayStore.UserFolderName(user)]?.GetValue<string>() ?? "Gizli klasör";

    public static void Map(WebApplication app, string uploadDir, DemoOptions demo)
    {
        var g = app.MapGroup("/api/hideaway").RequireAuthorization().RequiresPlugin("vault");
        static string User(ClaimsPrincipal u) => u.Identity!.Name!;

        g.MapGet("/state", (ClaimsPrincipal u, HttpContext ctx, HideawayStore store, HideawaySessions s, PluginStore plugins) => Results.Ok(new
        {
            setUp = store.IsSetUp(User(u)),
            unlocked = s.Get(ctx) is not null,
            label = LabelOf(plugins, User(u)),
            suggestions = LabelSuggestions,
            idleMinutes = (int)s.Idle.TotalMinutes,
        }));

        g.MapPost("/setup", async (SetupBody b, ClaimsPrincipal u, HttpContext ctx, HideawayStore store, HideawaySessions s) =>
        {
            try
            {
                var (key, vault) = await Task.Run(() => store.Setup(User(u), b.Password, string.IsNullOrEmpty(b.DecoyPassword) ? null : b.DecoyPassword));
                SetCookie(ctx, s.Add(vault));
                return Results.Ok(new { recoveryKey = key });
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return Results.Problem(e.Message, statusCode: 400); }
        });

        g.MapPost("/unlock", async (SecretBody b, ClaimsPrincipal u, HttpContext ctx, HideawayStore store, HideawaySessions s) =>
        {
            var vault = await Task.Run(() => store.Open(User(u), b.Password ?? ""));
            if (vault is null) return Results.Problem("Password is wrong.", statusCode: 401);
            SetCookie(ctx, s.Add(vault));
            return Results.NoContent();
        }).RequireRateLimiting("login");

        // the offline recovery tool, zipped (DLLs + a short how-to); the same tool opens a copy on a backup disk
        g.MapGet("/recovery-tool", (HttpContext ctx) =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "recover");
            if (!Directory.Exists(dir)) return Results.NotFound();
            var ms = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                foreach (var f in Directory.GetFiles(dir)) System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, f, "diskhaven-recover/" + Path.GetFileName(f));
                using var w = new StreamWriter(zip.CreateEntry("diskhaven-recover/NASIL-KULLANILIR.txt").Open());
                w.Write("""
                    DiskHaven gizli klasör kurtarma aracı

                    1. Bilgisayara .NET 10 çalışma ortamını kur: https://dotnet.microsoft.com/download/dotnet/10.0
                    2. Yedek diski tak. Kasa, diskte ".diskhaven-data/hideaway" klasöründedir.
                    3. Bu klasörde bir komut penceresi aç ve çalıştır:
                         dotnet diskhaven-recover.dll "E:\.diskhaven-data\hideaway" "C:\Kurtarilan"
                    4. Parolanı ya da kurtarma anahtarını yaz. Dosyalar normal klasörler olarak çıkış klasörüne yazılır.
                    """);
            }
            ms.Position = 0;
            return Results.File(ms, "application/zip", "diskhaven-kurtarma-araci.zip");
        });

        g.MapPost("/lock", (HttpContext ctx, HideawaySessions s) =>
        {
            s.Remove(ctx);
            ctx.Response.Cookies.Delete(HideawaySessions.Cookie, new CookieOptions { Path = "/api/hideaway" });
            return Results.NoContent();
        });

        // everything below needs an unlocked vault
        var v = g.MapGroup("").AddEndpointFilter(async (c, next) =>
            c.HttpContext.RequestServices.GetRequiredService<HideawaySessions>().Get(c.HttpContext) is null
                ? Results.Problem("The hidden folder is locked.", statusCode: 423)
                : await next(c));
        static HideawayStore.Opened Vault(HttpContext ctx) => ctx.RequestServices.GetRequiredService<HideawaySessions>().Get(ctx)!;
        static IResult Do(HttpContext ctx, Func<HideawayStore.Opened, object?> f)
        {
            var vault = Vault(ctx);
            try { lock (vault.Gate) { var r = f(vault); return r is null ? Results.NoContent() : Results.Ok(r); } }
            catch (KeyNotFoundException e) { return Results.Problem(e.Message, statusCode: 404); }
            catch (VaultPathException e) { return Results.Problem(e.Message, statusCode: 400); }
            catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
            catch (UnauthorizedAccessException e) { return Results.Problem(e.Message, statusCode: 403); }
            catch (IOException e) { return Results.Problem(e.Message, statusCode: 409); }
            catch (CryptographicException) { return Results.Problem("An encrypted item is damaged.", statusCode: 500); }
        }
        static object Entry(HideawayStore.Opened vault, HideawayNode n) => new
        {
            id = n.Id, name = n.Name, path = n.Id, kind = n.Folder ? "Folder" : "File",
            size = n.Folder ? vault.FolderSize(n.Id) : n.Size, modified = n.Modified,
            extension = n.Folder ? null : Path.GetExtension(n.Name).TrimStart('.').ToLowerInvariant() is { Length: > 0 } x ? x : null,
            hidden = false,
        };

        v.MapGet("/list", (string? folder, HttpContext ctx) => Do(ctx, vault => new
        {
            path = vault.PathOf(string.IsNullOrEmpty(folder) ? null : folder).Select(n => new { id = n.Id, name = n.Name }),
            entries = vault.Children(string.IsNullOrEmpty(folder) ? null : folder).Select(n => Entry(vault, n)),
        }));
        v.MapPost("/folder", (FolderBody b, HttpContext ctx) => Do(ctx, vault => Entry(vault, vault.CreateFolder(Nul(b.Parent), b.Name))));
        v.MapPost("/rename", (RenameBody b, HttpContext ctx) => Do(ctx, vault => Entry(vault, vault.Rename(b.Id, b.Name))));
        v.MapPost("/move", (IdsBody b, HttpContext ctx) => Do(ctx, vault => { vault.Move(b.Ids, Nul(b.Parent)); return null; }));
        v.MapPost("/copy", (IdsBody b, HttpContext ctx) => Do(ctx, vault => { vault.Copy(b.Ids, Nul(b.Parent)); return null; }));
        v.MapPost("/delete", (IdsBody b, HttpContext ctx) => Do(ctx, vault => { vault.Delete(b.Ids); return null; }));
        v.MapPost("/password", (PasswordBody b, HttpContext ctx) => Do(ctx, vault => { vault.ChangePassword(b.CurrentPassword, b.NewPassword); return null; }));
        v.MapPost("/decoy", (DecoyBody b, HttpContext ctx) => Do(ctx, vault => { vault.SetDecoy(b.DecoyPassword, b.CurrentPassword); return null; }));
        v.MapPut("/label", (LabelBody b, ClaimsPrincipal u, PluginStore plugins) =>
        {
            var label = b.Label.Trim();
            if (label.Length is < 1 or > 40) return Results.Problem("Name must be 1-40 characters.", statusCode: 400);
            var settings = plugins.Settings("vault");
            var labels = settings["labels"] as JsonObject ?? [];
            labels[HideawayStore.UserFolderName(User(u))] = label;
            settings["labels"] = labels;
            plugins.SaveSettings("vault", settings);
            return Results.NoContent();
        });

        v.MapPost("/commit", async (CommitBody b, HttpContext ctx, tusdotnet.Stores.TusDiskStore tus) =>
        {
            if (b.UploadId.Length != 32 || !b.UploadId.All(char.IsAsciiHexDigitLower))
                return Results.Problem("Invalid upload id.", statusCode: 400);
            var length = await tus.GetUploadLengthAsync(b.UploadId, ctx.RequestAborted);
            var offset = await tus.GetUploadOffsetAsync(b.UploadId, ctx.RequestAborted);
            if (length is null || offset != length) return Results.Problem("Upload is not complete.", statusCode: 409);
            var file = Path.Combine(uploadDir, b.UploadId);
            await using (var fs = File.OpenRead(file))
            {
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ctx.RequestAborted));
                if (!hash.Equals(b.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    await tus.DeleteFileAsync(b.UploadId, ctx.RequestAborted);
                    return Results.Problem("The uploaded file does not match its checksum.", statusCode: 422);
                }
            }
            // keep the data file (AddFile encrypts then deletes it); drop only tus' side files
            var keep = file + ".hide";
            File.Move(file, keep);
            await tus.DeleteFileAsync(b.UploadId, ctx.RequestAborted);
            return Do(ctx, vault =>
            {
                // "Folder/Sub/file.txt" from a folder upload: create the folders on the way
                var parts = b.RelativeName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                var parent = Nul(b.Parent);
                foreach (var dir in parts[..^1])
                    parent = (vault.Children(parent).FirstOrDefault(n => n.Folder && n.Name.Equals(dir, StringComparison.OrdinalIgnoreCase))
                              ?? vault.CreateFolder(parent, dir)).Id;
                var node = vault.AddFile(parent, parts[^1], keep, b.LastModified is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null);
                return Entry(vault, node);
            });
        });

        v.MapGet("/content", async (string id, bool? download, HttpContext ctx) =>
        {
            var vault = Vault(ctx);
            HideawayNode n;
            try { n = vault.Get(id); } catch (KeyNotFoundException) { return Results.NotFound(); }
            if (n.Folder) return Results.NotFound();
            if (!Types.TryGetContentType(n.Name, out var type)) type = "application/octet-stream";
            var passive = type.StartsWith("image/") && type != "image/svg+xml" || type.StartsWith("video/") || type.StartsWith("audio/") || type is "application/pdf" or "text/plain";
            if (!passive) { download = true; type = "application/octet-stream"; }
            ctx.Response.ContentType = type;
            ctx.Response.ContentLength = n.Size;
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers.CacheControl = "no-store";   // decrypted content never lands in a browser cache
            if (type != "application/pdf") ctx.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'";
            ctx.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition
            { FileName = n.Name, DispositionType = download == true ? "attachment" : "inline" }.ToString();
            ctx.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;
            try { await Task.Run(() => vault.ReadFile(id, ctx.Response.Body), ctx.RequestAborted); }
            catch (Exception e) when (e is IOException or OperationCanceledException or CryptographicException) { ctx.Abort(); }
            return Results.Empty;
        });

        v.MapGet("/archive", async (string[] ids, HttpContext ctx) =>
        {
            var vault = Vault(ctx);
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"dosyalar.zip\"";
            ctx.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;
            try
            {
                await Task.Run(() =>
                {
                    using var zip = new System.IO.Compression.ZipArchive(ctx.Response.Body, System.IO.Compression.ZipArchiveMode.Create, true);
                    void Add(HideawayNode n, string prefix)
                    {
                        if (n.Folder) { foreach (var c in vault.Children(n.Id)) Add(c, $"{prefix}{n.Name}/"); return; }
                        using var s = zip.CreateEntry(prefix + n.Name, System.IO.Compression.CompressionLevel.Fastest).Open();
                        vault.ReadFile(n.Id, s);
                    }
                    foreach (var id in ids) Add(vault.Get(id), "");
                }, ctx.RequestAborted);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or KeyNotFoundException or CryptographicException) { ctx.Abort(); }
            return Results.Empty;
        });
    }

    private static string? Nul(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static void SetCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(HideawaySessions.Cookie, token, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/api/hideaway",
            Secure = ctx.Request.IsHttps,
        });
}
