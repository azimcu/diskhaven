using System.Security.Cryptography;

namespace DiskHaven.Core.Storage;

/// <summary>
/// Final step of an upload: checks the received file against the SHA-256 the client computed
/// before sending, then moves it into the user's folder in one rename. A file that does not
/// match is never placed in the vault, so a broken transfer cannot leave a corrupt file behind.
/// </summary>
public sealed class UploadCommitter(VaultRoot root, IVaultChangeListener? listener = null)
{
    public sealed record Result(string Path, string Sha256, long Size);

    /// <param name="receivedFile">Fully received upload. Must be on the same disk as the vault (under .diskhaven).</param>
    /// <param name="destination">Folder the upload was dropped into.</param>
    /// <param name="relativeName">File name, or "Sub/Folder/name.ext" when a whole folder was uploaded.</param>
    /// <param name="expectedSha256">Hex SHA-256 computed by the client.</param>
    /// <param name="lastModified">Original modification time to keep, if the client sent one.</param>
    /// <param name="replaceExisting">When set and a file with the same name exists, this is called with that file's
    /// vault path to move it out of the way (into its version history or the recycle bin) and the upload takes its
    /// place. When null, a taken name gets " (2)".</param>
    public async Task<Result> CommitAsync(string receivedFile, string? destination, string relativeName,
        string expectedSha256, DateTimeOffset? lastModified, CancellationToken ct = default,
        Action<string>? replaceExisting = null)
    {
        var parts = VaultRoot.Normalize(relativeName).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            throw new VaultPathException("File name is empty.");
        foreach (var part in parts)
            FileNames.Validate(part);

        var actual = await HashAsync(receivedFile, ct);
        if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ChecksumMismatchException(expectedSha256, actual);

        var folder = root.Resolve(VaultRoot.Normalize(destination));
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("Destination folder not found.");
        string? createdTop = null;
        foreach (var dir in parts[..^1])
        {
            // reuse an existing folder with the same name in any letter case (NTFS / exFAT semantics)
            var existing = Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder).FirstOrDefault(d => Path.GetFileName(d).Equals(dir, StringComparison.OrdinalIgnoreCase))
                : null;
            if (existing is null)
                createdTop ??= Path.Combine(folder, dir);
            folder = existing ?? Directory.CreateDirectory(Path.Combine(folder, dir)).FullName;
            root.Resolve(root.ToRelative(folder)); // re-check: no symlink on the way
        }

        // make sure the bytes are on the disk before the file becomes visible
        using (var handle = File.OpenHandle(receivedFile, FileMode.Open, FileAccess.ReadWrite))
            RandomAccess.FlushToDisk(handle);
        if (lastModified is { } mtime)
            File.SetLastWriteTimeUtc(receivedFile, mtime.UtcDateTime);
        string target;
        var taken = replaceExisting is null ? null
            : Directory.EnumerateFiles(folder).FirstOrDefault(f => Path.GetFileName(f).Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
        if (taken is not null)
        {
            listener?.Removing(taken);
            replaceExisting!(root.ToRelative(taken));
            target = Path.Combine(folder, parts[^1]);
        }
        else
            target = Path.Combine(folder, FileNames.Unique(folder, parts[^1]));
        File.Move(receivedFile, target);   // throws if the name was taken meanwhile: nothing is overwritten
        listener?.Added(createdTop ?? target);
        return new Result(root.ToRelative(target), actual, new FileInfo(target).Length);
    }

    public static async Task<string> HashAsync(string file, CancellationToken ct = default)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}

public sealed class ChecksumMismatchException(string expected, string actual)
    : Exception($"Checksum mismatch: expected {expected}, received {actual}.")
{
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}
