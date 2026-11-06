using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DiskHaven.Api.Tests;

/// <summary>
/// Runs the real API in memory (no network, no containers): a temporary vault and config folder,
/// background services removed so a test never touches the host's disks or process table.
/// The first account is created through the same setup endpoint a user would use.
/// </summary>
public sealed class DiskHavenFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("diskhaven-it-").FullName;

    public string DataRoot => Path.Combine(_root, "data");
    public string ConfigDir => Path.Combine(_root, "config");
    private string SetupTokenPath => Path.Combine(ConfigDir, "setup-token.txt");

    public const string UserName = "azim";
    public const string Password = "test-parolasi-123";

    /// <summary>Signed in by the setup call, so the tests spend no login attempts on the shared client.</summary>
    public HttpClient Admin { get; private set; } = null!;

    public DiskHavenFactory()
    {
        // Program reads these while the host is built, before WebApplicationFactory's own configuration callbacks.
        Environment.SetEnvironmentVariable("DiskHaven__DataRoot", DataRoot);
        Environment.SetEnvironmentVariable("DiskHaven__ConfigDir", ConfigDir);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ConfigDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DiskHaven:DataRoot"] = DataRoot,
            ["DiskHaven:ConfigDir"] = ConfigDir,
        }));
        builder.ConfigureTestServices(services =>
        {
            // SystemStats, Housekeeping, FolderSizeScanner and SyncScheduler poll the real machine; a test must not.
            foreach (var service in services.Where(d => d.ServiceType == typeof(IHostedService)).ToArray())
                services.Remove(service);
        });
    }

    public async ValueTask InitializeAsync()
    {
        Admin = CreateClient();
        Admin.DefaultRequestHeaders.Add("X-DiskHaven", "1");
        var token = File.ReadAllText(SetupTokenPath).Trim();
        using var response = await Admin.PostAsJsonAsync("/api/auth/setup",
            new { token, userName = UserName, password = Password }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public new async ValueTask DisposeAsync()
    {
        Admin?.Dispose();
        try { await base.DisposeAsync(); }
        catch (InvalidOperationException) { }   // the server never started (setup failure): nothing to stop
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class ApiEndpointsTests : IClassFixture<DiskHavenFactory>
{
    private readonly DiskHavenFactory _factory;
    private readonly HttpClient _admin;

    public ApiEndpointsTests(DiskHavenFactory factory)
    {
        _factory = factory;
        _admin = factory.Admin;
    }

    private HttpClient NewClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-DiskHaven", "1");   // the app's CSRF guard
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    private static string NewName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private async Task<string> CreateFolder(string name)
    {
        using var response = await _admin.PostAsJsonAsync("/api/files/folder",
            new { parent = "", name }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await Json(response)).GetProperty("path").GetString()!;
    }

    [Fact]
    public async Task Health_is_public()
    {
        using var client = NewClient();
        using var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task File_endpoints_require_a_signed_in_user()
    {
        using var client = NewClient();
        using var response = await client.GetAsync("/api/files/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Writes_without_the_csrf_header_are_refused()
    {
        using var client = _factory.CreateClient();   // no X-DiskHaven header
        using var response = await client.PostAsJsonAsync("/api/auth/login",
            new { userName = "kimse", password = "olmayan-parola" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Setup_created_an_admin_and_that_account_is_signed_in()
    {
        using var response = await _admin.GetAsync("/api/auth/state", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await Json(response);
        Assert.False(state.GetProperty("setupRequired").GetBoolean());
        Assert.Equal(DiskHavenFactory.UserName, state.GetProperty("user").GetString());
        Assert.True(state.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task Folder_can_be_created_renamed_trashed_and_restored()
    {
        var name = NewName("klasor");
        await CreateFolder(name);

        using (var list = await _admin.GetAsync("/api/files/", TestContext.Current.CancellationToken))
        {
            var entries = (await Json(list)).GetProperty("entries");
            Assert.Contains(entries.EnumerateArray(), e => e.GetProperty("name").GetString() == name);
        }

        var renamed = name + "-yeni";
        using (var response = await _admin.PostAsJsonAsync("/api/files/rename",
            new { path = name, newName = renamed }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(renamed, (await Json(response)).GetProperty("path").GetString());
        }

        string trashId;
        using (var response = await _admin.PostAsJsonAsync("/api/files/delete",
            new { paths = new[] { renamed } }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            trashId = (await Json(response)).GetProperty("items")[0].GetProperty("id").GetString()!;
        }

        using (var response = await _admin.GetAsync("/api/trash/", TestContext.Current.CancellationToken))
        {
            Assert.Contains((await Json(response)).EnumerateArray(), e => e.GetProperty("id").GetString() == trashId);
        }

        using (var response = await _admin.PostAsJsonAsync($"/api/trash/{trashId}/restore", new { }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var response = await _admin.GetAsync("/api/files/", TestContext.Current.CancellationToken))
        {
            Assert.Contains((await Json(response)).GetProperty("entries").EnumerateArray(),
                e => e.GetProperty("name").GetString() == renamed);
        }
    }

    [Fact]
    public async Task Path_traversal_is_rejected()
    {
        using (var response = await _admin.GetAsync("/api/files/?path=..%2Fgizli", TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var created = await _admin.PostAsJsonAsync("/api/files/folder",
            new { parent = "", name = "../kacak" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    [Fact]
    public async Task Share_link_lists_and_serves_a_file_without_login()
    {
        var folder = NewName("paylas");
        await CreateFolder(folder);
        File.WriteAllText(Path.Combine(_factory.DataRoot, folder, "not.txt"), "merhaba");

        string token;
        using (var response = await _admin.PostAsJsonAsync("/api/shares",
            new { path = folder, days = (int?)null, mode = "View" }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var share = await Json(response);
            token = share.GetProperty("token").GetString()!;
            Assert.False(share.GetProperty("hasPassword").GetBoolean());
        }

        using var anonymous = NewClient();
        using (var response = await anonymous.GetAsync($"/api/public/{token}", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await Json(response);
            Assert.Equal(folder, page.GetProperty("name").GetString());
            Assert.Contains(page.GetProperty("entries").EnumerateArray(),
                e => e.GetProperty("name").GetString() == "not.txt");
        }

        using (var response = await anonymous.GetAsync($"/api/public/{token}/content?path=not.txt", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("merhaba", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Password_share_hides_everything_until_the_password_is_entered()
    {
        var folder = NewName("parola");
        await CreateFolder(folder);

        string token;
        using (var response = await _admin.PostAsJsonAsync("/api/shares",
            new { path = folder, days = (int?)null, mode = "View", password = "gizli123" }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            token = (await Json(response)).GetProperty("token").GetString()!;
        }

        using var anonymous = NewClient();
        using (var response = await anonymous.GetAsync($"/api/public/{token}", TestContext.Current.CancellationToken))
        {
            var page = await Json(response);
            Assert.True(page.GetProperty("locked").GetBoolean());
            Assert.False(page.TryGetProperty("name", out _));
        }

        using (var response = await anonymous.PostAsJsonAsync($"/api/public/{token}/unlock",
            new { password = "gizli123" }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var response = await anonymous.GetAsync($"/api/public/{token}", TestContext.Current.CancellationToken))
        {
            var page = await Json(response);
            Assert.False(page.TryGetProperty("locked", out _));
            Assert.Equal(folder, page.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task Drop_box_accepts_a_file_and_never_shows_the_folder_contents()
    {
        var folder = NewName("kutu");
        await CreateFolder(folder);

        string token;
        using (var response = await _admin.PostAsJsonAsync("/api/shares",
            new { path = folder, days = (int?)null, mode = "UploadOnly" }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            token = (await Json(response)).GetProperty("token").GetString()!;
        }

        using var anonymous = NewClient();
        using (var body = new StringContent("gelen dosya", Encoding.UTF8, "text/plain"))
        using (var response = await anonymous.PostAsync($"/api/public/{token}/upload?name=gelen.txt", body, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var response = await _admin.GetAsync($"/api/files/?path={folder}", TestContext.Current.CancellationToken))
        {
            Assert.Contains((await Json(response)).GetProperty("entries").EnumerateArray(),
                e => e.GetProperty("name").GetString() == "gelen.txt");
        }

        using (var response = await anonymous.GetAsync($"/api/public/{token}", TestContext.Current.CancellationToken))
        {
            var page = await Json(response);
            Assert.Equal(JsonValueKind.Null, page.GetProperty("entries").ValueKind);
        }
    }

    [Fact]
    public async Task A_normal_user_cannot_reach_admin_endpoints()
    {
        var userName = NewName("kullanici");
        using (var response = await _admin.PostAsJsonAsync("/api/users",
            new { userName, password = "ikinci-parola-123", isAdmin = false }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var user = NewClient();
        using (var response = await user.PostAsJsonAsync("/api/auth/login",
            new { userName, password = "ikinci-parola-123" }, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var response = await user.GetAsync("/api/admin/overview", TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await user.GetAsync("/api/users/", TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Removing_a_plugin_takes_its_endpoints_away_and_installing_returns_them()
    {
        try
        {
            using (var response = await _admin.PostAsJsonAsync("/api/admin/plugins/sharing",
                new { installed = false }, TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            using (var response = await _admin.GetAsync("/api/shares/", TestContext.Current.CancellationToken))
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            using var response = await _admin.PostAsJsonAsync("/api/admin/plugins/sharing",
                new { installed = true }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var response = await _admin.GetAsync("/api/shares/", TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
