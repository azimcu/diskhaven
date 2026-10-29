using System.IO.Compression;
using DiskHaven.Core.Storage;

namespace DiskHaven.Core.Tests;

public sealed class ArchivesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diskhaven-zip-").FullName;
    private readonly Archives _zip;

    public ArchivesTests()
    {
        _zip = new Archives(new VaultRoot(_dir));
        Directory.CreateDirectory(P("Belgeler/Alt"));
        Directory.CreateDirectory(P("Belgeler/Boş"));
        File.WriteAllText(P("Belgeler/not.txt"), "merhaba");
        File.WriteAllText(P("Belgeler/Alt/derin.txt"), "derin");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
    private string P(string rel) => Path.Combine(_dir, rel);

    [Fact]
    public void Compress_then_extract_gives_the_same_tree()
    {
        var zip = _zip.Compress(["Belgeler"]);
        Assert.Equal("Belgeler.zip", zip);
        var folder = _zip.Extract(zip, long.MaxValue);
        Assert.Equal("Belgeler (2)", folder);   // "Belgeler" exists: a numbered folder, like Explorer
        Assert.Equal("merhaba", File.ReadAllText(P("Belgeler (2)/Belgeler/not.txt")));
        Assert.Equal("derin", File.ReadAllText(P("Belgeler (2)/Belgeler/Alt/derin.txt")));
        Assert.True(Directory.Exists(P("Belgeler (2)/Belgeler/Boş")));   // empty folders survive
    }

    [Fact]
    public void Second_archive_of_the_same_item_gets_a_new_name()
    {
        _zip.Compress(["Belgeler/not.txt"]);
        Assert.Equal("Belgeler/not (2).zip", _zip.Compress(["Belgeler/not.txt"]));
    }

    [Fact]
    public void Several_items_must_share_a_folder()
    {
        Assert.Throws<VaultPathException>(() => _zip.Compress(["Belgeler/not.txt", "Belgeler/Alt/derin.txt"]));
    }

    [Theory]
    [InlineData("../kacak.txt")]
    [InlineData("a/../../kacak.txt")]
    [InlineData(".diskhaven/x.txt")]
    public void Unsafe_entries_are_refused_and_nothing_is_left_behind(string entryName)
    {
        using (var z = ZipFile.Open(P("kotu.zip"), ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry(entryName).Open())) w.Write("x");
        Assert.Throws<VaultPathException>(() => _zip.Extract("kotu.zip", long.MaxValue));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "kacak.txt")));
        Assert.False(Directory.Exists(P("kotu")));
        Assert.Empty(Directory.GetDirectories(P(".diskhaven/tmp")));
    }

    [Fact]
    public void Extract_checks_free_space_first()
    {
        var zip = _zip.Compress(["Belgeler"]);
        Assert.Throws<IOException>(() => _zip.Extract(zip, 3));
    }

    [Fact]
    public void Not_a_zip_is_a_clear_error()
    {
        File.WriteAllText(P("sahte.zip"), "bu bir zip değil");
        Assert.Throws<IOException>(() => _zip.Extract("sahte.zip", long.MaxValue));
    }

    [Fact]
    public void Hidden_flag_follows_a_rename()
    {
        var full = P("Belgeler/not.txt");
        try { HiddenFlag.Set(full, true); }
        catch (IOException) { return; }   // file system of the test machine has no extended attributes
        Assert.True(HiddenFlag.IsHidden(new FileInfo(full)));
        File.Move(full, P("Belgeler/yeni.txt"));
        Assert.True(HiddenFlag.IsHidden(new FileInfo(P("Belgeler/yeni.txt"))));
        HiddenFlag.Set(P("Belgeler/yeni.txt"), false);
        Assert.False(HiddenFlag.IsHidden(new FileInfo(P("Belgeler/yeni.txt"))));
        Assert.True(HiddenFlag.IsHidden(new FileInfo(P(".diskhaven-gibi"))));   // dot names are always hidden
    }
}
