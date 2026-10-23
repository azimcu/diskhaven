using HomeVault.Core.Accounts;

namespace HomeVault.Core.Tests;

public sealed class AccountStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("homevault-acc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Token() => File.ReadAllText(Path.Combine(_dir, "setup-token.txt")).Trim();

    [Fact]
    public void First_account_needs_the_setup_token_and_token_is_single_use()
    {
        var store = new AccountStore(_dir);
        Assert.False(store.HasAccounts);
        Assert.Throws<UnauthorizedAccessException>(() => store.CreateFirst("yanlis", "azim", "uzun-bir-parola"));

        var token = Token();
        store.CreateFirst(token, "azim", "uzun-bir-parola");
        Assert.True(store.HasAccounts);
        Assert.False(File.Exists(Path.Combine(_dir, "setup-token.txt")));
        Assert.Throws<InvalidOperationException>(() => store.CreateFirst(token, "baska", "uzun-bir-parola"));
    }

    [Fact]
    public void Password_is_stored_hashed_and_verified()
    {
        var store = new AccountStore(_dir);
        store.CreateFirst(Token(), "azim", "uzun-bir-parola");

        Assert.DoesNotContain("uzun-bir-parola", File.ReadAllText(Path.Combine(_dir, "accounts.json")));
        Assert.NotNull(store.Authenticate("AZIM", "uzun-bir-parola"));
        Assert.Null(store.Authenticate("azim", "yanlis-parola"));
        Assert.Null(store.Authenticate("yok", "uzun-bir-parola"));
    }

    [Fact]
    public void Short_password_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new AccountStore(_dir).CreateFirst(Token(), "azim", "kisa"));

    [Fact]
    public void ChangePassword_requires_current_password()
    {
        var store = new AccountStore(_dir);
        store.CreateFirst(Token(), "azim", "uzun-bir-parola");
        Assert.Throws<UnauthorizedAccessException>(() => store.ChangePassword("azim", "yanlis", "yeni-uzun-parola"));
        store.ChangePassword("azim", "uzun-bir-parola", "yeni-uzun-parola");
        Assert.NotNull(store.Authenticate("azim", "yeni-uzun-parola"));
    }
}
