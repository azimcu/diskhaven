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
        return new SyncPlan(list, DetectMoves(actions, targetRoot), skipped, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// A file that disappeared from one place on the backup and appeared in another with the same size
    /// and time was probably renamed or moved: move it on the backup instead of copying it again.
    /// The executor checks the content before moving, so a wrong guess only costs a normal copy.
    /// Actions are also put in a safe order: case fixes, new folders, moves, copies, deletes.
    /// </summary>
    internal static IReadOnlyList<SyncAction> DetectMoves(List<SyncAction> actions, string targetRoot)
    {
        // files that are about to leave the backup: single deletes, and everything inside deleted folders
        var gone = new List<(string Path, long Size, DateTime Mtime, SyncAction? Delete)>();
        foreach (var a in actions)
        {
            if (a.Kind == SyncActionKind.Delete && a.Size > 0)
                gone.Add((a.Path, a.Size, a.MtimeUtc, a));
            else if (a.Kind == SyncActionKind.DeleteFolder)
            {
                var dir = Path.Combine(targetRoot, a.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir))
                    continue;
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                    if (f.Length > 0 && f.LinkTarget is null)
                        gone.Add((a.Path + "/" + Path.GetRelativePath(dir, f.FullName).Replace(Path.DirectorySeparatorChar, '/'),
                            f.Length, f.LastWriteTimeUtc, null));
            }
        }
        var bySize = gone.GroupBy(g => g.Size).ToDictionary(g => g.Key, g => g.ToList());
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<SyncAction>();
        var result = new List<SyncAction>(actions.Count);
        foreach (var a in actions)
        {
            if (a.Kind == SyncActionKind.Copy && a.Size > 0 && bySize.TryGetValue(a.Size, out var candidates))
            {
                var name = a.Path[(a.Path.LastIndexOf('/') + 1)..];
                var match = candidates
                    .Where(c => !used.Contains(c.Path) && (c.Mtime - a.MtimeUtc).Duration() <= TimeTolerance)
                    .OrderByDescending(c => c.Path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)) // same name first
                    .Select(c => ((string Path, long Size, DateTime Mtime, SyncAction? Delete)?)c)
                    .FirstOrDefault();
                if (match is { } m)
                {
                    used.Add(m.Path);
                    if (m.Delete is not null)
                        removed.Add(m.Delete);
                    result.Add(new SyncAction(SyncActionKind.Move, a.Path, a.Size, m.Path, a.MtimeUtc));
                    continue;
                }
            }
            result.Add(a);
        }
        static int Phase(SyncActionKind k) => k switch
        {
            SyncActionKind.FixCase => 0,
            SyncActionKind.CreateFolder => 1,
            SyncActionKind.Move => 2,
            SyncActionKind.Copy or SyncActionKind.Update => 3,
            _ => 4,
        };
        return result.Where(a => !removed.Contains(a)).Select((a, i) => (a, i)).OrderBy(x => Phase(x.a.Kind)).ThenBy(x => x.i).Select(x => x.a).ToList();
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
                    actions.Add(new SyncAction(SyncActionKind.Delete, childRel, ((FileInfo)other).Length, MtimeUtc: ((FileInfo)other).LastWriteTimeUtc));
                // compare against the folder as it exists now (its case may still be fixed first)
                var targetDir = other is DirectoryInfo ? other.FullName : Path.Combine(target, info.Name);
                Compare(dir.FullName, targetDir, childRel, actions, skipped, ct);
            }
            else if (info is FileInfo file)
            {
                if (other is DirectoryInfo)
                {
                    actions.Add(new SyncAction(SyncActionKind.DeleteFolder, childRel));
                    actions.Add(new SyncAction(SyncActionKind.Copy, childRel, file.Length, MtimeUtc: file.LastWriteTimeUtc));
                }
                else if (other is FileInfo old)
                {
                    if (old.Length != file.Length || (old.LastWriteTimeUtc - file.LastWriteTimeUtc).Duration() > TimeTolerance)
                        actions.Add(new SyncAction(SyncActionKind.Update, childRel, file.Length, MtimeUtc: file.LastWriteTimeUtc));
                }
                else
                    actions.Add(new SyncAction(SyncActionKind.Copy, childRel, file.Length, MtimeUtc: file.LastWriteTimeUtc));
            }
        }

        // on the backup but no longer on the main disk
        foreach (var (name, info) in existing)
        {
            if (seen.Contains(name))
                continue;
            actions.Add(info is DirectoryInfo
                ? new SyncAction(SyncActionKind.DeleteFolder, $"{rel}/{info.Name}")
                : new SyncAction(SyncActionKind.Delete, $"{rel}/{info.Name}", ((FileInfo)info).Length, MtimeUtc: ((FileInfo)info).LastWriteTimeUtc));
        }
    }
}
