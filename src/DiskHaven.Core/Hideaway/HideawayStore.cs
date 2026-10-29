using System.Security.Cryptography;
using System.Text.Json;

namespace DiskHaven.Core.Hideaway;

/// <summary>One item of a hidden folder. Folders have no blob.</summary>
public sealed record HideawayNode(string Id, string? Parent, string Name, bool Folder, long Size, DateTimeOffset Modified, string? Blob);

/// <summary>The decrypted folder index of one vault (real or decoy; the code treats both the same).</summary>
public sealed class HideawayIndex
{
    public int Version { get; set; } = 1;
    /// <summary>Slot this vault may put its own decoy into. Chosen by whoever created this vault, never its own slot
    /// and never the slot of the vault that created it, so setting a decoy can never overwrite another vault.</summary>
    public int? DecoySlot { get; set; }
    public Dictionary<string, HideawayNode> Nodes { get; set; } = [];
}

/// <summary>
/// The hidden folder of one user: <c>&lt;data&gt;/.diskhaven/hideaway/&lt;user id&gt;/</c> with <c>slots.bin</c> and
/// <c>blobs/</c>. It lives in the internal folder, which the normal file tree can never open. Everything on disk is
/// either random-looking slots or encrypted blobs; names, folders and sizes are only inside the encrypted index.
/// An opened vault (<see cref="Opened"/>) holds the key in memory; the API forgets it when the vault is locked.
/// </summary>
public sealed class HideawayStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _root;

    public HideawayStore(string dataRoot) =>
        _root = Path.Combine(dataRoot, Storage.VaultRoot.InternalFolderName, "hideaway");

    private HideawayStore(string hideawayRoot, bool _) => _root = hideawayRoot;

    /// <summary>For the recovery tool: opens one user folder of a hideaway copied anywhere (e.g. a backup disk).</summary>
    public static Opened? OpenFolder(string userFolder, string secret)
    {
        var store = new HideawayStore(Path.GetDirectoryName(userFolder)!, true);
        var name = Path.GetFileName(userFolder);
        return store.OpenDir(name, secret);
    }

    /// <summary>Folder that holds everything of all users (copied as-is to backup disks).</summary>
    public string RootFolder => _root;

    public static string UserFolderName(string user) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("diskhaven-hideaway:" + user.ToLowerInvariant())))[..24];

    // a vault is addressed by its folder name; the API derives it from the user name, the recovery tool reads it from disk
    private string Dir(string user) => Path.Combine(_root, user.StartsWith('#') ? user[1..] : UserFolderName(user));
    private string SlotsFile(string user) => Path.Combine(Dir(user), "slots.bin");

    private Opened? OpenDir(string folderName, string secret) => Open("#" + folderName, secret);

    public bool IsSetUp(string user) => File.Exists(SlotsFile(user));

    /// <summary>Creates the vault. Returns the recovery key (shown once) and the opened vault.
    /// With a decoy password a second, separate vault is created in another slot.</summary>
    public (string RecoveryKey, Opened Vault) Setup(string user, string password, string? decoyPassword)
    {
        CheckPassword(password);
        if (decoyPassword is not null) { CheckPassword(decoyPassword); if (decoyPassword == password) throw new ArgumentException("Decoy password must differ."); }
        if (IsSetUp(user)) throw new InvalidOperationException("The hidden folder is already set up.");
        Directory.CreateDirectory(Path.Combine(Dir(user), "blobs"));
        var slots = Enumerable.Range(0, HideawayCrypto.SlotCount).Select(_ => HideawayCrypto.RandomSlot()).ToArray();
        var order = Enumerable.Range(0, HideawayCrypto.SlotCount).OrderBy(_ => RandomNumberGenerator.GetInt32(1000)).ToArray();
        var (mine, decoy, spare) = (order[0], order[1], order[2]);
        var master = HideawayCrypto.NewKey();
        var recovery = HideawayCrypto.NewKey();
        slots[mine] = HideawayCrypto.MakeSlot(master, password, recovery);
        var vault = new Opened(this, user, mine, master, new HideawayIndex { DecoySlot = decoy });
        if (decoyPassword is not null)
        {
            var dMaster = HideawayCrypto.NewKey();
            // own random recovery half: the user's recovery key always opens the real vault, never the decoy
            slots[decoy] = HideawayCrypto.MakeSlot(dMaster, decoyPassword, HideawayCrypto.NewKey());
            new Opened(this, user, decoy, dMaster, new HideawayIndex { DecoySlot = spare }).SaveIndex();
        }
        WriteSlots(user, slots);
        vault.SaveIndex();
        return (HideawayCrypto.FormatRecoveryKey(recovery), vault);
    }

    /// <summary>Opens whichever vault the password (or recovery key) belongs to; null if none.
    /// Every slot is always tried, so the time taken does not tell which one matched.</summary>
    public Opened? Open(string user, string secret)
    {
        if (!IsSetUp(user)) return null;
        var slots = ReadSlots(user);
        var recovery = HideawayCrypto.ParseRecoveryKey(secret);
        Opened? found = null;
        for (var i = 0; i < slots.Length; i++)
        {
            var master = HideawayCrypto.OpenWithPassword(slots[i], secret)
                         ?? (recovery is null ? null : HideawayCrypto.OpenWithRecoveryKey(slots[i], recovery));
            if (master is not null && found is null)
                found = new Opened(this, user, i, master, LoadIndex(user, master));
        }
        return found;
    }

    private HideawayIndex LoadIndex(string user, byte[] master)
    {
        var id = HideawayCrypto.IndexId(master);
        var file = BlobPath(user, id);
        if (!File.Exists(file)) return new HideawayIndex();
        using var fs = File.OpenRead(file);
        using var ms = new MemoryStream();
        HideawayCrypto.Decrypt(master, id, fs, ms);
        return JsonSerializer.Deserialize<HideawayIndex>(ms.ToArray(), Json) ?? new HideawayIndex();
    }

    internal string BlobPath(string user, string id) => Path.Combine(Dir(user), "blobs", id[..2], id);

    private byte[][] ReadSlots(string user)
    {
        var bytes = File.ReadAllBytes(SlotsFile(user));
        return Enumerable.Range(0, HideawayCrypto.SlotCount).Select(i => bytes.AsSpan(i * HideawayCrypto.SlotSize, HideawayCrypto.SlotSize).ToArray()).ToArray();
    }

    private void WriteSlots(string user, byte[][] slots)
    {
        var tmp = SlotsFile(user) + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            foreach (var s in slots) fs.Write(s);
            fs.Flush(true);
        }
        File.Move(tmp, SlotsFile(user), overwrite: true);
    }

    internal void WriteSlot(string user, int index, byte[] slot)
    {
        var slots = ReadSlots(user);
        slots[index] = slot;
        WriteSlots(user, slots);
    }

    internal byte[] ReadSlot(string user, int index) => ReadSlots(user)[index];

    public static void CheckPassword(string p)
    {
        if (p.Length < 8) throw new ArgumentException("Password must be at least 8 characters.");
    }

    /// <summary>An unlocked vault. Not thread-safe by itself; the API serialises calls per vault.</summary>
    public sealed class Opened
    {
        private readonly HideawayStore _store;
        private readonly byte[] _master;
        internal Opened(HideawayStore store, string user, int slot, byte[] master, HideawayIndex index)
        { _store = store; User = user; Slot = slot; _master = master; Index = index; }

        public string User { get; }
        internal int Slot { get; }
        public HideawayIndex Index { get; }
        public readonly Lock Gate = new();

        public IReadOnlyList<HideawayNode> Children(string? parent) =>
            Index.Nodes.Values.Where(n => n.Parent == parent).OrderBy(n => !n.Folder).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        public HideawayNode Get(string id) => Index.Nodes.TryGetValue(id, out var n) ? n : throw new KeyNotFoundException("Item not found.");

        /// <summary>"Folder/Sub/Name" of a node, for breadcrumbs.</summary>
        public IReadOnlyList<HideawayNode> PathOf(string? id)
        {
            var list = new List<HideawayNode>();
            for (var cur = id; cur is not null; cur = Get(cur).Parent) list.Insert(0, Get(cur));
            return list;
        }

        private void CheckParent(string? parent)
        {
            if (parent is not null && !Get(parent).Folder) throw new KeyNotFoundException("Folder not found.");
        }

        private string UniqueName(string? parent, string name, string? except = null)
        {
            name = Storage.FileNames.Validate(name);
            var taken = Index.Nodes.Values.Where(n => n.Parent == parent && n.Id != except).Select(n => n.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(name)) return name;
            var ext = Path.GetExtension(name); var stem = ext.Length > 0 ? name[..^ext.Length] : name;
            for (var i = 2; ; i++) if (!taken.Contains($"{stem} ({i}){ext}")) return $"{stem} ({i}){ext}";
        }

        public HideawayNode CreateFolder(string? parent, string name)
        {
            CheckParent(parent);
            var node = new HideawayNode(HideawayCrypto.NewBlobId(), parent, UniqueName(parent, name), true, 0, DateTimeOffset.UtcNow, null);
            Index.Nodes[node.Id] = node;
            SaveIndex();
            return node;
        }

        /// <summary>Encrypts a finished upload into the vault and deletes the plain temp file.</summary>
        public HideawayNode AddFile(string? parent, string name, string plainFile, DateTimeOffset? modified)
        {
            CheckParent(parent);
            var blob = HideawayCrypto.NewBlobId();
            long size;
            using (var src = File.OpenRead(plainFile)) size = WriteBlob(blob, src);
            File.Delete(plainFile);
            var node = new HideawayNode(HideawayCrypto.NewBlobId(), parent, UniqueName(parent, name), false, size, modified ?? DateTimeOffset.UtcNow, blob);
            Index.Nodes[node.Id] = node;
            SaveIndex();
            return node;
        }

        public HideawayNode Rename(string id, string name)
        {
            var n = Get(id);
            var updated = n with { Name = UniqueName(n.Parent, name, n.Id) };
            Index.Nodes[id] = updated;
            SaveIndex();
            return updated;
        }

        public void Move(IEnumerable<string> ids, string? parent)
        {
            CheckParent(parent);
            foreach (var id in ids)
            {
                var n = Get(id);
                for (var p = parent; p is not null; p = Get(p).Parent)
                    if (p == id) throw new Storage.VaultPathException("A folder cannot be moved or copied into itself.");
                Index.Nodes[id] = n with { Parent = parent, Name = UniqueName(parent, n.Name, n.Id) };
            }
            SaveIndex();
        }

        public void Copy(IEnumerable<string> ids, string? parent)
        {
            CheckParent(parent);
            foreach (var id in ids) CopyNode(Get(id), parent, true);
            SaveIndex();
        }

        private void CopyNode(HideawayNode n, string? parent, bool top)
        {
            var name = top && n.Parent == parent ? CopyName(n) : n.Name;
            string? blob = null;
            if (!n.Folder)
            {
                blob = HideawayCrypto.NewBlobId();
                // re-encrypted under a fresh blob id: copies never share ciphertext
                var tmp = Path.Combine(Path.GetTempPath(), "dh-" + HideawayCrypto.NewBlobId());
                try
                {
                    using (var o = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose))
                    {
                        ReadBlob(n.Blob!, o);
                        o.Position = 0;
                        WriteBlob(blob, o);
                    }
                }
                finally { if (File.Exists(tmp)) File.Delete(tmp); }
            }
            var copy = new HideawayNode(HideawayCrypto.NewBlobId(), parent, UniqueName(parent, name), n.Folder, n.Size, n.Modified, blob);
            Index.Nodes[copy.Id] = copy;
            if (n.Folder)
                foreach (var c in Children(n.Id).ToList()) CopyNode(c, copy.Id, false);
        }

        private static string CopyName(HideawayNode n) => n.Folder ? $"{n.Name} - Kopya"
            : $"{Path.GetFileNameWithoutExtension(n.Name)} - Kopya{Path.GetExtension(n.Name)}";

        /// <summary>Deletes for good (the vault has no recycle bin: deleted secrets should not linger).</summary>
        public void Delete(IEnumerable<string> ids)
        {
            foreach (var id in ids.ToList())
            {
                if (!Index.Nodes.ContainsKey(id)) continue;
                foreach (var c in Children(id).ToList()) Delete([c.Id]);
                var n = Index.Nodes[id];
                Index.Nodes.Remove(id);
                if (n.Blob is not null) File.Delete(_store.BlobPath(User, n.Blob));
            }
            SaveIndex();
        }

        public void ReadFile(string id, Stream output)
        {
            var n = Get(id);
            if (n.Folder) throw new KeyNotFoundException("File not found.");
            ReadBlob(n.Blob!, output);
        }

        public long FolderSize(string id) => Children(id).Sum(c => c.Folder ? FolderSize(c.Id) : c.Size);

        public void ChangePassword(string currentPassword, string newPassword)
        {
            CheckPassword(newPassword);
            if (HideawayCrypto.OpenWithPassword(_store.ReadSlot(User, Slot), currentPassword) is null)
                throw new UnauthorizedAccessException("Current password is wrong.");
            _store.WriteSlot(User, Slot, HideawayCrypto.Rekey(_store.ReadSlot(User, Slot), _master, newPassword));
        }

        /// <summary>Sets (or replaces) this vault's decoy: a separate empty vault opened by another password.
        /// The slot used is fixed when this vault was created, so this never touches any other vault.</summary>
        public void SetDecoy(string decoyPassword, string currentPassword)
        {
            CheckPassword(decoyPassword);
            if (decoyPassword == currentPassword) throw new ArgumentException("Decoy password must differ.");
            if (HideawayCrypto.OpenWithPassword(_store.ReadSlot(User, Slot), currentPassword) is null)
                throw new UnauthorizedAccessException("Current password is wrong.");
            var slot = Index.DecoySlot ?? throw new InvalidOperationException("No free slot.");
            var spare = Enumerable.Range(0, HideawayCrypto.SlotCount).First(i => i != slot && i != Slot);
            var dMaster = HideawayCrypto.NewKey();
            // the decoy gets its own recovery key half: the user's recovery key keeps opening only this vault
            _store.WriteSlot(User, slot, HideawayCrypto.MakeSlot(dMaster, decoyPassword, HideawayCrypto.NewKey()));
            new Opened(_store, User, slot, dMaster, new HideawayIndex { DecoySlot = spare }).SaveIndex();
        }

        internal void SaveIndex()
        {
            var id = HideawayCrypto.IndexId(_master);
            using var ms = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(Index, Json));
            WriteBlob(id, ms);
        }

        private long WriteBlob(string id, Stream plain)
        {
            var path = _store.BlobPath(User, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            long n;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                n = HideawayCrypto.Encrypt(_master, id, plain, fs);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
            return n;
        }

        private void ReadBlob(string id, Stream output)
        {
            using var fs = new FileStream(_store.BlobPath(User, id), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            HideawayCrypto.Decrypt(_master, id, fs, output);
        }

        /// <summary>Writes the whole vault, decrypted, as normal folders under <paramref name="target"/> (recovery/export).</summary>
        public int ExportTo(string target, string? parent = null)
        {
            Directory.CreateDirectory(target);
            var count = 0;
            foreach (var n in Children(parent))
            {
                var dest = Path.Combine(target, n.Name);
                if (n.Folder) { count += ExportTo(dest, n.Id); continue; }
                using (var o = new FileStream(dest, FileMode.Create, FileAccess.Write)) ReadBlob(n.Blob!, o);
                File.SetLastWriteTimeUtc(dest, n.Modified.UtcDateTime);
                count++;
            }
            return count;
        }
    }
}
