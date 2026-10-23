using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HomeVault.Core.Sync;

/// <summary>
/// Carries out a <see cref="SyncPlan"/> on a backup disk.
/// - Every file is written to a temporary name, flushed, read back from the disk and its SHA-256
///   compared with the source before it is renamed into place.
/// - Nothing is deleted outright: deleted and replaced files are moved into
///   <c>.homevault-trash/&lt;date&gt;/</c> on the backup disk and removed after 30 days.
/// - Errors on single files are collected and reported; the rest of the sync continues.
/// </summary>
public sealed class SyncExecutor(TimeProvider clock)
{
    public const string TrashFolder = ".homevault-trash";
    public const string TempFolder = ".homevault-tmp";
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);
    private const int Buffer = 1 << 20;

    public async Task<SyncReport> ExecuteAsync(SyncPlan plan, string sourceRoot, string targetRoot,
        IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        var started = clock.GetUtcNow();
        var trash = Path.Combine(targetRoot, TrashFolder, started.ToLocalTime().ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture));
        var temp = Path.Combine(targetRoot, TempFolder);
        Directory.CreateDirectory(temp);

        var errors = new List<SyncError>();
        int created = 0, copied = 0, updated = 0, deleted = 0, verified = 0, moved = 0, done = 0;
        long bytesDone = 0, bytesTotal = plan.BytesToCopy;
        var total = plan.Actions.Count;
        var canceled = false;

        void Report(string phase, string? current) =>
            progress?.Report(new SyncProgress(phase, done, total, bytesDone, bytesTotal, current));

        foreach (var action in plan.Actions)
        {
            if (ct.IsCancellationRequested) { canceled = true; break; }
            Report("copy", action.Path);
            var target = Combine(targetRoot, action.Path);
            try
            {
                switch (action.Kind)
                {
                    case SyncActionKind.CreateFolder:
                        Directory.CreateDirectory(target);
                        created++;
                        break;
                    case SyncActionKind.FixCase:
                        var fixedPath = Combine(targetRoot, action.TargetPath!);
                        // two-step rename: a case-only rename is a no-op on some filesystems
                        var step = fixedPath + ".hv-case";
                        if (Directory.Exists(target)) { Directory.Move(target, step); Directory.Move(step, fixedPath); }
                        else { File.Move(target, step); File.Move(step, fixedPath); }
                        break;
                    case SyncActionKind.Move:
                        var from = Combine(targetRoot, action.TargetPath!);
                        if (await SameContentAsync(Combine(sourceRoot, action.Path), from, ct))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            File.Move(from, target);
                            moved++;
                        }
                        else
                        {
                            // not the same file after all: normal verified copy, old file to the disk trash
                            await CopyVerifiedAsync(Combine(sourceRoot, action.Path), target, temp, trash, action.Path, b => { bytesDone += b; Report("copy", action.Path); }, ct);
                            MoveToTrash(from, trash, action.TargetPath!);
                            copied++;
                            deleted++;
                            verified++;
                        }
                        break;
                    case SyncActionKind.Copy:
                    case SyncActionKind.Update:
                        var source = Combine(sourceRoot, action.Path);
                        await CopyVerifiedAsync(source, target, temp, trash, action.Path, b => { bytesDone += b; Report("copy", action.Path); }, ct);
                        verified++;
                        if (action.Kind == SyncActionKind.Copy) copied++; else updated++;
                        break;
                    case SyncActionKind.Delete:
                    case SyncActionKind.DeleteFolder:
                        if (Directory.Exists(target) && !Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any())
                            Directory.Delete(target, recursive: true); // only empty folders left (e.g. after moves): nothing worth keeping
                        else if (File.Exists(target) || Directory.Exists(target))
                        {
                            MoveToTrash(target, trash, action.Path);
                            deleted++;
                        }
                        break;
                }
            }
            catch (OperationCanceledException) { canceled = true; break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ChecksumException)
            {
                errors.Add(new SyncError(action.Path, e.Message));
            }
            done++;
        }

        Report("cleanup", null);
        TryDelete(temp);
        PurgeTrash(targetRoot, clock.GetUtcNow());
        return new SyncReport(started, clock.GetUtcNow(), created, copied, updated, deleted, verified, bytesDone,
            errors, plan.Skipped, canceled, moved);
    }

    /// <summary>Copies source to target through a temporary file and verifies the copy on disk.</summary>
    private static async Task CopyVerifiedAsync(string source, string target, string tempDir, string trash, string rel,
        Action<long> onBytes, CancellationToken ct)
    {
        var temp = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
        try
        {
            byte[] sourceHash;
            var mtime = File.GetLastWriteTimeUtc(source);
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, useAsync: true))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, Buffer, useAsync: true))
                {
                    var buffer = new byte[Buffer];
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        hasher.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        onBytes(read);
                    }
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                    DropFromCache(output.SafeFileHandle);
                }
                sourceHash = hasher.GetHashAndReset();
            }

            // read the copy back from the disk, not from memory
            byte[] copyHash;
            await using (var check = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, useAsync: true))
                copyHash = await SHA256.HashDataAsync(check, ct);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, copyHash))
                throw new ChecksumException($"Kopya doğrulanamadı (disk hatası olabilir): {rel}");

            File.SetLastWriteTimeUtc(temp, mtime);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
                MoveToTrash(target, trash, rel); // keep the previous version for 30 days
            File.Move(temp, target);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static async Task<bool> SameContentAsync(string a, string b, CancellationToken ct)
    {
        if (!File.Exists(a) || !File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length)
            return false;
        await using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, useAsync: true);
        await using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, useAsync: true);
        var ha = await SHA256.HashDataAsync(fa, ct);
        var hb = await SHA256.HashDataAsync(fb, ct);
        return CryptographicOperations.FixedTimeEquals(ha, hb);
    }

    private static void MoveToTrash(string target, string trash, string rel)
    {
        var dest = Combine(trash, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (Directory.Exists(target))
            Directory.Move(target, dest);
        else
            File.Move(target, dest, overwrite: true);
    }

    /// <summary>Removes trash folders on the backup disk older than the retention period.</summary>
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

    private static string Combine(string root, string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_fadvise(IntPtr fd, long offset, long len, int advice);
    private const int POSIX_FADV_DONTNEED = 4;

    /// <summary>Asks Linux to forget cached pages of a flushed file so the verification read hits the disk.</summary>
    private static void DropFromCache(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux())
            return;
        try { posix_fadvise(handle.DangerousGetHandle(), 0, 0, POSIX_FADV_DONTNEED); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }
}

public sealed class ChecksumException(string message) : Exception(message);
