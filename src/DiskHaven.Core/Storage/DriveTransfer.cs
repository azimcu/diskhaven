namespace DiskHaven.Core.Storage;

public sealed record TransferProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string? Current);

public sealed record TransferError(string Path, string Message);

public sealed record TransferReport(
    IReadOnlyList<string> Created, int FilesCopied, long BytesCopied, int SourcesMovedToTrash,
    IReadOnlyList<TransferError> Errors, IReadOnlyList<string> Skipped, bool Canceled, IReadOnlyList<string>? Renamed = null)
{
    public bool Success => !Canceled && Errors.Count == 0;
}

/// <summary>
/// Copies or moves files and folders from one drive to another (or to another folder of the same drive),
/// one verified file at a time (<see cref="VerifiedCopy"/>).
/// <list type="bullet">
/// <item>Nothing at the destination is ever overwritten: a taken name gets " (2)". Inside a copied folder this
/// happens when the target disk does not tell letter case apart (exFAT, FAT, NTFS from Windows) and the source
/// has "Rapor.txt" and "rapor.txt"; such renames are listed in the report.</item>
/// <item>A move touches the source only after every file of that item was copied and verified and nothing in
/// it was skipped; then the source goes to its own drive's recycle bin (30 days), not away.</item>
/// <item>Free space is checked before the first byte is written; a disk that still fills up stops the job.</item>
/// <item>Symbolic links, pipes, sockets and devices are not copied (they hold no data); they are listed as skipped.</item>
/// <item>A folder that cannot be read or created is reported and the rest goes on.</item>
/// <item>A disk that disappears (unplugged, unmounted) stops the job at the next file: nothing is ever written
/// into the empty folder a missing disk leaves behind.</item>
/// </list>
/// </summary>
public sealed class DriveTransfer(TimeProvider clock)
{
    /// <summary>Space that must stay free on the destination after the transfer.</summary>
    public const long Reserve = 256L << 20;

    /// <summary>Below this much free space after a failed write the destination counts as full and the job stops.</summary>
    private const long FullBelow = 64L << 20;

