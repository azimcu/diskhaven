using System.Security.Claims;
using System.Threading.RateLimiting;
using HomeVault.Api;
using HomeVault.Core.Accounts;
using HomeVault.Core.Storage;
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
var uploadDir = Path.Combine(root.FullPath, VaultRoot.InternalFolderName, "uploads");
Directory.CreateDirectory(uploadDir);
var tusStore = new TusDiskStore(uploadDir);

builder.Services.AddSingleton(root);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FileBrowser>();
builder.Services.AddSingleton<FileOperations>();
builder.Services.AddSingleton<UploadCommitter>();
builder.Services.AddSingleton(new AccountStore(configDir));
builder.Services.AddSingleton(tusStore);
builder.Services.AddHostedService<Housekeeping>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

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
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1) }));
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

auth.MapPost("/password", (PasswordRequest req, ClaimsPrincipal user, AccountStore accounts) =>
{
    try
    {
        accounts.ChangePassword(user.Identity!.Name!, req.CurrentPassword, req.NewPassword);
        return Results.NoContent();
    }
    catch (UnauthorizedAccessException e) { return Results.Problem(e.Message, statusCode: 401); }
    catch (ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
}).RequireAuthorization().RequireRateLimiting("login");

// ---- files ------------------------------------------------------------------------------
var files = app.MapGroup("/api/files").RequireAuthorization();

files.MapGet("/", (string? path, FileBrowser browser) =>
    Guard(() => Results.Ok(new { path = VaultRoot.Normalize(path), entries = browser.List(path) })));

files.MapGet("/content", (string path, bool? download, FileBrowser browser) => Guard(() =>
{
    var full = browser.GetFile(path);
    if (!contentTypes.TryGetContentType(full, out var type))
        type = "application/octet-stream";
    // Range requests let video players seek and let interrupted downloads resume.
    return Results.File(full, type, download == true ? Path.GetFileName(full) : null,
        lastModified: File.GetLastWriteTimeUtc(full), enableRangeProcessing: true);
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

app.MapGet("/api/storage", (VaultRoot vault) =>
{
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

static Task SignIn(HttpContext ctx, Account account) =>
    ctx.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, account.UserName), new Claim(ClaimTypes.Role, account.IsAdmin ? "admin" : "user")],
        CookieAuthenticationDefaults.AuthenticationScheme)),
        new AuthenticationProperties { IsPersistent = true });

static IResult Guard(Func<IResult> action)
{
    try { return action(); }
    catch (VaultPathException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
    catch (VaultConflictException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
    { return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound); }
    catch (IOException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict); }
}

record SetupRequest(string Token, string UserName, string Password);
record LoginRequest(string UserName, string Password);
record PasswordRequest(string CurrentPassword, string NewPassword);
record FolderRequest(string? Parent, string Name);
record RenameRequest(string Path, string NewName);
record TransferRequest(string[] Paths, string? Destination);
record DeleteRequest(string[] Paths);
record CommitRequest(string? Destination, string RelativeName, string Sha256, long? LastModified);

public partial class Program;
