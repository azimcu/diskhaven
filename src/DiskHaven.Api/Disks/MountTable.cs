namespace DiskHaven.Api.Disks;

/// <summary>
/// Reads the kernel's mount table as this process sees it (/proc/self/mountinfo). Used before every use of a disk
/// folder: when a disk is not mounted, its folder under /disks is a plain (or missing) folder on the main disk, and
/// writing there would silently fill the main disk instead of the disk the user chose.
/// </summary>
public sealed class MountTable(IConfiguration config)
{
    private readonly string _file = config["DiskHaven:MountInfo"] ?? "/proc/self/mountinfo";

    public sealed record Mount(string Target, bool ReadOnly);

    /// <summary>The mount whose target is exactly <paramref name="path"/>, or null when nothing is mounted there.</summary>
    public Mount? At(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Mount? found = null;
        string[] lines;
        try { lines = File.ReadAllLines(_file); }
        catch (IOException) { return null; }
        foreach (var line in lines)
        {
            // id parent major:minor root target options [optional fields] - fstype source superoptions
            var parts = line.Split(' ');
            if (parts.Length < 6)
                continue;
            var target = Unescape(parts[4]);
            if (target != path)
                continue;
            var ro = parts[5].Split(',').Contains("ro");
            found = new Mount(target, ro);   // the last line wins: it is the mount on top
        }
        return found;
    }

    /// <summary>mountinfo writes space, tab, newline and backslash as octal escapes.</summary>
    internal static string Unescape(string s) =>
        !s.Contains('\\') ? s : System.Text.RegularExpressions.Regex.Replace(s, @"\\([0-7]{3})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
}
