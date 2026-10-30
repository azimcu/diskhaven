using System.Security.Claims;
using System.Threading.RateLimiting;
using DiskHaven.Api;
using DiskHaven.Api.Disks;
using DiskHaven.Core.Accounts;
using DiskHaven.Core.Sharing;
using DiskHaven.Core.Storage;
using DiskHaven.Core.Sync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.StaticFiles;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Expiration;
using tusdotnet.Stores;

var builder = WebApplication.CreateBuilder(args);

// Configuration: appsettings.json or environment, e.g. DiskHaven__DataRoot=/data DiskHaven__ConfigDir=/config
var dataRoot = builder.Configuration["DiskHaven:DataRoot"]
    ?? throw new InvalidOperationException("DiskHaven:DataRoot is not configured.");
var configDir = builder.Configuration["DiskHaven:ConfigDir"]
    ?? throw new InvalidOperationException("DiskHaven:ConfigDir is not configured.");

var root = new VaultRoot(dataRoot);
var demo = DemoOptions.From(builder.Configuration);
var uploadDir = Path.Combine(root.FullPath, VaultRoot.InternalFolderName, "uploads");
Directory.CreateDirectory(uploadDir);
var tusStore = new TusDiskStore(uploadDir);

builder.Services.AddSingleton(root);
builder.Services.AddSingleton(demo);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FolderSizeIndex>();
builder.Services.AddSingleton<IVaultChangeListener>(sp => sp.GetRequiredService<FolderSizeIndex>());
builder.Services.AddSingleton<FileBrowser>();
builder.Services.AddSingleton<FileOperations>();
builder.Services.AddSingleton<UploadCommitter>();
builder.Services.AddSingleton<Archives>();
builder.Services.AddSingleton(new PluginStore(configDir));
builder.Services.AddSingleton(new ActivityLog(configDir));
builder.Services.AddSingleton(new QuickAccess(configDir));
builder.Services.AddSingleton(new DiskHaven.Core.Hideaway.HideawayStore(dataRoot));
builder.Services.AddSingleton<HideawaySessions>();
builder.Services.AddSingleton<SystemStats>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemStats>());
builder.Services.AddSingleton(new AccountStore(configDir));
builder.Services.AddSingleton(new ShareStore(configDir));
builder.Services.AddSingleton(tusStore);
builder.Services.AddHostedService<Housekeeping>();
builder.Services.AddHostedService<FolderSizeScanner>();
builder.Services.AddSingleton<DiskAgentClient>();
builder.Services.AddSingleton(new DiskSettingsStore(configDir));
builder.Services.AddSingleton<DiskService>();
builder.Services.AddSingleton<SyncJobRunner>();
builder.Services.AddHostedService<SyncScheduler>();
builder.AddDiskHavenTelemetry();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Upload chunks are 16 MB; leave headroom.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64L * 1024 * 1024);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "diskhaven";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        // an API answers 401/403 instead of redirecting to a login page
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        // a deleted account, a changed password or a changed role ends the session on every device
        o.Events.OnValidatePrincipal = c =>
        {
            var accounts = c.HttpContext.RequestServices.GetRequiredService<AccountStore>();
            var account = c.Principal?.Identity?.Name is { } name ? accounts.Find(name) : null;
            if (account is null || c.Principal!.FindFirstValue("stamp") != AccountStore.Stamp(account) ||
                c.Principal.IsInRole("admin") != account.IsAdmin)
            {
                c.RejectPrincipal();
                return c.HttpContext.SignOutAsync();
            }
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder().AddPolicy("admin", p => p.RequireRole("admin"));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
    // share links are public: limit how fast one address can try tokens or pull files
    o.AddPolicy("public", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

// CSRF protection: state-changing API calls must carry a custom header, which a page on
// another origin cannot add without a CORS preflight (and no CORS is allowed).
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") &&
        !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) &&
        !HttpMethods.IsOptions(ctx.Request.Method) &&
        ctx.Request.Headers["X-DiskHaven"] != "1")
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("Missing X-DiskHaven header.");
        return;
    }
    await next();
});

// Public demo: account, disk, sync and share-link changes are switched off.
if (demo.Enabled)
    app.Use(async (ctx, next) =>
    {
        if (DemoOptions.IsBlocked(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { title = "Bu özellik demoda kapalı.", status = 403 });
            return;
        }
        await next();
    });

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".mkv"] = "video/x-matroska";

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// ---- authentication -------------------------------------------------------------------
var auth = app.MapGroup("/api/auth");

