using HomeVault.Core.Storage;

namespace HomeVault.Core.Sync;

/// <summary>
/// Compares chosen folders of the main disk with a backup disk and lists what has to change
/// so the backup matches. One-way: the main disk is never modified. Only the chosen folders are
/// looked at on the backup disk; anything else on it is left alone.
/// Files are equal when size matches and modification times are within 2 seconds
/// (FAT/exFAT store times less precisely than NTFS or ext4).
/// </summary>
public static class SyncPlanner
{
    public static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);

    public static SyncPlan Plan(string sourceRoot, IEnumerable<string> folders, string targetRoot, CancellationToken ct = default)
    {
        var actions = new List<SyncAction>();
        var skipped = new List<string>();
        var list = NormalizeFolders(folders);
        foreach (var folder in list)
        {
            var source = Path.Combine(sourceRoot, folder);
            if (!Directory.Exists(source))
            {
                skipped.Add($"{folder} (klasör ana diskte yok)");
                continue;
            }
            Compare(source, Path.Combine(targetRoot, folder), folder, actions, skipped, ct);
        }
        return new SyncPlan(list, actions, skipped, DateTimeOffset.UtcNow);
    }

    /// <summary>Removes duplicates and folders already covered by a parent in the list.</summary>
    public static IReadOnlyList<string> NormalizeFolders(IEnumerable<string> folders)
    {
        var normalized = folders.Select(VaultRoot.Normalize).Where(f => f.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f.Length).ToList();
        var result = new List<string>();
        foreach (var f in normalized)
            if (!result.Any(p => f.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
                result.Add(f);
        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Compare(string source, string target, string rel, List<SyncAction> actions, List<string> skipped, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var targetExists = Directory.Exists(target);
        if (!targetExists)
            actions.Add(new SyncAction(SyncActionKind.CreateFolder, rel));

        // target side, case-insensitive like the disks themselves
        var existing = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
        if (targetExists)
            foreach (var info in new DirectoryInfo(target).EnumerateFileSystemInfos())
                if (info.LinkTarget is null)
                    existing[info.Name] = info;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in new DirectoryInfo(source).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            if (info.LinkTarget is not null)
                continue;
            var childRel = $"{rel}/{info.Name}";
            try { FileNames.Validate(info.Name); }
            catch (VaultPathException)
            {
                skipped.Add($"{childRel} (adı Windows/exFAT'ta geçersiz)");
                continue;
            }
            if (!seen.Add(info.Name))
            {
                skipped.Add($"{childRel} (aynı adda, sadece büyük/küçük harfi farklı başka bir öğe var)");
                continue;
            }
            existing.TryGetValue(info.Name, out var other);
            if (other is not null && other.Name != info.Name)
                actions.Add(new SyncAction(SyncActionKind.FixCase, $"{rel}/{other.Name}", TargetPath: childRel));

            if (info is DirectoryInfo dir)
            {
                if (other is FileInfo)
                    actions.Add(new SyncAction(SyncActionKind.Delete, childRel, ((FileInfo)other).Length));
                // compare against the folder as it exists now (its case may still be fixed first)
                var targetDir = other is DirectoryInfo ? other.FullName : Path.Combine(target, info.Name);
                Compare(dir.FullName, targetDir, childRel, actions, skipped, ct);
            }
            else if (info is FileInfo file)
            {
                if (other is DirectoryInfo)
                {
                    actions.Add(new SyncAction(SyncActionKind.DeleteFolder, childRel));
                    actions.Add(new SyncAction(SyncActionKind.Copy, childRel, file.Length));
                }
                else if (other is FileInfo old)
                {
                    if (old.Length != file.Length || (old.LastWriteTimeUtc - file.LastWriteTimeUtc).Duration() > TimeTolerance)
                        actions.Add(new SyncAction(SyncActionKind.Update, childRel, file.Length));
                }
                else
                    actions.Add(new SyncAction(SyncActionKind.Copy, childRel, file.Length));
            }
        }

        // on the backup but no longer on the main disk
        foreach (var (name, info) in existing)
        {
            if (seen.Contains(name))
                continue;
            actions.Add(info is DirectoryInfo
                ? new SyncAction(SyncActionKind.DeleteFolder, $"{rel}/{info.Name}")
                : new SyncAction(SyncActionKind.Delete, $"{rel}/{info.Name}", ((FileInfo)info).Length));
        }
    }
}
