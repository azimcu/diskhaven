using HomeVault.Core.Accounts;
using HomeVault.Core.Sharing;
using HomeVault.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace HomeVault.Core.Tests;

public sealed class SearchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("homevault-search-").FullName;
    private readonly FileBrowser _browser;

    public SearchTests()
    {
        _browser = new FileBrowser(new VaultRoot(_dir));
        Directory.CreateDirectory(P("Belgeler/Okul/Ödevler"));
        Directory.CreateDirectory(P(".homevault/trash/x"));
        File.WriteAllText(P("Belgeler/Okul/Ödevler/Işık Raporu.pdf"), "a");
        File.WriteAllText(P("Belgeler/fatura_ekim.pdf"), "a");
        File.WriteAllText(P("Belgeler/Okul/notlar.txt"), "a");
        File.WriteAllText(P(".homevault/trash/x/Işık Raporu.pdf"), "çöpteki kopya");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
    private string P(string rel) => Path.Combine(_dir, rel);

    [Theory]
    [InlineData("isik")]          // no Turkish keyboard
    [InlineData("IŞIK rapor")]    // case, several words in any order
    [InlineData("rapor pdf")]
    public void Finds_files_in_subfolders_ignoring_case_and_accents(string q)
    {
        var r = _browser.Search("", q);
        Assert.Equal(["Belgeler/Okul/Ödevler/Işık Raporu.pdf"], r.Entries.Select(e => e.Path));
    }

    [Fact]
    public void Folders_match_too_and_come_first()
    {
        var r = _browser.Search("", "odev");
        Assert.Equal("Belgeler/Okul/Ödevler", Assert.Single(r.Entries).Path);
        Assert.Equal(EntryKind.Folder, r.Entries[0].Kind);
    }

    [Fact]
    public void Search_is_limited_to_the_folder_and_never_shows_the_internal_folder()
    {
        Assert.Empty(_browser.Search("Belgeler/Okul", "fatura").Entries);
        Assert.Single(_browser.Search("Belgeler", "fatura ekim").Entries);
        Assert.DoesNotContain(_browser.Search("", "pdf").Entries, e => e.Path.Contains(".homevault"));
    }

    [Fact]
    public void Hit_limit_marks_the_result_truncated()
    {
        for (var i = 0; i < 12; i++) File.WriteAllText(P($"Belgeler/kopya{i}.txt"), "");
        var r = _browser.Search("", "kopya", max: 5);
        Assert.Equal(5, r.Entries.Count);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Traversal_in_search_folder_is_rejected() =>
        Assert.Throws<VaultPathException>(() => _browser.Search("../", "a"));
}

public sealed class ShareStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("homevault-share-").FullName;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Link_works_until_it_expires_and_then_disappears()
    {
        var store = new ShareStore(_dir, _clock);
        var s = store.Create("Belgeler/fatura.pdf", EntryKind.File, "azim", TimeSpan.FromDays(7));
        Assert.True(s.Token.Length >= 40);
        Assert.Equal(s.Id, store.Find(s.Token)?.Id);
        _clock.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        Assert.Null(store.Find(s.Token));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Revoked_or_unknown_tokens_give_nothing()
    {
        var store = new ShareStore(_dir, _clock);
        var s = store.Create("Belgeler", EntryKind.Folder, "azim", null);
        Assert.Null(store.Find(s.Token[..^1] + (s.Token[^1] == 'A' ? 'B' : 'A')));
        Assert.Null(store.Find(""));
        store.Revoke(s.Id);
        Assert.Null(store.Find(s.Token));
        Assert.Throws<KeyNotFoundException>(() => store.Revoke(s.Id));
    }

    [Fact]
    public void Paths_inside_a_shared_folder_cannot_leave_it()
    {
        var folder = new ShareStore(_dir, _clock).Create("Belgeler/Okul", EntryKind.Folder, "azim", null);
        Assert.Equal("Belgeler/Okul", ShareStore.Inside(folder, ""));
        Assert.Equal("Belgeler/Okul/Ödevler/a.pdf", ShareStore.Inside(folder, "Ödevler/a.pdf"));
        Assert.Throws<VaultPathException>(() => ShareStore.Inside(folder, "../Gizli"));
        Assert.Throws<VaultPathException>(() => ShareStore.Inside(folder, "a/../../b"));
        Assert.Throws<VaultPathException>(() => ShareStore.Inside(folder, ".homevault/trash"));

        var file = new ShareStore(_dir, _clock).Create("Belgeler/a.pdf", EntryKind.File, "azim", null);
        Assert.Throws<VaultPathException>(() => ShareStore.Inside(file, "x"));
    }

    [Fact]
    public void The_whole_vault_cannot_be_shared() =>
        Assert.Throws<VaultPathException>(() => new ShareStore(_dir, _clock).Create("/", EntryKind.Folder, "azim", null));

    [Fact]
    public void Links_survive_a_restart()
    {
        var s = new ShareStore(_dir, _clock).Create("Müzik", EntryKind.Folder, "azim", null);
        Assert.Equal("Müzik", new ShareStore(_dir, _clock).Find(s.Token)?.Path);
    }
}

public sealed class UserManagementTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("homevault-users-").FullName;
    private readonly AccountStore _store;

    public UserManagementTests()
    {
        _store = new AccountStore(_dir);
        _store.CreateFirst(File.ReadAllText(Path.Combine(_dir, "setup-token.txt")), "azim", "uzun-bir-parola");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Admin_adds_a_second_user_who_can_log_in()
    {
        _store.Create("esim", "baska-uzun-parola", isAdmin: false);
        Assert.False(_store.Authenticate("esim", "baska-uzun-parola")!.IsAdmin);
        Assert.Throws<InvalidOperationException>(() => _store.Create("ESIM", "baska-uzun-parola", false));
        Assert.Throws<ArgumentException>(() => _store.Create("x", "baska-uzun-parola", false));
        Assert.Throws<ArgumentException>(() => _store.Create("kisa", "123", false));
    }

    [Fact]
    public void Last_admin_cannot_be_removed_but_others_can()
    {
        _store.Create("esim", "baska-uzun-parola", false);
        Assert.Throws<InvalidOperationException>(() => _store.Delete("azim"));
        _store.Delete("esim");
        Assert.Null(_store.Find("esim"));
        Assert.Throws<KeyNotFoundException>(() => _store.Delete("esim"));
    }

    [Fact]
    public void Password_reset_changes_the_session_stamp()
    {
        var before = AccountStore.Stamp(_store.Find("azim")!);
        _store.SetPassword("azim", "yepyeni-uzun-parola");
        Assert.NotEqual(before, AccountStore.Stamp(_store.Find("azim")!));
        Assert.NotNull(_store.Authenticate("azim", "yepyeni-uzun-parola"));
        Assert.Null(_store.Authenticate("azim", "uzun-bir-parola"));
    }
}