auth.MapGet("/state", (HttpContext ctx, AccountStore accounts) => Results.Ok(new
{
    setupRequired = !accounts.HasAccounts,
    user = ctx.User.Identity?.IsAuthenticated == true ? ctx.User.Identity.Name : null,
    isAdmin = ctx.User.IsInRole("admin"),
    demo = demo.Enabled ? new { userName = demo.UserName, password = demo.Password } : null,
}));

auth.MapPost("/setup", async (SetupRequest req, HttpContext ctx, AccountStore accounts) =>
{
    try
    {
        var account = accounts.CreateFirst(req.Token, req.UserName, req.Password);
        await SignIn(ctx, account);
        return Results.Ok(new { user = account.UserName });
    }
    catch (UnauthorizedAccessException e) { return Results.Problem(e.Message, statusCode: 401); }
    catch (InvalidOperationException e) { return Results.Problem(e.Message, statusCode: 409); }
    catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
}).RequireRateLimiting("login");

auth.MapPost("/login", async (LoginRequest req, HttpContext ctx, AccountStore accounts) =>
{
    var account = accounts.Authenticate(req.UserName, req.Password);
    if (account is null)
    {
        Telemetry.FailedLogins.Add(1);
        return Results.Problem("User name or password is wrong.", statusCode: 401);
    }
    await SignIn(ctx, account);
    return Results.Ok(new { user = account.UserName });
}).RequireRateLimiting("login");

auth.MapPost("/logout", async (HttpContext ctx, HideawaySessions hidden) =>
{
    if (ctx.User.Identity?.Name is { } name) hidden.LockUser(name);   // signing out also locks the hidden folder
    await ctx.SignOutAsync();
    return Results.NoContent();
});

auth.MapPost("/password", async (PasswordRequest req, ClaimsPrincipal user, HttpContext ctx, AccountStore accounts) =>
{
    try
    {
        accounts.ChangePassword(user.Identity!.Name!, req.CurrentPassword, req.NewPassword);
        // keep this device signed in with the new stamp; other devices are logged out
        await SignIn(ctx, accounts.Find(user.Identity!.Name!)!);
        return Results.NoContent();
    }
    catch (UnauthorizedAccessException e) { return Results.Problem(e.Message, statusCode: 401); }
    catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
}).RequireAuthorization().RequireRateLimiting("login");

// ---- files ------------------------------------------------------------------------------
var files = app.MapGroup("/api/files").RequireAuthorization().AddEndpointFilter(Telemetry.CountFileOperation);

files.MapGet("/", (string? path, FileBrowser browser) =>
    Guard(() => Results.Ok(new { path = VaultRoot.Normalize(path), entries = browser.List(path) })));

files.MapGet("/content", (string path, bool? download, HttpContext ctx, FileBrowser browser) => Guard(() =>
{
    return ServeFile(ctx, browser.GetFile(path), download == true);
}));

files.MapPost("/folder", (FolderRequest req, FileOperations ops) =>
    Guard(() => Results.Ok(new { path = ops.CreateFolder(req.Parent, req.Name) }))).LogsActivity("folder");

files.MapPost("/rename", (RenameRequest req, FileOperations ops) =>
    Guard(() => Results.Ok(new { path = ops.Rename(req.Path, req.NewName) }))).LogsActivity("rename");

files.MapPost("/move", (TransferRequest req, FileOperations ops) =>
    Guard(() => Results.Ok(new { paths = ops.Move(req.Paths, req.Destination) }))).LogsActivity("move");

files.MapPost("/copy", (TransferRequest req, FileOperations ops) =>
    Guard(() => Results.Ok(new { paths = ops.Copy(req.Paths, req.Destination) }))).LogsActivity("copy");

files.MapPost("/delete", (DeleteRequest req, FileOperations ops) =>
    Guard(() => Results.Ok(new { items = ops.Delete(req.Paths) }))).LogsActivity("delete");

files.MapGet("/search", (string? path, string q, FileBrowser browser, CancellationToken ct) => Guard(() =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    var r = browser.Search(path, q, 500, timeout.Token);
    return Results.Ok(new { path = VaultRoot.Normalize(path), entries = r.Entries, truncated = r.Truncated });
}));

files.MapPost("/hidden", (HiddenRequest req, VaultRoot vault, FileBrowser browser) => Guard(() =>
{
    foreach (var p in req.Paths)
    {
        var full = vault.Resolve(p);
        if (full == vault.FullPath || (!File.Exists(full) && !Directory.Exists(full)))
            throw new FileNotFoundException("Item not found.");
        HiddenFlag.Set(full, req.Hidden);
    }
    return Results.NoContent();
})).LogsActivity("hide");

