using System.Security.Cryptography;
using DiskHaven.Core.Hideaway;

namespace DiskHaven.Core.Tests;

public sealed class HideawayTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diskhaven-hide-").FullName;
    private readonly HideawayStore _store;
    public HideawayTests() => _store = new HideawayStore(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Plain(string text) { var f = Path.Combine(_dir, Guid.NewGuid() + ".up"); File.WriteAllText(f, text); return f; }
    private static string Read(HideawayStore.Opened v, string id) { using var ms = new MemoryStream(); v.ReadFile(id, ms); return System.Text.Encoding.UTF8.GetString(ms.ToArray()); }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(HideawayCrypto.ChunkSize)]       // exactly one chunk: no empty trailing chunk
    [InlineData(HideawayCrypto.ChunkSize + 1)]
    [InlineData(3 * HideawayCrypto.ChunkSize - 7)]
    public void Blobs_round_trip(int size)
    {
        var key = HideawayCrypto.NewKey();
        var data = RandomNumberGenerator.GetBytes(size);
        using var enc = new MemoryStream();
        HideawayCrypto.Encrypt(key, "b1", new MemoryStream(data), enc);
        using var dec = new MemoryStream();
        HideawayCrypto.Decrypt(key, "b1", new MemoryStream(enc.ToArray()), dec);
        Assert.Equal(data, dec.ToArray());
    }

    [Fact]
    public void Tampering_truncation_and_wrong_id_are_detected()
    {
        var key = HideawayCrypto.NewKey();
        var data = RandomNumberGenerator.GetBytes(2 * HideawayCrypto.ChunkSize + 100);
        using var enc = new MemoryStream();
        HideawayCrypto.Encrypt(key, "b1", new MemoryStream(data), enc);
        var bytes = enc.ToArray();
        var flipped = (byte[])bytes.Clone(); flipped[500] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => HideawayCrypto.Decrypt(key, "b1", new MemoryStream(flipped), Stream.Null));
        // drop the last chunk: the previous one is not marked "last"
        var cut = bytes[..(16 + 2 * (HideawayCrypto.ChunkSize + 16))];
        Assert.ThrowsAny<CryptographicException>(() => HideawayCrypto.Decrypt(key, "b1", new MemoryStream(cut), Stream.Null));
        // a blob renamed to another id does not open
        Assert.ThrowsAny<CryptographicException>(() => HideawayCrypto.Decrypt(key, "b2", new MemoryStream(bytes), Stream.Null));
    }

    [Fact]
    public void Password_and_recovery_key_open_the_real_vault_decoy_opens_its_own()
    {
        var (recovery, real) = _store.Setup("azim", "gercek-parola", "yem-parolasi");
        var f = real.CreateFolder(null, "Gizli belgeler");
        var file = real.AddFile(f.Id, "not.txt", Plain("çok gizli"), null);

        var decoy = _store.Open("azim", "yem-parolasi")!;
        Assert.Empty(decoy.Children(null));                       // the decoy shows nothing of the real one
        decoy.AddFile(null, "tatil.txt", Plain("sıradan"), null);

        var again = _store.Open("azim", "gercek-parola")!;
        Assert.Single(again.Children(null));
        Assert.Equal("çok gizli", Read(again, file.Id));
        var byKey = _store.Open("azim", recovery.ToLowerInvariant().Replace("-", " "))!;   // typed loosely
        Assert.Equal("Gizli belgeler", byKey.Children(null).Single().Name);
        Assert.Null(_store.Open("azim", "yanlis-parola"));
        Assert.Equal("tatil.txt", _store.Open("azim", "yem-parolasi")!.Children(null).Single().Name);
    }

    [Fact]
    public void Nothing_readable_is_stored_and_slot_file_size_never_changes()
    {
        var (_, v) = _store.Setup("azim", "gercek-parola", null);
        var slots = Directory.GetFiles(_dir, "slots.bin", SearchOption.AllDirectories).Single();
        var before = new FileInfo(slots).Length;
        v.AddFile(null, "banka-sifreleri.txt", Plain("hesap no 12345"), null);
        v.SetDecoy("yem-parolasi", "gercek-parola");
        Assert.Equal(before, new FileInfo(slots).Length);
        Assert.Equal(HideawayCrypto.SlotCount * HideawayCrypto.SlotSize, before);
        foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
        {
            var text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(f));
            Assert.DoesNotContain("banka", text);
            Assert.DoesNotContain("12345", text);
        }
    }

    [Fact]
    public void A_decoy_setting_its_own_decoy_never_touches_the_real_vault()
    {
        var (_, real) = _store.Setup("azim", "gercek-parola", "yem-parolasi");
        var secret = real.AddFile(null, "a.txt", Plain("gerçek"), null);
        var decoy = _store.Open("azim", "yem-parolasi")!;
        decoy.SetDecoy("ikinci-yem", "yem-parolasi");             // works, like it would in the real vault
        Assert.NotNull(_store.Open("azim", "ikinci-yem"));
        Assert.Equal("gerçek", Read(_store.Open("azim", "gercek-parola")!, secret.Id));
    }

    [Fact]
    public void Operations_keep_the_tree_consistent()
    {
        var (_, v) = _store.Setup("azim", "gercek-parola", null);
        var a = v.CreateFolder(null, "A");
        var b = v.CreateFolder(a.Id, "B");
        var f = v.AddFile(b.Id, "x.txt", Plain("x"), null);
        Assert.Throws<DiskHaven.Core.Storage.VaultPathException>(() => v.Move([a.Id], b.Id));
        v.Copy([a.Id], null);
        var copy = v.Children(null).Single(n => n.Name == "A - Kopya");
        var copied = v.Children(v.Children(copy.Id).Single().Id).Single();
        Assert.NotEqual(f.Blob, copied.Blob);
        Assert.Equal("x", Read(v, copied.Id));
        v.Rename(f.Id, "y.txt");
        v.Delete([a.Id]);
        Assert.Equal(["A - Kopya"], v.Children(null).Select(n => n.Name));
        var blobs = Directory.GetFiles(_dir, "*", SearchOption.AllDirectories).Count(p => p.Contains("blobs"));
        Assert.Equal(2, blobs);   // index + the copy's file: deleted files leave nothing behind

        var outDir = Path.Combine(_dir, "disari");
        Assert.Equal(1, _store.Open("azim", "gercek-parola")!.ExportTo(outDir));
        Assert.Equal("x", File.ReadAllText(Path.Combine(outDir, "A - Kopya", "B", "x.txt")));
    }
}

public sealed class HideawayRecoveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("diskhaven-rec-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_copy_on_a_backup_disk_opens_with_the_recovery_key()
    {
        var store = new HideawayStore(_dir);
        var (key, v) = store.Setup("azim", "gercek-parola", "yem-parolasi");
        var up = Path.Combine(_dir, "u"); File.WriteAllText(up, "yedekte de açılır");
        v.AddFile(v.CreateFolder(null, "Klasör").Id, "dosya.txt", up, null);

        // what the sync copies to the backup disk
        var disk = Path.Combine(_dir, "disk", ".diskhaven-data", "hideaway");
        foreach (var f in Directory.GetFiles(store.RootFolder, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(disk, Path.GetRelativePath(store.RootFolder, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        var userDir = Directory.GetDirectories(disk).Single();
        Assert.Null(HideawayStore.OpenFolder(userDir, "yanlis"));
        var opened = HideawayStore.OpenFolder(userDir, key)!;
        var outDir = Path.Combine(_dir, "cikis");
        Assert.Equal(1, opened.ExportTo(outDir));
        Assert.Equal("yedekte de açılır", File.ReadAllText(Path.Combine(outDir, "Klasör", "dosya.txt")));
    }
}
