using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Sync;

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
        var stats = new Stats();
        var list = NormalizeFolders(folders);
        foreach (var folder in list)
        {
            var source = Path.Combine(sourceRoot, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(source))
            {
                // no source folder means no comparison at all: nothing on the target is deleted
                skipped.Add($"{folder} (klasör kaynakta yok)");
                continue;
            }
            Compare(source, Path.Combine(targetRoot, folder.Replace('/', Path.DirectorySeparatorChar)), folder, actions, skipped, stats, ct);
        }
        var final = DetectMoves(actions, targetRoot);
        // what leaves the target, in files: a deleted folder is one action but may hold thousands of files,
        // and a file that only moved to another place on the target is not lost
        var deleted = stats.ReplacedFiles + final.Count(a => a.Kind == SyncActionKind.Delete);
        var targetFiles = stats.TargetFiles;
        var deletedFolders = final.Where(a => a.Kind == SyncActionKind.DeleteFolder).ToList();
        foreach (var folder in deletedFolders)
        {
            var inside = CountFiles(Path.Combine(targetRoot, folder.Path.Replace('/', Path.DirectorySeparatorChar)));
            deleted += inside;
            targetFiles += inside;
        }
        deleted -= final.Count(a => a.Kind == SyncActionKind.Move
            && deletedFolders.Any(f => a.TargetPath!.StartsWith(f.Path + "/", StringComparison.Ordinal)));
        return new SyncPlan(list, final, skipped, DateTimeOffset.UtcNow,
            stats.SourceFiles, targetFiles, Math.Max(deleted, 0));
    }

    private sealed class Stats
    {
        public int SourceFiles;
        public int TargetFiles;
        /// <summary>Target files that go to the recycle bin because a file became a folder or the other way round.</summary>
        public int ReplacedFiles;
    }

    private static int CountFiles(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0,
            }).Count();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
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
                // unreadable subfolders are skipped (move detection is only an optimisation; the delete itself
                // reports them), they must not fail the whole plan
                var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", walk))
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

    private static void Compare(string source, string target, string rel, List<SyncAction> actions, List<string> skipped,
        Stats stats, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        List<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(source).EnumerateFileSystemInfos().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // one unreadable folder must not stop the backup, and must never make its target look "deleted"
            skipped.Add($"{rel} (kaynak klasör okunamadı: {e.Message})");
            return;
        }

        var targetExists = Directory.Exists(target);
        if (!targetExists)
            actions.Add(new SyncAction(SyncActionKind.CreateFolder, rel));

        // target side, case-insensitive like the disks themselves
        var existing = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
        if (targetExists)
        {
            try
            {
                foreach (var info in new DirectoryInfo(target).EnumerateFileSystemInfos())
                    if (info.LinkTarget is null && !VaultRoot.IsInternalName(info.Name))
                        existing[info.Name] = info;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{rel} (hedef klasör okunamadı: {e.Message}); bu klasörde hiçbir şey değiştirilmedi");
                return;
            }
            stats.TargetFiles += existing.Values.Count(v => v is FileInfo);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // a source entry that is skipped must protect the same name on the target from the delete pass
        void Keep(string sourceName, string why)
        {
            seen.Add(sourceName);
            if (existing.TryGetValue(sourceName, out var twin))
                seen.Add(twin.Name);
            skipped.Add(why);
        }

        foreach (var info in entries.OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var childRel = $"{rel}/{info.Name}";
            // DiskHaven's own files (markers, trash, temporary copies) are never part of a backup; a same-named
            // internal entry on the target is ours too and must never be deleted by the mirror
            if (VaultRoot.IsInternalName(info.Name))
            {
                seen.Add(info.Name);
                continue;
            }
            if (info.LinkTarget is not null)
            {
                Keep(info.Name, $"{childRel} (sembolik bağlantı; kopyalanmadı)");
                continue;
            }
            // a name that is not valid UTF-8 reaches .NET with U+FFFD in it and cannot be opened by that name
            if (info.Name.Contains('�') && !File.Exists(info.FullName) && !Directory.Exists(info.FullName))
            {
                Keep(info.Name, $"{childRel} (adı geçerli UTF-8 değil; kopyalanmadı)");
                continue;
            }
            try { FileNames.Validate(info.Name); }
            catch (VaultPathException)
            {
                Keep(info.Name, $"{childRel} (adı Windows/exFAT'ta geçersiz)");
                continue;
            }
            if (!seen.Add(info.Name))
            {
                skipped.Add($"{childRel} (aynı adda, sadece büyük/küçük harfi farklı başka bir öğe var; hedef disk ikisini ayırt edemiyor)");
                continue;
            }
            existing.TryGetValue(info.Name, out var other);
            if (other is not null && other.Name != info.Name)
                actions.Add(new SyncAction(SyncActionKind.FixCase, $"{rel}/{other.Name}", TargetPath: childRel));

            if (info is DirectoryInfo dir)
            {
                // the old file goes to the recycle bin when the executor creates the folder (a separate delete
                // action would run later and throw away the folder that was just copied)
                if (other is FileInfo)
                    stats.ReplacedFiles++;
                // compare against the folder as it exists now (its case may still be fixed first)
                var targetDir = other is DirectoryInfo ? other.FullName : Path.Combine(target, info.Name);
                Compare(dir.FullName, targetDir, childRel, actions, skipped, stats, ct);
            }
            else if (info is FileInfo file)
            {
                if (SpecialFiles.IsSpecial(file.FullName))
                {
                    Keep(info.Name, $"{childRel} (özel dosya; içinde veri yok, kopyalanmadı)");
                    continue;
                }
                stats.SourceFiles++;
                if (other is DirectoryInfo)
                {
                    // the old folder goes to the recycle bin when the executor puts the file in its place
                    var inside = CountFiles(other.FullName);
                    stats.ReplacedFiles += inside;
                    stats.TargetFiles += inside;
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

        // on the target but no longer on the source
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