    public async Task<TransferReport> RunAsync(VaultRoot source, IReadOnlyList<string> paths, VaultRoot destination,
        string? destinationFolder, bool move, IProgress<TransferProgress>? progress = null, CancellationToken ct = default,
        Func<string?>? drivesPresent = null)
    {
        destination.EnsureWritable();
        if (move)
            source.EnsureWritable();
        var dest = destination.Resolve(destinationFolder);
        if (!Directory.Exists(dest))
            throw new DirectoryNotFoundException("Destination folder not found.");

        var items = new List<string>();
        foreach (var p in paths)
        {
            var full = source.Resolve(p);
            if (full == source.FullPath)
                throw new VaultPathException("The root folder cannot be changed.");
            if (!File.Exists(full) && !Directory.Exists(full))
                throw new FileNotFoundException("Item not found.");
            if (new FileInfo(full).LinkTarget is not null)
                throw new VaultPathException("Symbolic links are not served.");
            SpecialFiles.EnsureNotSpecial(full);
            if (Directory.Exists(full) && (dest == full || dest.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new VaultConflictException("A folder cannot be moved or copied into itself.");
            items.Add(full);
        }

        // count first: the free-space check and the progress bar need the totals
        long bytesTotal = 0;
        var filesTotal = 0;
        foreach (var item in items)
            foreach (var (info, _) in Entries(item, source, null, null))
                if (info is FileInfo f)
                {
                    bytesTotal += f.Length;
                    filesTotal++;
                }
        var free = new DriveInfo(dest).AvailableFreeSpace;
        if (bytesTotal > free - Reserve)
            throw new IOException($"Not enough free space on the disk. Needed {bytesTotal} bytes, free {free} bytes.");

        var tempDir = Path.Combine(destination.FullPath, VaultRoot.InternalFolderName, "tmp");
        var errors = new List<TransferError>();
        var skipped = new List<string>();
        var renamed = new List<string>();
        var created = new List<string>();
        // what each source file looked like when it was copied: a move must not throw away later changes
        var copiedSizeAndTime = new Dictionary<string, (long Size, DateTime Mtime)>(StringComparer.Ordinal);
        int filesDone = 0, copied = 0, trashed = 0;
        long bytesDone = 0;
        var canceled = false;
        var stop = false;
        void Report(string? current) => progress?.Report(new TransferProgress(filesDone, filesTotal, bytesDone, bytesTotal, current));

        // a disk that went away (cable, power, unmount) ends the job; checked before every file and folder
        // (one small read of the mount table) and after each failure
        bool Gone(string rel)
        {
            if (drivesPresent?.Invoke() is not { } reason)
                return false;
            errors.Add(new TransferError(rel, reason));
            return stop = true;
        }

        // after a failed write: is the destination full? Then nothing more can be written - stop.
        bool DiskFull(string rel)
        {
            if (Gone(rel))
                return true;
            long left;
            try { left = new DriveInfo(dest).AvailableFreeSpace; } catch (IOException) { return false; }
            if (left >= FullBelow)
                return false;
            errors.Add(new TransferError(rel, "The destination disk is full. The transfer was stopped."));
            return stop = true;
        }

        async Task<bool> CopyOne(string file, string target, string rel)
        {
            if (Gone(rel))
                return false;
            try
            {
                // record the state before the copy: VerifiedCopy then guarantees the file did not change
                // while it was read, and the move checks this record once more before trashing the original
                var before = new FileInfo(file);
                await VerifiedCopy.CopyAsync(file, target, tempDir, b => { bytesDone += b; Report(rel); }, ct);
                copiedSizeAndTime[file] = (before.Length, before.LastWriteTimeUtc);
                copied++;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!DiskFull(rel))
                    errors.Add(new TransferError(rel, e.Message));
                return false;
            }
        }

        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) { canceled = true; break; }
            var itemErrors = errors.Count;
            var itemSkipped = skipped.Count;
            var targetTop = Path.Combine(dest, FileNames.Unique(dest, Path.GetFileName(item)));
            created.Add(destination.ToRelative(targetTop));
            try
            {
                if (File.Exists(item))
                {
                    Report(source.ToRelative(item));
                    await CopyOne(item, targetTop, source.ToRelative(item));
                    filesDone++;
                }
                else
                {
                    Directory.CreateDirectory(targetTop);
                    // where each source folder went; a folder missing here could not be created (reported once)
                    var map = new Dictionary<string, string>(StringComparer.Ordinal) { [item] = targetTop };
                    var folders = new List<(string Source, string Target)> { (item, targetTop) };
                    foreach (var (info, parent) in Entries(item, source, skipped, errors))
                    {
                        if (ct.IsCancellationRequested) { canceled = true; break; }
                        var rel = source.ToRelative(info.FullName);
                        if (!map.TryGetValue(parent, out var parentTarget))
                        {
                            if (info is FileInfo) filesDone++;
                            continue;   // inside a folder that could not be created: that folder's error covers it
                        }
                        var target = Path.Combine(parentTarget, info.Name);
                        if (File.Exists(target) || Directory.Exists(target))
                        {
                            // the target folder is new, so a taken name means the disk sees two source names as one
                            target = Path.Combine(parentTarget, FileNames.Unique(parentTarget, info.Name));
                            renamed.Add($"{rel} -> {Path.GetFileName(target)}");
                        }
                        if (info is DirectoryInfo)
                        {
                            if (Gone(rel))
                                break;
                            try
                            {
                                Directory.CreateDirectory(target);
                                target = ActualName(target, rel, renamed);
                                map[info.FullName] = target;
                                folders.Add((info.FullName, target));
                            }
                            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                            {
                                if (!DiskFull(rel))
                                    errors.Add(new TransferError(rel, "The folder could not be created on the destination: " + e.Message));
                            }
                        }
                        else
                        {
                            Report(rel);
                            if (await CopyOne(info.FullName, target, rel))
                                ActualName(target, rel, renamed);
                            filesDone++;
                        }
                        if (stop)
                            break;
                    }
                    // folder times last: creating files inside a folder changes its time
                    for (var i = folders.Count - 1; i >= 0; i--)
                        TrySetTime(folders[i].Source, folders[i].Target);
                }
            }
            catch (OperationCanceledException) { canceled = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!DiskFull(source.ToRelative(item)))
                    errors.Add(new TransferError(source.ToRelative(item), e.Message));
            }
            if (canceled || stop)
                break;
            // move: the source leaves only when all of it arrived intact
            if (move && errors.Count == itemErrors && skipped.Count > itemSkipped)
                errors.Add(new TransferError(source.ToRelative(item),
                    "Copied, but the original was kept: it contains links or special files that cannot be copied."));
            else if (move && errors.Count == itemErrors)
            {
                // the copy was made from an older state: never trash the original on top of a newer change
                if (ChangedSinceCopy(item, copiedSizeAndTime) is { } changed)
                {
                    errors.Add(new TransferError(source.ToRelative(changed),
                        "The original was kept because it changed while it was being copied."));
                    continue;
                }
                try
                {
                    new FileOperations(source, clock).Delete([source.ToRelative(item)]);
                    trashed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors.Add(new TransferError(source.ToRelative(item), "Copied, but the original could not be moved to the recycle bin: " + e.Message));
                }
            }
        }
        Report(null);
        return new TransferReport(created, copied, bytesDone, trashed, errors, skipped, canceled, renamed);
    }

    /// <summary>
    /// Compares the source with how it looked when each file was copied. Returns the path of the first
    /// file that is new, changed or gone (also when its folder cannot be read any more), else null.
    /// A move uses this just before sending the original to the recycle bin: a change that arrived after
    /// the copy was verified must keep the original, otherwise the copy would silently lose it.
    /// </summary>
    private static string? ChangedSinceCopy(string item, Dictionary<string, (long Size, DateTime Mtime)> copied)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? Changed(string file)
        {
            seen.Add(file);
            if (!copied.TryGetValue(file, out var was))
                return file;   // a file that was not there when the copy ran
            try
            {
                var now = new FileInfo(file);
                return now.Length == was.Size && now.LastWriteTimeUtc == was.Mtime ? null : file;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return file; }
        }

        if (File.Exists(item))
            return Changed(item);
        var pending = new Stack<string>();
        pending.Push(item);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            List<FileSystemInfo> children;
            try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return dir; }
            foreach (var info in children)
            {
                if (info.LinkTarget is not null || (info is FileInfo && SpecialFiles.IsSpecial(info.FullName)))
                    continue;   // links and special files were skipped by the copy and travel with the folder itself
                if (info is DirectoryInfo)
                    pending.Push(info.FullName);
                else if (Changed(info.FullName) is { } changed)
                    return changed;
            }
        }
        foreach (var file in copied.Keys)
            if ((file == item || file.StartsWith(item + Path.DirectorySeparatorChar, StringComparison.Ordinal)) && !seen.Contains(file))
                return file;   // deleted after it was copied
        return null;
    }

    /// <summary>
    /// Everything under a folder, each folder before its contents, with the folder it is in. Symbolic links,
    /// pipes, sockets and devices go to <paramref name="skipped"/>; a folder that cannot be listed goes to
    /// <paramref name="errors"/> and the walk goes on. A single file yields just itself.
    /// </summary>
    private static IEnumerable<(FileSystemInfo Info, string Parent)> Entries(string item, VaultRoot root,
        List<string>? skipped, List<TransferError>? errors)
    {
        if (File.Exists(item))
        {
            yield return (new FileInfo(item), Path.GetDirectoryName(item)!);
            yield break;
        }
        var pending = new Queue<string>();
        pending.Enqueue(item);
        while (pending.Count > 0)
        {
            var dir = pending.Dequeue();
            List<FileSystemInfo> children;
            try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors?.Add(new TransferError(root.ToRelative(dir), "The folder could not be read: " + e.Message));
                continue;
            }
            foreach (var info in children.OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                if (info.LinkTarget is not null)
                {
                    skipped?.Add(root.ToRelative(info.FullName) + " (symbolic link)");
                    continue;
                }
                // a name that is not valid UTF-8 reaches .NET with U+FFFD in it and cannot be opened by that name
                if (info.Name.Contains('\uFFFD') && !File.Exists(info.FullName) && !Directory.Exists(info.FullName))
                {
                    errors?.Add(new TransferError(root.ToRelative(info.FullName),
                        "The name of this item is not valid UTF-8 (often from an old Windows or Linux system); it was not copied. Rename it on its own computer first."));
                    continue;
                }
                if (info is DirectoryInfo)
                    pending.Enqueue(info.FullName);
                else if (SpecialFiles.IsSpecial(info.FullName))
                {
                    skipped?.Add(root.ToRelative(info.FullName) + " (special file)");
                    continue;
                }
                yield return (info, dir);
            }
        }
    }

    /// <summary>
    /// Windows filesystems (exFAT, FAT, NTFS from Windows) cannot keep a name that ends in a dot or a space, and
    /// Linux drops the end without saying so ("not." becomes "not"). Such names are looked up as the folder
    /// lists them, and a changed name is reported like any other rename. Returns the path as it really is.
    /// </summary>
    private static string ActualName(string target, string rel, List<string> renamed)
    {
        var name = Path.GetFileName(target);
        if (!name.EndsWith('.') && !name.EndsWith(' '))
            return target;
        var parent = Path.GetDirectoryName(target)!;
        var names = Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName).ToList();
        if (names.Contains(name, StringComparer.Ordinal))
            return target;
        var trimmed = name.TrimEnd('.', ' ');
        var actual = names.FirstOrDefault(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase)) ?? trimmed;
        renamed.Add($"{rel} -> {actual} (this disk does not allow a name that ends in a dot or a space)");
        return Path.Combine(parent, actual);
    }

    private static void TrySetTime(string source, string target)
    {
        try { Directory.SetLastWriteTimeUtc(target, Directory.GetLastWriteTimeUtc(source)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