files.MapGet("/properties", (string path, FileBrowser browser) => Guard(() => Results.Ok(browser.Properties(path))));

files.MapGet("/checksum", async (string path, FileBrowser browser, CancellationToken ct) =>
{
    string full;
    try { full = browser.GetFile(path); }
    catch (Exception e) when (e is VaultPathException or FileNotFoundException) { return Results.Problem(e.Message, statusCode: 404); }
    await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
    var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs, ct);
    return Results.Ok(new { sha256 = Convert.ToHexStringLower(hash) });
});

files.MapPost("/compress", (CompressRequest req, Archives archives, VaultRoot vault) => Guard(() =>
{
    if (demo.Enabled && DemoOptions.FolderBytes(vault.FullPath) >= demo.MaxTotalBytes)
        throw new IOException("Demo storage is full.");
    return Results.Ok(new { path = archives.Compress(req.Paths, req.Name) });
})).RequiresPlugin("archive").LogsActivity("compress");

files.MapPost("/extract", (ExtractRequest req, Archives archives, VaultRoot vault) => Guard(() =>
{
    var free = demo.Enabled ? Math.Max(0, demo.MaxTotalBytes - DemoOptions.FolderBytes(vault.FullPath)) : new DriveInfo(vault.FullPath).AvailableFreeSpace;
    return Results.Ok(new { path = archives.Extract(req.Path, free) });
})).RequiresPlugin("archive").LogsActivity("extract");

files.MapGet("/zip-list", (string path, Archives archives) =>
    Guard(() => Results.Ok(archives.List(path).Select(e => new { name = e.Name, size = e.Size })))).RequiresPlugin("archive");

// several items or a folder as one ZIP download, built while it is sent (nothing is stored)
files.MapGet("/archive", async (string[] paths, string? name, HttpContext ctx, Archives archives, VaultRoot vault) =>
{
    try { foreach (var p in paths) _ = vault.Resolve(p); }
    catch (VaultPathException e) { return Results.Problem(e.Message, statusCode: 400); }
    var file = (string.IsNullOrWhiteSpace(name) ? paths.Length == 1 ? Path.GetFileName(paths[0]) : "DiskHaven" : name) + ".zip";
    ctx.Response.ContentType = "application/zip";
    ctx.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = file, DispositionType = "attachment" }.ToString();
    var feature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();
    if (feature is not null) feature.AllowSynchronousIO = true;   // ZipArchive writes synchronously
    try { await Task.Run(() => archives.WriteTo(ctx.Response.Body, paths, ctx.RequestAborted)); }
    catch (Exception e) when (e is FileNotFoundException or OperationCanceledException or IOException) { }
    return Results.Empty;
}).RequiresPlugin("archive");

// ---- system: live numbers for the corner gauge -----------------------------------------
app.MapGet("/api/system/stats", (SystemStats stats) => Results.Ok(stats.Last)).RequireAuthorization().RequiresPlugin("gauge");

app.MapGet("/api/storage", (VaultRoot vault) =>
{
    // the demo shows its own quota, not the host disk
    if (demo.Enabled)
        return Results.Ok(new { total = demo.MaxTotalBytes, free = Math.Max(0, demo.MaxTotalBytes - DemoOptions.FolderBytes(vault.FullPath)) });
    var drive = new DriveInfo(vault.FullPath);
    return Results.Ok(new { total = drive.TotalSize, free = drive.AvailableFreeSpace });
}).RequireAuthorization();

// ---- recycle bin ------------------------------------------------------------------------
var trash = app.MapGroup("/api/trash").RequireAuthorization().AddEndpointFilter(Telemetry.CountFileOperation);
trash.MapGet("/", (FileOperations ops) => Results.Ok(ops.ListTrash()));
trash.MapPost("/{id}/restore", (string id, FileOperations ops) => Guard(() => Results.Ok(new { path = ops.Restore(id) }))).LogsActivity("restore");
trash.MapDelete("/{id}", (string id, FileOperations ops) => Guard(() => { ops.Purge(id); return Results.NoContent(); })).LogsActivity("purge");
trash.MapDelete("/", (FileOperations ops) =>
{
    foreach (var item in ops.ListTrash())
        ops.Purge(item.Id);
    return Results.NoContent();
});

