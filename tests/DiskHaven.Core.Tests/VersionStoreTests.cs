using System.Security.Cryptography;
using DiskHaven.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace DiskHaven.Core.Tests;

public sealed class VersionStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diskhaven-ver-").FullName;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
    private readonly VaultRoot _root;
    private readonly VersionStore _versions;

    public VersionStoreTests()
    {
        _root = new VaultRoot(_dir);
        _versions = new VersionStore(_root, _clock);
        Directory.CreateDirectory(Path.Combine(_dir, "Belgeler"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string rel, string text)
    {
        var full = Path.Combine(_dir, rel);
        File.WriteAllText(full, text);
        return full;
    }

    private async Task<UploadCommitter.Result> Upload(string name, string text, bool replace)
    {
        var incoming = Path.Combine(_dir, ".diskhaven", "uploads");
        Directory.CreateDirectory(incoming);
        var file = Path.Combine(incoming, Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, text);
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        return await new UploadCommitter(_root).CommitAsync(file, "Belgeler", name, sha, null,
            replaceExisting: replace ? old => _versions.Keep(old) : null);
    }

    [Fact]
    public async Task Replacing_upload_keeps_the_old_file_as_a_version()
    {
        Write("Belgeler/rapor.docx", "eski");
        var result = await Upload("Rapor.docx", "yeni", replace: true);

        Assert.Equal("Belgeler/Rapor.docx", result.Path);
        Assert.Equal("yeni", File.ReadAllText(Path.Combine(_dir, "Belgeler", "Rapor.docx")));
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "Belgeler")));
        var v = Assert.Single(_versions.List("Belgeler/rapor.docx"));   // same file whatever the letter case
        Assert.Equal("eski", File.ReadAllText(_versions.Get("Belgeler/Rapor.docx", v.Id)));
        Assert.Equal(4, v.Size);
    }

    [Fact]
    public async Task Without_replace_a_taken_name_still_gets_a_number()
    {
        Write("Belgeler/a.txt", "1");
        var result = await Upload("a.txt", "2", replace: false);
        Assert.Equal("Belgeler/a (2).txt", result.Path);
        Assert.Empty(_versions.List("Belgeler/a.txt"));
    }

    [Fact]
    public void Restore_swaps_and_loses_nothing()
    {
        Write("Belgeler/not.txt", "v1");
        var v1 = _versions.Keep("Belgeler/not.txt");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Write("Belgeler/not.txt", "v2");

        _versions.Restore("Belgeler/not.txt", v1.Id);

        Assert.Equal("v1", File.ReadAllText(Path.Combine(_dir, "Belgeler", "not.txt")));
        var left = Assert.Single(_versions.List("Belgeler/not.txt"));
        Assert.Equal("v2", File.ReadAllText(_versions.Get("Belgeler/not.txt", left.Id)));
    }

    [Fact]
    public void Newest_first_capped_and_purged_by_age()
    {
        for (var i = 0; i < VersionStore.MaxPerFile + 3; i++)
        {
            Write("Belgeler/x.txt", i.ToString());
            _versions.Keep("Belgeler/x.txt");
            _clock.Advance(TimeSpan.FromHours(1));
        }
        var list = _versions.List("Belgeler/x.txt");
        Assert.Equal(VersionStore.MaxPerFile, list.Count);
        Assert.Equal((VersionStore.MaxPerFile + 2).ToString(), File.ReadAllText(_versions.Get("Belgeler/x.txt", list[0].Id)));

        _clock.Advance(TimeSpan.FromDays(30));
        Assert.Equal(VersionStore.MaxPerFile, _versions.PurgeOlderThan(TimeSpan.FromDays(30)));
        Assert.Empty(_versions.List("Belgeler/x.txt"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("0000000000000000000-zzzzzz")]
    [InlineData("")]
    public void Bad_version_ids_are_refused(string id)
    {
        Write("Belgeler/y.txt", "1");
        _versions.Keep("Belgeler/y.txt");
        Assert.Throws<FileNotFoundException>(() => _versions.Get("Belgeler/y.txt", id));
    }

    [Fact]
    public void History_follows_rename_move_and_the_recycle_bin()
    {
        var ops = new FileOperations(_root, _clock, versions: _versions);
        Directory.CreateDirectory(Path.Combine(_dir, "Arşiv"));
        Write("Belgeler/plan.txt", "eski");
        _versions.Keep("Belgeler/plan.txt");
        Write("Belgeler/plan.txt", "yeni");

        ops.Rename("Belgeler/plan.txt", "Plan2.txt");
        Assert.Empty(_versions.List("Belgeler/plan.txt"));
        Assert.Single(_versions.List("Belgeler/Plan2.txt"));

        ops.Move(["Belgeler"], "Arşiv");   // a whole folder: everything below it moves too
        Assert.Single(_versions.List("Arşiv/Belgeler/Plan2.txt"));

        var trashed = Assert.Single(ops.Delete(["Arşiv/Belgeler/Plan2.txt"]));
        Write("Arşiv/Belgeler/Plan2.txt", "başka bir dosya");   // a new file taking the name sees no old history
        Assert.Empty(_versions.List("Arşiv/Belgeler/Plan2.txt"));
        File.Delete(Path.Combine(_dir, "Arşiv", "Belgeler", "Plan2.txt"));

        var back = ops.Restore(trashed.Id);
        Assert.Equal("eski", File.ReadAllText(_versions.Get(back, Assert.Single(_versions.List(back)).Id)));

        var again = Assert.Single(ops.Delete([back]));
        ops.Purge(again.Id);   // gone for good: the history goes too
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_dir, ".diskhaven", "versions")));
    }

    [Fact]
    public void Paths_outside_the_vault_are_refused() =>
        Assert.Throws<VaultPathException>(() => _versions.Keep("../dışarı.txt"));
}
