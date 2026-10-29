using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Tests;

public sealed class VaultRootTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diskhaven-test-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("diskhaven-outside-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("/", "")]
    [InlineData("Belgeler/Ödev.docx", "Belgeler/Ödev.docx")]
    [InlineData("\\Belgeler\\Alt//dosya.txt", "Belgeler/Alt/dosya.txt")]
    public void Normalize_accepts_valid_paths(string? input, string expected) =>
        Assert.Equal(expected, VaultRoot.Normalize(input));

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("Belgeler/../../x")]
    [InlineData("a/./b")]
    [InlineData("C:/Windows")]
    [InlineData("a\0b")]
    [InlineData(".diskhaven/trash")]
    public void Normalize_rejects_dangerous_paths(string input) =>
        Assert.Throws<VaultPathException>(() => VaultRoot.Normalize(input));

    [Fact]
    public void Resolve_stays_inside_root()
    {
        var root = new VaultRoot(_dir);
        Assert.Equal(Path.Combine(_dir, "a", "b.txt"), root.Resolve("a/b.txt"));
        Assert.Equal(root.FullPath, root.Resolve(""));
    }

    [Fact]
    public void Resolve_rejects_symlink_leading_outside()
    {
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "x");
        Directory.CreateSymbolicLink(Path.Combine(_dir, "link"), _outside);
        var root = new VaultRoot(_dir);
        Assert.Throws<VaultPathException>(() => root.Resolve("link/secret.txt"));
    }

    [Fact]
    public void List_sorts_folders_first_with_natural_order_and_hides_internal_folder()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".diskhaven"));
        Directory.CreateDirectory(Path.Combine(_dir, "Zeta"));
        Directory.CreateDirectory(Path.Combine(_dir, "alfa"));
        File.WriteAllText(Path.Combine(_dir, "dosya10.txt"), "");
        File.WriteAllText(Path.Combine(_dir, "dosya2.txt"), "12345");
        Directory.CreateSymbolicLink(Path.Combine(_dir, "link"), _outside);

        var entries = new FileBrowser(new VaultRoot(_dir)).List("");

        Assert.Equal(["alfa", "Zeta", "dosya2.txt", "dosya10.txt"], entries.Select(e => e.Name));
        Assert.Equal(5, entries[2].Size);
        Assert.Equal("txt", entries[2].Extension);
        Assert.Null(entries[0].Size);
    }

    [Fact]
    public void GetFile_throws_for_missing_file() =>
        Assert.Throws<FileNotFoundException>(() => new FileBrowser(new VaultRoot(_dir)).GetFile("yok.txt"));
}

public class LegacyNameTests
{
    [Fact]
    public void Moves_the_old_internal_folder_on_start()
    {
        var d = Directory.CreateTempSubdirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(d.FullName, ".homevault", "trash"));
            _ = new DiskHaven.Core.Storage.VaultRoot(d.FullName);
            Assert.True(Directory.Exists(Path.Combine(d.FullName, ".diskhaven", "trash")));
            Assert.False(Directory.Exists(Path.Combine(d.FullName, ".homevault")));
        }
        finally { d.Delete(true); }
    }

    [Fact]
    public void Old_backup_disks_keep_their_marker_and_trash()
    {
        var d = Directory.CreateTempSubdirectory();
        try
        {
            new DiskHaven.Core.Sync.DiskMarker("abc", "Yedek", DateTimeOffset.UnixEpoch).Write(d.FullName);
            File.Move(Path.Combine(d.FullName, ".diskhaven-disk.json"), Path.Combine(d.FullName, ".homevault-disk.json"));
            Directory.CreateDirectory(Path.Combine(d.FullName, ".homevault-trash", "2026-10-01"));
            Assert.Equal("abc", DiskHaven.Core.Sync.DiskMarker.Read(d.FullName)?.DiskId);
            Assert.True(Directory.Exists(Path.Combine(d.FullName, ".diskhaven-trash", "2026-10-01")));
        }
        finally { d.Delete(true); }
    }
}