// ---- share links ------------------------------------------------------------------------
// the owner's view of a link: everything but the password hash
static object ShareView(Share s) => new
{
    s.Id, s.Token, s.Path, s.Kind, s.CreatedBy, s.CreatedAt, s.ExpiresAt, s.Downloads, s.Mode, s.HasPassword, s.Uploads,
};
var sharesApi = app.MapGroup("/api/shares").RequireAuthorization().RequiresPlugin("sharing");
sharesApi.MapGet("/", (ShareStore shares) => Results.Ok(shares.List().Select(ShareView)));
sharesApi.MapPost("/", (ShareRequest req, ClaimsPrincipal user, ShareStore shares, VaultRoot vault) => Guard(() =>
{
    var full = vault.Resolve(req.Path);
    var kind = Directory.Exists(full) ? EntryKind.Folder : File.Exists(full) ? EntryKind.File
        : throw new FileNotFoundException("Item not found.");
    TimeSpan? life = req.Days is > 0 ? TimeSpan.FromDays(req.Days.Value) : null;
    try { return Results.Ok(ShareView(shares.Create(req.Path, kind, user.Identity!.Name!, life, req.Mode ?? ShareMode.View, req.Password))); }
    catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
})).LogsActivity("share");
sharesApi.MapDelete("/{id}", (string id, ShareStore shares) => Guard(() => { shares.Revoke(id); return Results.NoContent(); })).LogsActivity("unshare");

// public side of a link: no login, only the shared item and what is inside it.
// A password-protected link answers only after the password was entered (a cookie scoped to this link).
static string UnlockCookie(Share s) => "dh_s_" + s.Id;
Share OpenShare(string token, HttpContext ctx, ShareStore shares)
{
    var share = shares.Find(token) ?? throw new KeyNotFoundException("This link has expired or was removed.");
    if (!shares.IsUnlocked(share, ctx.Request.Cookies[UnlockCookie(share)]))
        throw new SharePasswordException();
    return share;
}
var publicApi = app.MapGroup("/api/public/{token}").RequireRateLimiting("public").RequiresPlugin("sharing");
publicApi.MapGet("/", (string token, string? path, HttpContext ctx, ShareStore shares, FileBrowser browser, VaultRoot vault) => Guard(() =>
{
    var share = shares.Find(token) ?? throw new KeyNotFoundException("This link has expired or was removed.");
    if (!shares.IsUnlocked(share, ctx.Request.Cookies[UnlockCookie(share)]))
        return Results.Ok(new { locked = true });   // nothing about the item before the password
    var target = ShareStore.Inside(share, share.Mode == ShareMode.UploadOnly ? null : path);
    var full = vault.Resolve(target);
    var name = Path.GetFileName(full);
    var by = share.CreatedBy;
    if (share.Mode == ShareMode.UploadOnly)   // a drop box never shows what is already in the folder
        return Results.Ok(new { name, kind = EntryKind.Folder, size = (long?)null, by, expiresAt = share.ExpiresAt, mode = share.Mode, entries = (object?)null });
    if (File.Exists(full))
        return Results.Ok(new { name, kind = EntryKind.File, size = new FileInfo(full).Length, by, expiresAt = share.ExpiresAt, mode = share.Mode, entries = (object?)null });
    var prefix = share.Path.Length + 1;
    var entries = browser.List(target).Where(e => !e.Hidden)
        .Select(e => e with { Path = e.Path[prefix..] });   // paths relative to the shared folder, never the vault
    return Results.Ok(new { name, kind = EntryKind.Folder, size = (long?)null, by, expiresAt = share.ExpiresAt, mode = share.Mode, entries = (object?)entries });
}));
publicApi.MapPost("/unlock", (string token, UnlockRequest req, HttpContext ctx, ShareStore shares) => Guard(() =>
{
    var share = shares.Find(token) ?? throw new KeyNotFoundException("This link has expired or was removed.");
    if (shares.Unlock(share, req.Password ?? "") is not { } value)
        return Results.Problem("Wrong link password.", statusCode: StatusCodes.Status403Forbidden);
    ctx.Response.Cookies.Append(UnlockCookie(share), value, new CookieOptions
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, Path = $"/api/public/{token}",
    });
    return Results.NoContent();
})).RequireRateLimiting("login");   // guessing the password is as slow as guessing an account's
publicApi.MapGet("/content", (string token, string? path, bool? download, HttpContext ctx, ShareStore shares, FileBrowser browser) => Guard(() =>
{
    var share = OpenShare(token, ctx, shares);
    if (share.Mode == ShareMode.UploadOnly)
        return Results.Problem("This link only accepts files.", statusCode: StatusCodes.Status403Forbidden);
    var full = browser.GetFile(ShareStore.Inside(share, path));
    if (download == true)
    {
        shares.CountDownload(share.Id);
        Telemetry.ShareDownloads.Add(1);
    }
    return ServeFile(ctx, full, download == true);
}));
// drop box: the raw request body is one file. It is hashed while it arrives and then placed with the
// same committer as a normal upload, so it never overwrites anything (a taken name gets " (2)").
publicApi.MapPost("/upload", async (string token, string? name, HttpContext ctx, ShareStore shares, UploadCommitter committer,
    VaultRoot vault, CancellationToken ct) =>
{
    Share share;
    try { share = OpenShare(token, ctx, shares); }
    catch (KeyNotFoundException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound); }
    catch (SharePasswordException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status403Forbidden); }
    if (share.Mode != ShareMode.UploadOnly)
        return Results.Problem("This link does not accept files.", statusCode: StatusCodes.Status403Forbidden);
    try { FileNames.Validate(name); }
    catch (VaultPathException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
    if (name!.Contains('/') || name.Contains('\\'))
        return Results.Problem("Invalid file name.", statusCode: StatusCodes.Status400BadRequest);
    if (ctx.Request.ContentLength is { } announced && announced > new DriveInfo(vault.FullPath).AvailableFreeSpace - (1L << 30))
        return Results.Problem("Not enough free space.", statusCode: StatusCodes.Status507InsufficientStorage);
    if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        limit.MaxRequestBodySize = 64L << 30;   // one file per request, so the 64 MB chunk limit does not apply
    var temp = Path.Combine(uploadDir, "drop-" + Guid.NewGuid().ToString("N"));
    try
    {
        string hash;
        await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        using (var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1 << 20];
            var drive = new DriveInfo(vault.FullPath);
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                // the announced length can be missing or wrong: keep the 1 GB reserve before every 1 MB write
                if (drive.AvailableFreeSpace - n < (1L << 30))
                    return Results.Problem("Not enough free space.", statusCode: StatusCodes.Status507InsufficientStorage);
                sha.AppendData(buffer, 0, n);
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
            }
            hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        var result = await committer.CommitAsync(temp, share.Path, name, hash, null, ct);
        shares.CountUpload(share.Id);
        Telemetry.Uploads.Add(1);
        // only the size: the stored name (maybe "x (2).txt") would tell a visitor what is already in the folder
        return Results.Ok(new { size = result.Size });
    }
    catch (VaultPathException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
    catch (DirectoryNotFoundException) { return Results.Problem("This link has expired or was removed.", statusCode: StatusCodes.Status404NotFound); }
    finally { if (File.Exists(temp)) File.Delete(temp); }
});

