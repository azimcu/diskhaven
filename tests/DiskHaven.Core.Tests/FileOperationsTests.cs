using DiskHaven.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace DiskHaven.Core.Tests;

public sealed class FileOperationsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diskhaven-ops-").FullName;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FileOperations _ops;

    public FileOperationsTests()
    {
        _ops = new FileOperations(new VaultRoot(_dir), _clock);
        Directory.CreateDirectory(P("Belgeler/Alt"));
        File.WriteAllText(P("Belgeler/not.txt"), "merhaba");
        File.WriteAllText(P("Belgeler/Alt/derin.txt"), "derin");
        Directory.CreateDirectory(P("Hedef"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string rel) => Path.Combine(_dir, rel);

    [Theory]
    [InlineData("a<b")]
    [InlineData("soru?")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("noktalı.")]
    [InlineData("   ")]
    public void Windows_incompatible_names_are_rejected(string name) =>
        Assert.Throws<VaultPathException>(() => _ops.CreateFolder("", name));

    [Fact]
    public void CreateFolder_refuses_existing_name_ignoring_case()
    {
        Assert.Equal("Yeni Klasör", _ops.CreateFolder("", "Yeni Klasör"));
        Assert.Throws<VaultConflictException>(() => _ops.CreateFolder("", "yeni klasör"));
    }

    [Fact]
    public void Rename_allows_case_change_but_not_clash()
    {
        File.WriteAllText(P("Belgeler/diger.txt"), "");
        Assert.Equal("Belgeler/NOT.txt", _ops.Rename("Belgeler/not.txt", "NOT.txt"));
        Assert.Throws<VaultConflictException>(() => _ops.Rename("Belgeler/NOT.txt", "diger.txt"));
    }

    [Fact]
    public void Move_keeps_both_on_clash()
    {
        File.WriteAllText(P("Hedef/not.txt"), "eski");
        var moved = _ops.Move(["Belgeler/not.txt"], "Hedef");
        Assert.Equal(["Hedef/not (2).txt"], moved);
        Assert.Equal("eski", File.ReadAllText(P("Hedef/not.txt")));
        Assert.False(File.Exists(P("Belgeler/not.txt")));
    }

    [Fact]
    public void Folder_cannot_go_into_itself()
    {
        Assert.Throws<VaultConflictException>(() => _ops.Move(["Belgeler"], "Belgeler/Alt"));
        Assert.Throws<VaultConflictException>(() => _ops.Copy(["Belgeler"], "Belgeler"));
    }

    [Fact]
    public void Copy_into_same_folder_names_it_kopya_and_keeps_content_and_time()
    {
        var time = new DateTime(2020, 5, 5, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(P("Belgeler/not.txt"), time);
        Assert.Equal(["Belgeler/not - Kopya.txt"], _ops.Copy(["Belgeler/not.txt"], "Belgeler"));
        Assert.Equal("merhaba", File.ReadAllText(P("Belgeler/not - Kopya.txt")));
        Assert.Equal(time, File.GetLastWriteTimeUtc(P("Belgeler/not - Kopya.txt")));
    }

    [Fact]
    public void Copy_folder_recursively_leaves_no_temp_files()
    {
        _ops.Copy(["Belgeler"], "Hedef");
        Assert.Equal("derin", File.ReadAllText(P("Hedef/Belgeler/Alt/derin.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(P(".diskhaven/tmp")));
    }

    [Fact]
    public void Root_cannot_be_deleted_or_renamed()
    {
        Assert.Throws<VaultPathException>(() => _ops.Delete([""]));
        Assert.Throws<VaultPathException>(() => _ops.Rename("/", "x"));
    }

    [Fact]
    public void Delete_moves_to_trash_and_restore_puts_it_back()
    {
        var item = Assert.Single(_ops.Delete(["Belgeler/Alt"]));
        Assert.False(Directory.Exists(P("Belgeler/Alt")));
        Assert.Equal(EntryKind.Folder, item.Kind);
        Assert.Equal(5, item.Size);
        Assert.Equal("Belgeler/Alt", Assert.Single(_ops.ListTrash()).OriginalPath);

        Assert.Equal("Belgeler/Alt", _ops.Restore(item.Id));
        Assert.Equal("derin", File.ReadAllText(P("Belgeler/Alt/derin.txt")));
        Assert.Empty(_ops.ListTrash());
    }

    [Fact]
    public void Restore_recreates_missing_parent_and_keeps_both_on_clash()
    {
        var item = _ops.Delete(["Belgeler/Alt/derin.txt"])[0];
        Directory.Delete(P("Belgeler/Alt"));
        Assert.Equal("Belgeler/Alt/derin.txt", _ops.Restore(item.Id));

        var again = _ops.Delete(["Belgeler/Alt/derin.txt"])[0];
        File.WriteAllText(P("Belgeler/Alt/derin.txt"), "yeni");
        Assert.Equal("Belgeler/Alt/derin (2).txt", _ops.Restore(again.Id));
    }

    [Fact]
    public void Trash_is_hidden_from_listing_and_unreachable_by_path()
    {
        _ops.Delete(["Belgeler/not.txt"]);
        var names = new FileBrowser(_ops.Root).List("").Select(e => e.Name);
        Assert.DoesNotContain(".diskhaven", names);
        Assert.Throws<VaultPathException>(() => _ops.Root.Resolve(".diskhaven/trash"));
    }

    [Fact]
    public void PurgeOlderThan_removes_only_old_items()
    {
        _ops.Delete(["Belgeler/not.txt"]);
        _clock.Advance(TimeSpan.FromDays(31));
        _ops.Delete(["Belgeler/Alt/derin.txt"]);
        Assert.Equal(1, _ops.PurgeOlderThan(TimeSpan.FromDays(30)));
        Assert.Equal("derin.txt", Assert.Single(_ops.ListTrash()).Name);
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("ABCDEF0123456789abcdef0123456789")]
    public void Trash_ids_are_validated(string id) =>
        Assert.Throws<VaultPathException>(() => _ops.Restore(id));
}
