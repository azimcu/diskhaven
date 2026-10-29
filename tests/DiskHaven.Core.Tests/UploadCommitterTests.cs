using System.Security.Cryptography;
using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Tests;

public sealed class UploadCommitterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diskhaven-up-").FullName;
    private readonly UploadCommitter _committer;
    private readonly string _incoming;

    public UploadCommitterTests()
    {
        _committer = new UploadCommitter(new VaultRoot(_dir));
        _incoming = Path.Combine(_dir, ".diskhaven", "uploads");
        Directory.CreateDirectory(_incoming);
        Directory.CreateDirectory(Path.Combine(_dir, "Hedef"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (string File, string Sha) Received(byte[] content)
    {
        var file = Path.Combine(_incoming, Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(file, content);
        return (file, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    [Fact]
    public async Task Matching_checksum_places_file_with_original_time()
    {
        var (file, sha) = Received("içerik"u8.ToArray());
        var mtime = new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var result = await _committer.CommitAsync(file, "Hedef", "Fotoğraf.jpg", sha.ToUpperInvariant(), mtime);

        Assert.Equal("Hedef/Fotoğraf.jpg", result.Path);
        Assert.Equal(sha, result.Sha256);
        Assert.False(File.Exists(file));
        Assert.Equal(mtime.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(_dir, "Hedef", "Fotoğraf.jpg")));
    }

    [Fact]
    public async Task Wrong_checksum_never_reaches_the_vault()
    {
        var (file, _) = Received([1, 2, 3]);
        var wrong = Convert.ToHexStringLower(SHA256.HashData([1, 2, 4]));
        await Assert.ThrowsAsync<ChecksumMismatchException>(() => _committer.CommitAsync(file, "Hedef", "a.bin", wrong, null));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_dir, "Hedef")));
    }

    [Fact]
    public async Task Folder_upload_creates_subfolders_reusing_existing_case()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "Hedef", "Tatil"));
        var (file, sha) = Received([9]);
        var result = await _committer.CommitAsync(file, "Hedef", "TATIL/Gün 1/video.mp4", sha, null);
        Assert.Equal("Hedef/Tatil/Gün 1/video.mp4", result.Path);
    }

    [Fact]
    public async Task Existing_file_is_kept_and_upload_gets_new_name()
    {
        File.WriteAllText(Path.Combine(_dir, "Hedef", "a.txt"), "eski");
        var (file, sha) = Received("yeni"u8.ToArray());
        var result = await _committer.CommitAsync(file, "Hedef", "a.txt", sha, null);
        Assert.Equal("Hedef/a (2).txt", result.Path);
        Assert.Equal("eski", File.ReadAllText(Path.Combine(_dir, "Hedef", "a.txt")));
    }

    [Theory]
    [InlineData("../kaçış.txt")]
    [InlineData("alt/CON")]
    [InlineData("a?.txt")]
    public async Task Bad_names_are_rejected(string name)
    {
        var (file, sha) = Received([1]);
        await Assert.ThrowsAsync<VaultPathException>(() => _committer.CommitAsync(file, "Hedef", name, sha, null));
        Assert.True(File.Exists(file));
    }
}