// ---- users (admin) ----------------------------------------------------------------------
var usersApi = app.MapGroup("/api/users").RequireAuthorization("admin");
usersApi.MapGet("/", (AccountStore accounts) =>
    Results.Ok(accounts.List().Select(a => new { userName = a.UserName, isAdmin = a.IsAdmin, createdAt = a.CreatedAt })));
usersApi.MapPost("/", (NewUserRequest req, AccountStore accounts) =>
{
    try { var a = accounts.Create(req.UserName, req.Password, req.IsAdmin); return Results.Ok(new { userName = a.UserName, isAdmin = a.IsAdmin }); }
    catch (InvalidOperationException e) { return Results.Problem(e.Message, statusCode: 409); }
    catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
});
usersApi.MapDelete("/{name}", (string name, ClaimsPrincipal user, AccountStore accounts) =>
{
    if (name.Equals(user.Identity!.Name, StringComparison.OrdinalIgnoreCase))
        return Results.Problem("You cannot remove your own account.", statusCode: 409);
    try { accounts.Delete(name); return Results.NoContent(); }
    catch (KeyNotFoundException e) { return Results.Problem(e.Message, statusCode: 404); }
    catch (InvalidOperationException e) { return Results.Problem(e.Message, statusCode: 409); }
});
usersApi.MapPost("/{name}/password", (string name, ResetPasswordRequest req, AccountStore accounts) =>
{
    try { accounts.SetPassword(name, req.NewPassword); return Results.NoContent(); }
    catch (KeyNotFoundException e) { return Results.Problem(e.Message, statusCode: 404); }
    catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
});

// ---- disks and sync (admin) -----------------------------------------------------------
var disksApi = app.MapGroup("/api/disks").RequireAuthorization("admin").RequiresPlugin("disks");

disksApi.MapGet("/", async (DiskAgentClient agent, DiskService disks, SyncJobRunner runner, CancellationToken ct) =>
    demo.Enabled ? Results.Ok(new { agent = false, demo = true, disks = Array.Empty<DiskView>(), job = (SyncJob?)null }) :
    !agent.Available
        ? Results.Ok(new { agent = false, disks = Array.Empty<DiskView>(), job = runner.Current })
        : await GuardAsync(async () => Results.Ok(new { agent = true, disks = await disks.ListAsync(ct), job = runner.Current })));

