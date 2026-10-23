using HomeVault.Core.Storage;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

// Configuration: appsettings.json or environment, e.g. HomeVault__DataRoot=/srv/vault
var dataRoot = builder.Configuration["HomeVault:DataRoot"]
    ?? throw new InvalidOperationException("HomeVault:DataRoot is not configured.");
builder.Services.AddSingleton(new VaultRoot(dataRoot));
builder.Services.AddSingleton<FileBrowser>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();

var contentTypes = new FileExtensionContentTypeProvider();
var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok" }));

api.MapGet("/files", (string? path, FileBrowser browser) =>
    Guard(() => Results.Ok(new { path = VaultRoot.Normalize(path), entries = browser.List(path) })));

api.MapGet("/files/content", (string path, bool? download, FileBrowser browser) => Guard(() =>
{
    var full = browser.GetFile(path);
    if (!contentTypes.TryGetContentType(full, out var type))
        type = "application/octet-stream";
    // Range requests let video players seek and let interrupted downloads resume.
    return Results.File(full, type, download == true ? Path.GetFileName(full) : null, enableRangeProcessing: true);
}));

app.Run();

static IResult Guard(Func<IResult> action)
{
    try { return action(); }
    catch (VaultPathException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
    catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
    { return Results.Problem(e.Message, statusCode: StatusCodes.Status404NotFound); }
}

public partial class Program;
