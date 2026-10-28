using System.Security.Cryptography;
using System.Text.Json;

namespace HomeVault.Core.Accounts;

public sealed record Account(string UserName, string PasswordHash, bool IsAdmin, DateTimeOffset CreatedAt);

/// <summary>
/// User accounts in a small JSON file in the config folder (a home NAS has one or two users).
/// The very first account is created through a setup page that requires a one-time token
/// stored next to it, so nobody else on the network can claim a fresh install.
/// </summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly string _setupTokenFile;
    private readonly Lock _lock = new();

    public AccountStore(string configDir)
    {
        Directory.CreateDirectory(configDir);
        _file = Path.Combine(configDir, "accounts.json");
        _setupTokenFile = Path.Combine(configDir, "setup-token.txt");
        if (!HasAccounts && !File.Exists(_setupTokenFile))
            File.WriteAllText(_setupTokenFile, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + "\n");
    }

    public bool HasAccounts => Load().Count > 0;

    public string SetupTokenPath => _setupTokenFile;

    public Account? Find(string userName) =>
        Load().FirstOrDefault(a => a.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the account if the password is right, otherwise null.</summary>
    public Account? Authenticate(string userName, string password)
    {
        var account = Find(userName);
        // hash anyway so a wrong user name takes as long as a wrong password
        var ok = PasswordHasher.Verify(password, account?.PasswordHash ?? DummyHash);
        return ok && account is not null ? account : null;
    }

    /// <summary>Creates the first (admin) account. Fails if the token is wrong or an account already exists.</summary>
    public Account CreateFirst(string setupToken, string userName, string password)
    {
        lock (_lock)
        {
            if (HasAccounts)
                throw new InvalidOperationException("Setup is already complete.");
            var expected = File.Exists(_setupTokenFile) ? File.ReadAllText(_setupTokenFile).Trim() : "";
            if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(setupToken.Trim()), System.Text.Encoding.UTF8.GetBytes(expected)))
                throw new UnauthorizedAccessException("Setup token is wrong.");
            var account = NewAccount(userName, password, isAdmin: true);
            Save([account]);
            File.Delete(_setupTokenFile);
            return account;
        }
    }

    public IReadOnlyList<Account> List() => Load().OrderBy(a => a.CreatedAt).ToList();

    /// <summary>Adds an account (done by an admin from the users page).</summary>
    public Account Create(string userName, string password, bool isAdmin)
    {
        lock (_lock)
        {
            var accounts = Load();
            if (accounts.Count == 0)
                throw new InvalidOperationException("Use the setup page to create the first account.");
            var account = NewAccount(userName, password, isAdmin);
            if (accounts.Any(a => a.UserName.Equals(account.UserName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"User {account.UserName} already exists.");
            accounts.Add(account);
            Save(accounts);
            return account;
        }
    }

    /// <summary>Removes an account. The last admin cannot be removed, so the NAS can always be managed.</summary>
    public void Delete(string userName)
    {
        lock (_lock)
        {
            var accounts = Load();
            var account = accounts.FirstOrDefault(a => a.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException("User not found.");
            if (account.IsAdmin && accounts.Count(a => a.IsAdmin) == 1)
                throw new InvalidOperationException("The last admin cannot be removed.");
            accounts.Remove(account);
            Save(accounts);
        }
    }

    /// <summary>Sets a new password without the old one (an admin resetting another user's password).</summary>
    public void SetPassword(string userName, string newPassword)
    {
        lock (_lock)
        {
            var accounts = Load();
            var i = accounts.FindIndex(a => a.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
                throw new KeyNotFoundException("User not found.");
            ValidatePassword(newPassword);
            accounts[i] = accounts[i] with { PasswordHash = PasswordHasher.Hash(newPassword) };
            Save(accounts);
        }
    }

    /// <summary>
    /// A short value that changes whenever the password changes. It is stored in the login cookie,
    /// so changing a password (or deleting the account) logs out every other device.
    /// </summary>
    public static string Stamp(Account account) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(account.PasswordHash)))[..16];

    public void ChangePassword(string userName, string currentPassword, string newPassword)
    {
        lock (_lock)
        {
            var accounts = Load();
            var i = accounts.FindIndex(a => a.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase));
            if (i < 0 || !PasswordHasher.Verify(currentPassword, accounts[i].PasswordHash))
                throw new UnauthorizedAccessException("Current password is wrong.");
            ValidatePassword(newPassword);
            accounts[i] = accounts[i] with { PasswordHash = PasswordHasher.Hash(newPassword) };
            Save(accounts);
        }
    }

    public static void ValidatePassword(string password)
    {
        if (password.Length < 10)
            throw new ArgumentException("Password must be at least 10 characters.");
    }

    private static Account NewAccount(string userName, string password, bool isAdmin)
    {
        userName = userName.Trim();
        if (userName.Length is < 2 or > 32 || !userName.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException("User name: 2-32 characters, letters, digits . _ -");
        ValidatePassword(password);
        return new Account(userName, PasswordHasher.Hash(password), isAdmin, DateTimeOffset.UtcNow);
    }

    private List<Account> Load() =>
        File.Exists(_file) ? JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_file)) ?? [] : [];

    private void Save(List<Account> accounts)
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(accounts, Json));
        File.Move(temp, _file, overwrite: true);
    }

    private static readonly string DummyHash = PasswordHasher.Hash(Guid.NewGuid().ToString());
}