disksApi.MapPost("/{id}/mount", (string id, DiskService disks, CancellationToken ct) =>
    GuardAsync(async () => { await disks.EnsureMountedAsync(id, ct); return Results.NoContent(); }));
disksApi.MapPost("/{id}/unmount", (string id, DiskAgentClient agent, CancellationToken ct) =>
    GuardAsync(async () => { await agent.UnmountAsync(id, ct); return Results.NoContent(); }));
disksApi.MapPost("/{id}/standby", (string id, DiskAgentClient agent, CancellationToken ct) =>
    GuardAsync(async () => { await agent.UnmountAsync(id, ct); await agent.StandbyAsync(id, ct); return Results.NoContent(); }));
disksApi.MapPost("/{id}/power-off", (string id, DiskAgentClient agent, SyncJobRunner runner, CancellationToken ct) =>
    GuardAsync(async () =>
    {
        if (runner.Current is { Status: JobStatus.Running } j && j.FsId == id)
            throw new VaultConflictException("A sync is already running.");
        await agent.PowerOffAsync(id, ct);
        return Results.NoContent();
    }));
disksApi.MapPost("/{id}/power-on", (string id, DiskService disks, CancellationToken ct) =>
    GuardAsync(async () => { await disks.EnsureMountedAsync(id, ct); return Results.NoContent(); }));
disksApi.MapGet("/{id}/smart", (string id, DiskAgentClient agent, CancellationToken ct) =>
    GuardAsync(async () => Results.Ok(await agent.SmartAsync(id, ct))));

disksApi.MapPost("/{id}/adopt", (string id, AdoptRequest req, DiskService disks, CancellationToken ct) =>
    GuardAsync(async () => Results.Ok(await disks.AdoptAsync(id, req.Name, ct))));
disksApi.MapDelete("/{id}", (string id, DiskSettingsStore store) => { store.Remove(id); return Results.NoContent(); });
disksApi.MapPut("/{id}", (string id, DiskSettingsRequest req, DiskService disks) =>
    Guard(() => Results.Ok(disks.UpdateSettings(id, req.Name, req.Folders, req.Schedule, req.After))));
disksApi.MapGet("/{id}/history", (string id, DiskSettingsStore store) => Results.Ok(store.History(id)));

disksApi.MapPost("/{id}/plan", (string id, DiskService disks, DiskSettingsStore store, VaultRoot vault, CancellationToken ct) =>
    GuardAsync(async () =>
    {
        var s = store.Get(id) ?? throw new VaultConflictException("Disk is not adopted.");
        var root = await disks.EnsureMountedAsync(id, ct);
        var plan = await Task.Run(() => SyncPlanner.Plan(vault.FullPath, s.Folders, root, ct), ct);
        var drive = new DriveInfo(root);
        return Results.Ok(new
        {
            summary = SyncJobRunner.Summarize(plan),
            free = drive.AvailableFreeSpace,
            total = drive.TotalSize,
            actions = plan.Actions.Where(a => a.Kind != SyncActionKind.CreateFolder).Take(200),
            skipped = plan.Skipped.Take(50),
        });
    }));
disksApi.MapPost("/{id}/sync", (string id, SyncJobRunner runner) => Guard(() => Results.Ok(runner.Start(id))));

app.MapGet("/api/sync/job", (SyncJobRunner runner) => Results.Ok(runner.Current)).RequireAuthorization("admin").RequiresPlugin("disks");
app.MapPost("/api/sync/job/cancel", (SyncJobRunner runner) => { runner.Cancel(); return Results.NoContent(); }).RequireAuthorization("admin").RequiresPlugin("disks");

// ---- plugins, quick access, admin panel ---------------------------------------------------
var startedAt = DateTimeOffset.UtcNow;
var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

app.MapGet("/api/plugins", (ClaimsPrincipal user, PluginStore plugins) => Results.Ok(new
{
    installed = plugins.Installed,
    vaultLabel = plugins.IsInstalled("vault") ? HideawayEndpoints.LabelOf(plugins, user.Identity!.Name!) : null,
})).RequireAuthorization();

