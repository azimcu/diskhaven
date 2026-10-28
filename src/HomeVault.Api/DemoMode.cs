namespace HomeVault.Api;

/// <summary>
/// Public demo settings (HomeVault:Demo:*). A demo instance shows its sign-in on the login page,
/// refuses anything that could lock visitors out or reach the host, and caps uploads.
/// The data itself is reset from a clean copy by the host (deploy/demo), not by the app.
/// </summary>
public sealed record DemoOptions(bool Enabled, string UserName, string Password, long MaxUploadBytes, long MaxTotalBytes)
{
    public static DemoOptions From(IConfiguration c) => new(
        c.GetValue("HomeVault:Demo:Enabled", false),
        c["HomeVault:Demo:UserName"] ?? "demo",
        c["HomeVault:Demo:Password"] ?? "",
        c.GetValue("HomeVault:Demo:MaxUploadMB", 20L) << 20,
        c.GetValue("HomeVault:Demo:MaxTotalMB", 400L) << 20);

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
