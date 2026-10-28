using HomeVault.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace HomeVault.Api.Tests;

public class DemoModeTests
{
    static HttpRequest Req(string method, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        return ctx.Request;
    }

    [Theory]
    [InlineData("POST", "/api/disks/abc/mount")]
    [InlineData("DELETE", "/api/disks/abc")]
    [InlineData("POST", "/api/sync/job/cancel")]
    [InlineData("POST", "/api/auth/password")]
    [InlineData("POST", "/api/users")]
    [InlineData("DELETE", "/api/users/demo")]
    [InlineData("POST", "/api/shares")]
    public void Blocks_account_disk_and_share_changes(string method, string path) =>
        Assert.True(DemoOptions.IsBlocked(Req(method, path)));

    [Theory]
    [InlineData("GET", "/api/files")]
    [InlineData("POST", "/api/files/folder")]
    [InlineData("POST", "/api/files/delete")]
    [InlineData("POST", "/api/auth/login")]
    [InlineData("POST", "/api/auth/setup")] // guarded by the secret setup code instead
    [InlineData("POST", "/api/auth/logout")]
    [InlineData("GET", "/api/users")]
    [InlineData("GET", "/api/shares")]
    [InlineData("GET", "/api/disks")]
    [InlineData("GET", "/api/auth/state")]
    public void Allows_normal_file_work(string method, string path) =>
        Assert.False(DemoOptions.IsBlocked(Req(method, path)));

    [Fact]
    public void Reads_settings_with_safe_defaults()
    {
        var off = DemoOptions.From(new ConfigurationBuilder().Build());
        Assert.False(off.Enabled);
        Assert.Equal(20L << 20, off.MaxUploadBytes);

        var on = DemoOptions.From(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HomeVault:Demo:Enabled"] = "true", ["HomeVault:Demo:UserName"] = "misafir",
            ["HomeVault:Demo:Password"] = "x", ["HomeVault:Demo:MaxUploadMB"] = "5",
        }).Build());
        Assert.True(on.Enabled);
        Assert.Equal("misafir", on.UserName);
        Assert.Equal(5L << 20, on.MaxUploadBytes);
    }

    [Fact]
    public void Counts_bytes_in_all_subfolders()
    {
        var d = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(d.FullName, "a"), new byte[100]);
            Directory.CreateDirectory(Path.Combine(d.FullName, "x", "y"));
            File.WriteAllBytes(Path.Combine(d.FullName, "x", "y", "b"), new byte[50]);
            Assert.Equal(150, DemoOptions.FolderBytes(d.FullName));
        }
        finally { d.Delete(true); }
    }
}

public class TelemetryTests
{
    [Theory]
    [InlineData("/api/files/folder", "folder")]
    [InlineData("/api/files/delete", "delete")]
    [InlineData("/api/trash/abc123/restore", "restore")]
    [InlineData("/api/trash/abc123", "purge")]
    [InlineData("/api/trash", "purge")]
    [InlineData("/api/trash/", "purge")]
    public void Names_file_operations_by_path(string path, string expected) =>
        Assert.Equal(expected, Telemetry.OperationName(new PathString(path)));
}
