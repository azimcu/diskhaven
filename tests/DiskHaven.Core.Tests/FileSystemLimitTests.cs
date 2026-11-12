using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Tests;

/// <summary>The FAT32 4 GB per-file limit, and the "source changed while copying" check.</summary>
public sealed class FileSystemLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1L << 30)]
    [InlineData(FileSystemLimits.Fat32MaxFileSize)]
    public void Fat32_accepts_files_up_to_four_gib_minus_one(long size) =>
        FileSystemLimits.EnsureFileFits(FileSystemKind.Fat, size);

    [Theory]
    [InlineData(4L << 30)]
    [InlineData(25L << 30)]
    public void Fat32_rejects_four_gib_and_larger(long size)
    {
        var e = Assert.Throws<IOException>(() => FileSystemLimits.EnsureFileFits(FileSystemKind.Fat, size));
        Assert.Equal(FileSystemLimits.TooLargeMessage, e.Message);
    }

    [Theory]
    [InlineData(FileSystemKind.ExFat)]
    [InlineData(FileSystemKind.Ntfs)]
    [InlineData(FileSystemKind.Other)]
    [InlineData(FileSystemKind.Unknown)]
    public void Other_file_systems_have_no_such_limit(FileSystemKind kind) =>
        FileSystemLimits.EnsureFileFits(kind, 100L << 30);

    [Fact]
    public void Only_the_dotnet_file_length_error_is_recognized()
    {
        var real = new ArgumentException("Specified file length was too large for the file system. (Parameter 'value')", "value");
        Assert.True(FileSystemLimits.IsFileTooLarge(real));
        Assert.False(FileSystemLimits.IsFileTooLarge(new ArgumentException("Path is empty.", "path")));
        Assert.False(FileSystemLimits.IsFileTooLarge(new ArgumentException("Specified file length was too large for the file system.")));
    }

    [Fact]
    public void Detection_does_not_call_an_ordinary_folder_fat()
    {
        var dir = Directory.CreateTempSubdirectory("diskhaven-fs-").FullName;
        try
        {
            Assert.NotEqual(FileSystemKind.Fat, FileSystemLimits.Detect(dir));
            // and a huge file is not refused on this file system
            FileSystemLimits.EnsureFileFits(dir, 25L << 30);
        }
        finally { Directory.Delete(dir); }
    }

    [Fact]
    public async Task Source_that_grows_while_being_copied_is_never_verified()
    {
        var dir = Directory.CreateTempSubdirectory("diskhaven-changed-").FullName;
        try
        {
            var source = Path.Combine(dir, "kaynak.bin");
            var tempDir = Path.Combine(dir, "hedef");
            File.WriteAllBytes(source, new byte[3 << 20]);
            var appended = false;
            var e = await Assert.ThrowsAsync<IOException>(() => VerifiedCopy.ToTempAsync(source, tempDir, _ =>
            {
                if (appended)
                    return;
                appended = true;
                using var stream = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.WriteByte(1);
            }, Ct));
            Assert.Contains("changed while it was being copied", e.Message);
            Assert.Empty(Directory.EnumerateFiles(tempDir));   // the half copy is gone
            Assert.Equal((3 << 20) + 1, new FileInfo(source).Length);   // the source is untouched otherwise
        }
        finally
        {
            foreach (var d in Directory.EnumerateDirectories(dir))
                Directory.Delete(d, recursive: true);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Unchanged_source_copies_normally()
    {
        var dir = Directory.CreateTempSubdirectory("diskhaven-same-").FullName;
        try
        {
            var source = Path.Combine(dir, "kaynak.bin");
            File.WriteAllBytes(source, new byte[2 << 20]);
            var (temp, hash) = await VerifiedCopy.ToTempAsync(source, Path.Combine(dir, "hedef"), null, Ct);
            Assert.True(File.Exists(temp));
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))), hash);
        }
        finally
        {
            foreach (var d in Directory.EnumerateDirectories(dir))
                Directory.Delete(d, recursive: true);
            Directory.Delete(dir, recursive: true);
        }
    }
}
