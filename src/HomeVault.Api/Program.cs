using System.Security.Claims;
using System.Threading.RateLimiting;
using HomeVault.Api;
using HomeVault.Api.Disks;
using HomeVault.Core.Accounts;
using HomeVault.Core.Sharing;
using HomeVault.Core.Storage;
using HomeVault.Core.Sync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.StaticFiles;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Expiration;
using tusdotnet.Stores;

var builder = WebApplication.CreateBuilder(args);

// Configuration: appsettings.json or environment, e.g. HomeVault__DataRoot=/data HomeVault__ConfigDir=/config
var dataRoot = builder.Configuration["HomeVault:DataRoot"]
    ?? throw new InvalidOperationException("HomeVault:DataRoot is not configured.");
var configDir = builder.Configuration["HomeVault:ConfigDir"]
    ?? throw new InvalidOperationException("HomeVault:ConfigDir is not configured.");

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
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Upload chunks are 16 MB; leave headroom.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 64L * 1024 * 1024);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "homevault";
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
        ctx.Request.Headers["X-HomeVault"] != "1")
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("Missing X-HomeVault header.");
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
        return Results.Problem("User name or password is wrong.", statusCode: 401);
    await SignIn(ctx, account);
    return Results.Ok(new { user = account.UserName });
}).RequireRateLimiting("login");