var quick = app.MapGroup("/api/quick").RequireAuthorization().RequiresPlugin("quick");
quick.MapGet("/", (ClaimsPrincipal u, QuickAccess q) => { var (f, r) = q.Get(QuickAccess.Name(u)); return Results.Ok(new { favorites = f, recent = r }); });
quick.MapPost("/favorite", (QuickRequest req, ClaimsPrincipal u, QuickAccess q) => Guard(() =>
{
    var path = VaultRoot.Normalize(req.Path);
    q.Change(QuickAccess.Name(u), (fav, _) => { fav.Remove(path); if (req.On) fav.Add(path); });
    return Results.NoContent();
}));
quick.MapPost("/recent", (QuickRequest req, ClaimsPrincipal u, QuickAccess q) => Guard(() =>
{
    var path = VaultRoot.Normalize(req.Path);
    q.Change(QuickAccess.Name(u), (_, rec) => { rec.Remove(path); rec.Insert(0, path); if (rec.Count > 20) rec.RemoveRange(20, rec.Count - 20); });
    return Results.NoContent();
}));

var admin = app.MapGroup("/api/admin").RequireAuthorization("admin");
admin.MapGet("/overview", (VaultRoot vault, AccountStore accounts, SystemStats stats, PluginStore plugins, ShareStore shares, FileOperations ops) =>
{
    var drive = new DriveInfo(vault.FullPath);
    return Results.Ok(new
    {
        version, startedAt, demo = demo.Enabled,
        storage = demo.Enabled
            ? new { total = demo.MaxTotalBytes, free = Math.Max(0, demo.MaxTotalBytes - DemoOptions.FolderBytes(vault.FullPath)) }
            : new { total = drive.TotalSize, free = drive.AvailableFreeSpace },
        users = accounts.List().Count, shares = shares.List().Count, trash = ops.ListTrash().Count,
        plugins = plugins.Installed.Count, system = stats.Last,
        dotnet = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    });
});
admin.MapGet("/plugins", (PluginStore plugins) => Results.Ok(PluginCatalog.All.Select(p => new
{
    p.Id, p.Name, p.Description, p.Category, p.Version, p.Features, installed = plugins.IsInstalled(p.Id),
})));
admin.MapPost("/plugins/{id}", (string id, PluginToggle req, PluginStore plugins) => Guard(() =>
{
    plugins.SetInstalled(id, req.Installed);
    return Results.NoContent();
}));
admin.MapGet("/activity", (string? q, string? user, string? action, int? limit, ActivityLog log) =>
    Results.Ok(log.Query(q, user, action, limit ?? 200))).RequiresPlugin("activity");

HideawayEndpoints.Map(app, uploadDir, demo);

// ---- uploads: tus protocol for resumable chunks, then a verified commit ----------------
app.MapTus("/api/tus", _ => Task.FromResult(new DefaultTusConfiguration
{
    Store = tusStore,
    Expiration = new SlidingExpiration(TimeSpan.FromHours(48)),
    Events = new()
    {
        OnBeforeCreateAsync = ctx =>
        {
            var free = new DriveInfo(root.FullPath).AvailableFreeSpace;
            // keep 1 GB free; the demo sits on its own small disk and is capped below instead
            var reserve = demo.Enabled ? 0 : 1L << 30;
            if (demo.Enabled && ctx.UploadLength < 0)
                ctx.FailRequest(System.Net.HttpStatusCode.LengthRequired, "Demoda dosya boyutu baştan bildirilmeli.");
            else if (ctx.UploadLength > free - reserve)
                ctx.FailRequest(System.Net.HttpStatusCode.InsufficientStorage, "Not enough free space on the disk.");
            else if (demo.Enabled && ctx.UploadLength > demo.MaxUploadBytes)
                ctx.FailRequest(System.Net.HttpStatusCode.RequestEntityTooLarge, $"Demoda dosya başına en fazla {demo.MaxUploadBytes >> 20} MB.");
            else if (demo.Enabled && DemoOptions.FolderBytes(root.FullPath) + ctx.UploadLength > demo.MaxTotalBytes)
                ctx.FailRequest(System.Net.HttpStatusCode.InsufficientStorage, "Demo alanı doldu; her saat başı sıfırlanır.");
            return Task.CompletedTask;
        },
    },
})).RequireAuthorization();

app.MapPost("/api/uploads/{id}/commit", async (string id, CommitRequest req, UploadCommitter committer, CancellationToken ct) =>
{
    if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower))
        return Results.Problem("Invalid upload id.", statusCode: 400);
    var length = await tusStore.GetUploadLengthAsync(id, ct);
    var offset = await tusStore.GetUploadOffsetAsync(id, ct);
    if (length is null || offset != length)
        return Results.Problem("Upload is not complete.", statusCode: 409);
    using var activity = Telemetry.Source.StartActivity("upload.commit");
    activity?.SetTag("diskhaven.upload.bytes", length);
    try
    {
        var result = await committer.CommitAsync(Path.Combine(uploadDir, id), req.Destination, req.RelativeName,
            req.Sha256, req.LastModified is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null, ct);
        await tusStore.DeleteFileAsync(id, ct); // removes tus side files; the data file was moved
        Telemetry.Uploads.Add(1);
        Telemetry.UploadedBytes.Add(length.Value);
        return Results.Ok(result);
    }
    catch (ChecksumMismatchException e)
    {
        // the received data is wrong: throw it away so the client uploads again
        await tusStore.DeleteFileAsync(id, ct);
        return Results.Problem(e.Message, statusCode: 422);
    }
    catch (VaultPathException e) { return Results.Problem(e.Message, statusCode: 400); }
    catch (DirectoryNotFoundException e) { return Results.Problem(e.Message, statusCode: 404); }
}).RequireAuthorization().LogsActivity("upload");

