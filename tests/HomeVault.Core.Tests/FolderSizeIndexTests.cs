using HomeVault.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace HomeVault.Core.Tests;

public sealed class FolderSizeIndexTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("homevault-size-").FullName;
    private readonly VaultRoot _root;
    private readonly FolderSizeIndex _index;
    private readonly FileOperations _ops;

    public FolderSizeIndexTests()
    {
        Directory.CreateDirectory(P("A/B"));
        Directory.CreateDirectory(P("C"));
        File.WriteAllBytes(P("A/a.bin"), new byte[100]);
        File.WriteAllBytes(P("A/B/b.bin"), new byte[50]);
        File.WriteAllBytes(P("kok.bin"), new byte[7]);
        _root = new VaultRoot(_dir);
        _index = new FolderSizeIndex(_root);
        _ops = new FileOperations(_root, new FakeTimeProvider(), _index);
        _index.Scan();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
    private string P(string rel) => Path.Combine(_dir, rel);

    [Fact]
    public void Scan_computes_recursive_totals_and_ignores_internal_folder()
    {
        Directory.CreateDirectory(P(".homevault/trash"));
        File.WriteAllBytes(P(".homevault/trash/x"), new byte[1000]);
        _index.Scan();
        Assert.Equal(new FolderStats(150, 2, 1), _index.Get("A"));
        Assert.Equal(new FolderStats(157, 3, 3), _index.Get(""));
        Assert.Equal(new FolderStats(0, 0, 0), _index.Get("C"));
    }

    [Fact]
    public void Delete_move_copy_and_restore_keep_totals_right()
    {
        _ops.Move(["A/B"], "C");
        Assert.Equal(new FolderStats(100, 1, 0), _index.Get("A"));
        Assert.Equal(new FolderStats(50, 1, 1), _index.Get("C"));
        Assert.Equal(new FolderStats(50, 1, 0), _index.Get("C/B"));

        _ops.Copy(["A"], "C");
        Assert.Equal(new FolderStats(150, 2, 2), _index.Get("C"));

        var trashed = _ops.Delete(["C"]);
        Assert.Null(_index.Get("C"));
        Assert.Equal(new FolderStats(107, 2, 1), _index.Get(""));

        _ops.Restore(trashed[0].Id);
        Assert.Equal(new FolderStats(150, 2, 2), _index.Get("C"));
        Assert.Equal(new FolderStats(257, 4, 4), _index.Get(""));
    }

    [Fact]
    public void Index_matches_a_fresh_scan_after_many_operations()
    {
        _ops.CreateFolder("", "Yeni");
        _ops.Rename("A", "A2");
        _ops.Copy(["A2/B"], "Yeni");
        var item = _ops.Delete(["A2/B/b.bin"])[0];
        _ops.Delete(["A2/B"]);
        _ops.Restore(item.Id); // recreates the missing A2/B
        var incremental = _index.Get("");

        var fresh = new FolderSizeIndex(_root);
        fresh.Scan();
        Assert.Equal(fresh.Get(""), incremental);
        Assert.Equal(fresh.Get("A2"), _index.Get("A2"));
    }

    [Fact]
    public void Listing_shows_folder_size_and_counts()
    {
        var entries = new FileBrowser(_root, _index).List("");
        var a = entries.Single(e => e.Name == "A");
        Assert.Equal(150, a.Size);
        Assert.Equal(2, a.Files);
        Assert.Equal(1, a.Folders);
    }
}