auth.MapPost("/logout", async (HttpContext ctx) =>
{
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
var files = app.MapGroup("/api/files").RequireAuthorization();

files.MapGet("/", (string? path, FileBrowser browser) =>
    Guard(() => Results.Ok(new { path = VaultRoot.Normalize(path), entries = browser.List(path) })));

files.MapGet("/content", (string path, bool? download, HttpContext ctx, FileBrowser browser) => Guard(() =>
{
    return ServeFile(ctx, browser.GetFile(path), download == true);
}));

files.MapPost("/folder", (FolderRequest req, FileOperations ops) =>
    Guard(() => Results.Ok(new { path = ops.CreateFolder(req.Parent, req.Name) })));

files.MapPost("/rename", (RenameRequest req, FileOperations ops) =>
    Guard(() => Results.Ok(new { path = ops.Rename(req.Path, req.NewName) })));

files.MapPost("/move", (TransferRequest req, FileOperations ops) =>
    Guard(() => Results.Ok(new { paths = ops.Move(req.Paths, req.Destination) })));

files.MapPost("/copy", (TransferRequest req, FileOperations ops) =>
    Guard(() => Results.Ok(new { paths = ops.Copy(req.Paths, req.Destination) })));

files.MapPost("/delete", (DeleteRequest req, FileOperations ops) =>
    Guard(() => Results.Ok(new { items = ops.Delete(req.Paths) })));

files.MapGet("/search", (string? path, string q, FileBrowser browser, CancellationToken ct) => Guard(() =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    var r = browser.Search(path, q, 500, timeout.Token);
    return Results.Ok(new { path = VaultRoot.Normalize(path), entries = r.Entries, truncated = r.Truncated });
}));

app.MapGet("/api/storage", (VaultRoot vault) =>
{
    // the demo shows its own quota, not the host disk
    if (demo.Enabled)
        return Results.Ok(new { total = demo.MaxTotalBytes, free = Math.Max(0, demo.MaxTotalBytes - DemoOptions.FolderBytes(vault.FullPath)) });
    var drive = new DriveInfo(vault.FullPath);
    return Results.Ok(new { total = drive.TotalSize, free = drive.AvailableFreeSpace });
}).RequireAuthorization();

// ---- recycle bin ------------------------------------------------------------------------
var trash = app.MapGroup("/api/trash").RequireAuthorization();
trash.MapGet("/", (FileOperations ops) => Results.Ok(ops.ListTrash()));
trash.MapPost("/{id}/restore", (string id, FileOperations ops) => Guard(() => Results.Ok(new { path = ops.Restore(id) })));
trash.MapDelete("/{id}", (string id, FileOperations ops) => Guard(() => { ops.Purge(id); return Results.NoContent(); }));
trash.MapDelete("/", (FileOperations ops) =>
{
    foreach (var item in ops.ListTrash())
        ops.Purge(item.Id);
    return Results.NoContent();
});

// ---- share links ------------------------------------------------------------------------
var sharesApi = app.MapGroup("/api/shares").RequireAuthorization();
sharesApi.MapGet("/", (ShareStore shares) => Results.Ok(shares.List()));
sharesApi.MapPost("/", (ShareRequest req, ClaimsPrincipal user, ShareStore shares, VaultRoot vault) => Guard(() =>
{
    var full = vault.Resolve(req.Path);
    var kind = Directory.Exists(full) ? EntryKind.Folder : File.Exists(full) ? EntryKind.File
        : throw new FileNotFoundException("Item not found.");
    TimeSpan? life = req.Days is > 0 ? TimeSpan.FromDays(req.Days.Value) : null;
    return Results.Ok(shares.Create(req.Path, kind, user.Identity!.Name!, life));
}));
sharesApi.MapDelete("/{id}", (string id, ShareStore shares) => Guard(() => { shares.Revoke(id); return Results.NoContent(); }));

// public side of a link: no login, read-only, only the shared item and what is inside it
var publicApi = app.MapGroup("/api/public/{token}").RequireRateLimiting("public");
publicApi.MapGet("/", (string token, string? path, ShareStore shares, FileBrowser browser, VaultRoot vault) => Guard(() =>
{
    var share = shares.Find(token) ?? throw new KeyNotFoundException("This link has expired or was removed.");
    var target = ShareStore.Inside(share, path);
    var full = vault.Resolve(target);
    var name = Path.GetFileName(full);
    var by = share.CreatedBy;
    if (File.Exists(full))
        return Results.Ok(new { name, kind = EntryKind.File, size = new FileInfo(full).Length, by, expiresAt = share.ExpiresAt, entries = (object?)null });
    var prefix = share.Path.Length + 1;
    var entries = browser.List(target).Where(e => !e.Hidden)
        .Select(e => e with { Path = e.Path[prefix..] });   // paths relative to the shared folder, never the vault
    return Results.Ok(new { name, kind = EntryKind.Folder, size = (long?)null, by, expiresAt = share.ExpiresAt, entries = (object?)entries });
}));
publicApi.MapGet("/content", (string token, string? path, bool? download, HttpContext ctx, ShareStore shares, FileBrowser browser) => Guard(() =>
{
    var share = shares.Find(token) ?? throw new KeyNotFoundException("This link has expired or was removed.");
    var full = browser.GetFile(ShareStore.Inside(share, path));
    if (download == true)
        shares.CountDownload(share.Id);
    return ServeFile(ctx, full, download == true);
}));

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
var disksApi = app.MapGroup("/api/disks").RequireAuthorization("admin");

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

app.MapGet("/api/sync/job", (SyncJobRunner runner) => Results.Ok(runner.Current)).RequireAuthorization("admin");
app.MapPost("/api/sync/job/cancel", (SyncJobRunner runner) => { runner.Cancel(); return Results.NoContent(); }).RequireAuthorization("admin");

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
            const long reserve = 1L << 30; // keep 1 GB free
            if (ctx.UploadLength > free - reserve)
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
    try
    {
        var result = await committer.CommitAsync(Path.Combine(uploadDir, id), req.Destination, req.RelativeName,
            req.Sha256, req.LastModified is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null, ct);
        await tusStore.DeleteFileAsync(id, ct); // removes tus side files; the data file was moved
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
}).RequireAuthorization();

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
}

record SetupRequest(string Token, string UserName, string Password);
record LoginRequest(string UserName, string Password);
record PasswordRequest(string CurrentPassword, string NewPassword);
record FolderRequest(string? Parent, string Name);
record RenameRequest(string Path, string NewName);
record TransferRequest(string[] Paths, string? Destination);
record DeleteRequest(string[] Paths);
record AdoptRequest(string Name);
record DiskSettingsRequest(string? Name, string[]? Folders, SyncSchedule? Schedule, AfterSync? After);
record ShareRequest(string Path, int? Days);
record NewUserRequest(string UserName, string Password, bool IsAdmin);
record ResetPasswordRequest(string NewPassword);
record CommitRequest(string? Destination, string RelativeName, string Sha256, long? LastModified);

public partial class Program;
