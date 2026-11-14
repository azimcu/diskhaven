using System.Globalization;
using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Sync;

/// <summary>
/// Carries out a <see cref="SyncPlan"/> on a backup folder.
/// - Every file goes through <see cref="VerifiedCopy"/>: written under a temporary name in the target disk's
///   internal folder, flushed, read back from the disk and compared by SHA-256, then renamed into place.
/// - Nothing is deleted outright: a file or folder that disappears or is replaced on the backup goes to the
///   target disk's own recycle bin (<c>.diskhaven/trash</c>) and is removed after 30 days.
/// - Errors on single files are collected and reported; the rest of the sync continues.
/// The plan is written below <c>targetPrefix</c> inside the target disk; the recycle bin and temporary files
/// stay at the root of that disk, so the normal cleanup finds them.
/// </summary>
public sealed class SyncExecutor(TimeProvider clock)
{
    /// <summary>Recycle bin folder used by versions before the multi-disk backup; only cleaned up now.</summary>
    public const string TrashFolder = ".diskhaven-trash";
    public const string LegacyTrashFolder = ".homevault-trash";
    /// <summary>Temporary copies, relative to the target disk's root.</summary>
    public const string TempFolder = VaultRoot.InternalFolderName + "/tmp";
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);
    /// <summary>Free space that must stay on the target while a backup runs; below this the job stops.</summary>
    private const long MinFree = 64L << 20;

    public Task<SyncReport> ExecuteAsync(SyncPlan plan, string sourceRoot, string targetRoot,
        IProgress<SyncProgress>? progress = null, CancellationToken ct = default) =>
        ExecuteAsync(plan, sourceRoot, new VaultRoot(targetRoot), "", progress, ct);

    /// <summary>
    /// Runs the plan. <paramref name="target"/> is the target disk; <paramref name="targetPrefix"/> is the
    /// folder inside it that holds the mirror ("" for the root). <paramref name="disksLost"/> is asked before every
    /// step and returns why the job must stop (a disk is gone), or null.
    /// </summary>
    public async Task<SyncReport> ExecuteAsync(SyncPlan plan, string sourceRoot, VaultRoot target,
        string targetPrefix = "", IProgress<SyncProgress>? progress = null, CancellationToken ct = default,
        Func<string?>? disksLost = null)
    {
        target.EnsureWritable();
        targetPrefix = VaultRoot.Normalize(targetPrefix);
        var targetBase = target.Resolve(targetPrefix);
        // each run has its own subfolder: uploads, ZIP jobs and copies on the same disk share TempFolder,
        // and the cleanup at the end must only remove this run's files
        var tempParent = Path.Combine(target.FullPath, TempFolder);
        CleanStaleRuns(tempParent, clock.GetUtcNow().UtcDateTime);
        var temp = Path.Combine(tempParent, RunFolderPrefix + Guid.NewGuid().ToString("N"));
        var trash = new FileOperations(target, clock);
        var started = clock.GetUtcNow();

        // the temporary folder lives on the target disk: if it disappears, the disk was unplugged
        if (plan.Actions.Count > 0)
            Directory.CreateDirectory(temp);

        string? TargetProblem()
        {
            if (!Directory.Exists(temp))
                return "Hedef disk kayboldu (çıkarıldı ya da bağlantısı koptu). İş durduruldu; hedefteki dosyalar olduğu gibi kaldı.";
            if (plan.BytesToCopy == 0)
                return null;
            try
            {
                if (new DriveInfo(target.FullPath).AvailableFreeSpace < MinFree)
                    return "Hedef diskte yer kalmadı. İş durduruldu; yazılan dosyalar doğrulandı.";
            }
            catch (Exception e) when (e is IOException or ArgumentException)
            {
                return "Hedef disk okunamadı. İş durduruldu.";
            }
            return null;
        }

        var errors = new List<SyncError>();
        var renamed = new List<string>();
        int created = 0, copied = 0, updated = 0, deleted = 0, verified = 0, moved = 0, done = 0;
        long bytesDone = 0, bytesTotal = plan.BytesToCopy;
        var total = plan.Actions.Count;
        var canceled = false;
        var lost = false;

        void Report(string phase, string? current) =>
            progress?.Report(new SyncProgress(phase, done, total, bytesDone, bytesTotal, current));

        // an entry the plan wants gone: only truly empty folders are removed without a recycle bin entry
        void Remove(string path, string rel)
        {
            if (Directory.Exists(path) && !Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any())
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            if (File.Exists(path) || Directory.Exists(path))
            {
                trash.Delete([TargetRelative(targetPrefix, rel)]);
                deleted++;
            }
        }

        foreach (var action in plan.Actions)
        {
            if (ct.IsCancellationRequested) { canceled = true; break; }
            // a pulled disk leaves an empty folder on the system disk: never write there, never go on with deletes
            if (disksLost?.Invoke() is { } gone)
            {
                errors.Add(new SyncError("(disk)", gone));
                lost = true;
                break;
            }
            if (done % 64 == 0 && TargetProblem() is { } problem)
            {
                errors.Add(new SyncError("(hedef disk)", problem));
                break;
            }
            Report("copy", action.Path);
            var targetPath = Combine(targetBase, action.Path);
            try
            {
                switch (action.Kind)
                {
                    case SyncActionKind.CreateFolder:
                        // the source has a folder where the target still has a file: it goes to the recycle bin first
                        if (File.Exists(targetPath))
                        {
                            trash.Delete([TargetRelative(targetPrefix, action.Path)]);
                            deleted++;
                        }
                        Directory.CreateDirectory(targetPath);
                        created++;
                        break;
                    case SyncActionKind.FixCase:
                        var fixedPath = Combine(targetBase, action.TargetPath!);
                        // two-step rename: a case-only rename is a no-op on some filesystems
                        var step = fixedPath + ".diskhaven-case";
                        if (Directory.Exists(targetPath)) { Directory.Move(targetPath, step); Directory.Move(step, fixedPath); }
                        else { File.Move(targetPath, step); File.Move(step, fixedPath); }
                        renamed.Add($"{action.Path} -> {Path.GetFileName(fixedPath)} (yalnızca büyük/küçük harf)");
                        break;
                    case SyncActionKind.Move:
                        var from = Combine(targetBase, action.TargetPath!);
                        if (await SameContentAsync(Combine(sourceRoot, action.Path), from, ct))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                            // the source has a file where the target still has a folder
                            if (Directory.Exists(targetPath))
                            {
                                trash.Delete([TargetRelative(targetPrefix, action.Path)]);
                                deleted++;
                            }
                            File.Move(from, targetPath);
                            moved++;
                        }
                        else
                        {
                            // not the same file after all: normal verified copy, old file to the disk's recycle bin
                            await CopyVerifiedAsync(Combine(sourceRoot, action.Path), targetPath, temp, trash,
                                TargetRelative(targetPrefix, action.TargetPath!), action.Path, renamed, b => { bytesDone += b; Report("copy", action.Path); }, ct);
                            copied++;
                            verified++;
                        }
                        break;
                    case SyncActionKind.Copy:
                    case SyncActionKind.Update:
                        await CopyVerifiedAsync(Combine(sourceRoot, action.Path), targetPath, temp, trash,
                            TargetRelative(targetPrefix, action.Path), action.Path, renamed, b => { bytesDone += b; Report("copy", action.Path); }, ct);
                        verified++;
                        if (action.Kind == SyncActionKind.Copy) copied++; else updated++;
                        break;
                    case SyncActionKind.Delete:
                    case SyncActionKind.DeleteFolder:
                        Remove(targetPath, action.Path);
                        break;
                }
            }
            catch (OperationCanceledException) { canceled = true; break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ChecksumException or VaultPathException)
            {
                errors.Add(new SyncError(action.Path, e.Message));
                if (disksLost?.Invoke() is { } goneNow)
                {
                    errors.Add(new SyncError("(disk)", goneNow));
                    lost = true;
                    break;
                }
                if (TargetProblem() is { } diskProblem)
                {
                    errors.Add(new SyncError("(hedef disk)", diskProblem));
                    break;
                }
            }
            done++;
        }

        Report("cleanup", null);
        TryDelete(temp);
        try { if (Directory.Exists(tempParent) && !Directory.EnumerateFileSystemEntries(tempParent).Any()) Directory.Delete(tempParent); }
        catch (IOException) { }               // another job put a file there in the meantime: it stays
        catch (UnauthorizedAccessException) { }
        if (!lost)
        {
            try { trash.PurgeOlderThan(TrashRetention); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            PurgeTrash(target.FullPath, clock.GetUtcNow());   // leftovers of the old backup layout
        }
        return new SyncReport(started, clock.GetUtcNow(), created, copied, updated, deleted, verified, bytesDone,
            errors, plan.Skipped, canceled, moved, renamed);
    }

    /// <summary>
    /// Copies source to target through <see cref="VerifiedCopy"/> and replaces the old target version safely.
    /// <paramref name="replaceRel"/> is the entry on the target disk that holds the old version (the same path
    /// for an update, the old place for a move that turned out not to be a move).
    /// </summary>
    private static async Task CopyVerifiedAsync(string source, string target, string tempDir, FileOperations trash,
        string replaceRel, string sourceRel, List<string> renamed, Action<long> onBytes, CancellationToken ct)
    {
        var (temp, _) = await VerifiedCopy.ToTempAsync(source, tempDir, onBytes, ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // never overwrite: the previous version waits in the recycle bin for 30 days
            var old = trash.Root.Resolve(replaceRel);
            if (Directory.Exists(old) || File.Exists(old))
                trash.Delete([replaceRel]);
            File.Move(temp, target);
            ReportTrimmedName(target, sourceRel, renamed);
        }
        finally { VerifiedCopy.TryDelete(temp); }
    }

    /// <summary>
    /// FAT and exFAT drop a trailing space from a name without saying so. The file is written and verified;
    /// only its name differs, and the owner sees that in the report.
    /// </summary>
    private static void ReportTrimmedName(string target, string rel, List<string> renamed)
    {
        var name = Path.GetFileName(target);
        if (!name.EndsWith(' '))
            return;
        var parent = Path.GetDirectoryName(target)!;
        var names = Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName).ToList();
        if (names.Contains(name, StringComparer.Ordinal) || !names.Contains(name.TrimEnd(), StringComparer.Ordinal))
            return;
        renamed.Add($"{rel} -> {name.TrimEnd()} (bu disk adın sonundaki boşluğu kabul etmiyor)");
    }

    private static async Task<bool> SameContentAsync(string a, string b, CancellationToken ct)
    {
        if (!File.Exists(a) || !File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length)
            return false;
        await using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var ha = await System.Security.Cryptography.SHA256.HashDataAsync(fa, ct);
        var hb = await System.Security.Cryptography.SHA256.HashDataAsync(fb, ct);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ha, hb);
    }

    /// <summary>Removes the old dated trash folders of the earlier backup layout.</summary>
    public static int PurgeTrash(string targetRoot, DateTimeOffset now)
    {
        var dir = Path.Combine(targetRoot, TrashFolder);
        if (!Directory.Exists(dir))
            return 0;
        var removed = 0;
        foreach (var slot in Directory.EnumerateDirectories(dir))
        {
            if (DateTime.TryParseExact(Path.GetFileName(slot), "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var when) && now - when > TrashRetention)
            {
                TryDelete(slot);
                removed++;
            }
        }
        return removed;
    }

    private static string TargetRelative(string prefix, string rel) =>
        prefix.Length == 0 ? rel : $"{prefix}/{rel}";

    private static string Combine(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private const string RunFolderPrefix = "backup-";

    /// <summary>Removes run folders a crashed or killed backup left behind (older than a day).</summary>
    private static void CleanStaleRuns(string tempParent, DateTime nowUtc)
    {
        try
        {
            if (!Directory.Exists(tempParent))
                return;
            foreach (var d in new DirectoryInfo(tempParent).EnumerateDirectories(RunFolderPrefix + "*"))
                if (d.LinkTarget is null && nowUtc - d.LastWriteTimeUtc > TimeSpan.FromDays(1))
                    TryDelete(d.FullName);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class ChecksumException(string message) : Exception(message);
