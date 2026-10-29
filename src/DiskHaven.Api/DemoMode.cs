namespace DiskHaven.Api;

/// <summary>
/// Public demo settings (DiskHaven:Demo:*). A demo instance shows its sign-in on the login page,
/// refuses anything that could lock visitors out or reach the host, and caps uploads.
/// The data itself is reset from a clean copy by the host (deploy/demo), not by the app.
/// </summary>
public sealed record DemoOptions(bool Enabled, string UserName, string Password, long MaxUploadBytes, long MaxTotalBytes)
{
    public static DemoOptions From(IConfiguration c) => new(
        c.GetValue("DiskHaven:Demo:Enabled", false),
        c["DiskHaven:Demo:UserName"] ?? "demo",
        c["DiskHaven:Demo:Password"] ?? "",
        c.GetValue("DiskHaven:Demo:MaxUploadMB", 20L) << 20,
        c.GetValue("DiskHaven:Demo:MaxTotalMB", 400L) << 20);

    /// <summary>Changes a demo visitor may not make: password, accounts, disks, sync and public share links.
    /// Reading stays open so every screen can be shown (a demo has no disk agent, so the disk list is empty).</summary>
    public static bool IsBlocked(HttpRequest r)
    {
        if (HttpMethods.IsGet(r.Method) || HttpMethods.IsHead(r.Method))
            return false;
        var p = r.Path;
        return p.StartsWithSegments("/api/auth/password") || p.StartsWithSegments("/api/users") ||
               p.StartsWithSegments("/api/shares") || p.StartsWithSegments("/api/disks") || p.StartsWithSegments("/api/sync");
    }

    public static long FolderBytes(string path) =>
        new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Sum(f => f.Length);
}
