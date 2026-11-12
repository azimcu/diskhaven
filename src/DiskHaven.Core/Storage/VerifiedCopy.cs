using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DiskHaven.Core.Storage;

/// <summary>
/// Copies one file to another disk so that a bad copy can never take the place of a good file:
/// the bytes go to a temporary file in the target's own internal folder, are flushed to the disk,
/// dropped from the page cache and read back from the disk; only when the SHA-256 of the read-back
/// equals the SHA-256 of the source does the copy get its final name (one rename, never overwriting).
/// </summary>
public static class VerifiedCopy
{
    private const int Buffer = 1 << 20;

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="tempDir"/> (which must be on the target disk) and verifies it.
    /// Returns the temporary file and the source's SHA-256 (hex). The caller renames the file into place.
    /// </summary>
    public static async Task<(string Temp, string Sha256)> ToTempAsync(string source, string tempDir,
        Action<long>? onBytes = null, CancellationToken ct = default)
    {
        SpecialFiles.EnsureNotSpecial(source);   // opening a named pipe would wait forever
        Directory.CreateDirectory(tempDir);
        // FAT32 cannot hold 4 GB or more in one file; say so before writing a single byte
        var before = new FileInfo(source);
        FileSystemLimits.EnsureFileFits(tempDir, before.Length);
        var temp = Path.Combine(tempDir, "copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] sourceHash;
            var mtime = before.LastWriteTimeUtc;
            var size = before.Length;
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
                        onBytes?.Invoke(read);
                    }
                    await output.FlushAsync(ct);
                    output.Flush(flushToDisk: true);
                    DropFromCache(output.SafeFileHandle);
                }
                sourceHash = hasher.GetHashAndReset();
            }
            // the source must not have changed while it was read: compare both size and time, because
            // an appended or truncated file is silently mixed into the hash otherwise
            var after = new FileInfo(source);
            if (after.Length != size || after.LastWriteTimeUtc != mtime)
                throw new IOException($"The file changed while it was being copied: {Path.GetFileName(source)}");

            byte[] copyHash;
            await using (var check = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read, Buffer, useAsync: true))
                copyHash = await SHA256.HashDataAsync(check, ct);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, copyHash))
                throw new IOException($"The copy could not be verified (possible disk error): {Path.GetFileName(source)}");
            File.SetLastWriteTimeUtc(temp, mtime);
            return (temp, Convert.ToHexStringLower(sourceHash));
        }
        catch (ArgumentException e) when (FileSystemLimits.IsFileTooLarge(e))
        {
            // safety net: the pre-check above should already have stopped this
            TryDelete(temp);
            throw new IOException(FileSystemLimits.TooLargeMessage, e);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Verified copy of one file to <paramref name="target"/>, which must not exist yet.</summary>
    public static async Task<string> CopyAsync(string source, string target, string tempDir,
        Action<long>? onBytes = null, CancellationToken ct = default)
    {
        var (temp, hash) = await ToTempAsync(source, tempDir, onBytes, ct);
        try
        {
            File.Move(temp, target); // throws if the name is taken: nothing is overwritten
            return hash;
        }
        finally { TryDelete(temp); }
    }

    /// <summary>
    /// Removes temporary files left in <paramref name="tempDir"/> by a copy that never finished (the disk was pulled
    /// or the server stopped mid-copy). Only files untouched for <paramref name="olderThan"/> go: a running copy
    /// writes to its file all the time. Returns how many were removed.
    /// </summary>
    public static int CleanStale(string tempDir, TimeSpan olderThan, DateTime nowUtc)
    {
        if (!Directory.Exists(tempDir))
            return 0;
        var removed = 0;
        foreach (var f in new DirectoryInfo(tempDir).EnumerateFiles())
            if (f.LinkTarget is null && nowUtc - f.LastWriteTimeUtc > olderThan)
            {
                TryDelete(f.FullName);
                if (!File.Exists(f.FullName))
                    removed++;
            }
        return removed;
    }

    internal static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