// single-page app: unknown non-API paths serve index.html
app.MapFallback(async ctx =>
{
    var index = Path.Combine(app.Environment.WebRootPath ?? "", "index.html");
    if (ctx.Request.Path.StartsWithSegments("/api") || !File.Exists(index))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    ctx.Response.ContentType = "text/html; charset=utf-8";
    await ctx.Response.SendFileAsync(index);
});

if (!app.Services.GetRequiredService<AccountStore>().HasAccounts)
    app.Logger.LogWarning("No account yet. Open the web UI and use the setup token from {File}",
        app.Services.GetRequiredService<AccountStore>().SetupTokenPath);

app.Run();

// Serves a stored file. Only passive media (images, audio, video, PDF, plain text) is shown inline; anything that a
// browser could run (HTML, SVG, XML, scripts...) is always downloaded, so a stored file can never act on this origin
// with the viewer's session. The sandbox CSP blocks scripts even if a browser sniffs the type.
IResult ServeFile(HttpContext ctx, string full, bool download)
{
    if (!contentTypes.TryGetContentType(full, out var type))
        type = "application/octet-stream";
    var passive = type.StartsWith("image/") && type != "image/svg+xml" || type.StartsWith("video/") || type.StartsWith("audio/")
                  || type is "application/pdf" or "text/plain";
    if (!passive)
    {
        download = true;
        type = "application/octet-stream";
    }
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    if (type != "application/pdf")   // the browser's PDF viewer does not open under a sandbox policy; it runs isolated anyway
        ctx.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'";
    // Range requests let video players seek and let interrupted downloads resume.
    return Results.File(full, type, download ? Path.GetFileName(full) : null,
        lastModified: File.GetLastWriteTimeUtc(full), enableRangeProcessing: true);
}

static Task SignIn(HttpContext ctx, Account account) =>
    ctx.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, account.UserName), new Claim(ClaimTypes.Role, account.IsAdmin ? "admin" : "user"),
         new Claim("stamp", AccountStore.Stamp(account))],
        CookieAuthenticationDefaults.AuthenticationScheme)),
        new AuthenticationProperties { IsPersistent = true });

static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
{
    try { return await action(); }
    catch (DiskAgentException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (VaultPathException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
    catch (VaultConflictException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (KeyNotFoundException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound); }
    catch (IOException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
}

static IResult Guard(Func<IResult> action)
{
    try { return action(); }
    catch (VaultPathException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
    catch (VaultConflictException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
    { return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound); }
    catch (IOException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (KeyNotFoundException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound); }
    // 403, not 401: a 401 would send a signed-in owner viewing their own link to the login screen
    catch (SharePasswordException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status403Forbidden); }
}

record SetupRequest(string Token, string UserName, string Password);
record LoginRequest(string UserName, string Password);
record PasswordRequest(string CurrentPassword, string NewPassword);
record FolderRequest(string? Parent, string Name);
record RenameRequest(string Path, string NewName);
record TransferRequest(string[] Paths, string? Destination);
record DeleteRequest(string[] Paths);
record HiddenRequest(string[] Paths, bool Hidden);
record CompressRequest(string[] Paths, string? Name);
record ExtractRequest(string Path);
record QuickRequest(string Path, bool On = true);
record PluginToggle(bool Installed);
record AdoptRequest(string Name);
record DiskSettingsRequest(string? Name, string[]? Folders, SyncSchedule? Schedule, AfterSync? After);
record ShareRequest(string Path, int? Days, ShareMode? Mode = null, string? Password = null);
record UnlockRequest(string? Password);
sealed class SharePasswordException() : Exception("This link is protected with a password.");
record NewUserRequest(string UserName, string Password, bool IsAdmin);
record ResetPasswordRequest(string NewPassword);
record CommitRequest(string? Destination, string RelativeName, string Sha256, long? LastModified);

public partial class Program;
