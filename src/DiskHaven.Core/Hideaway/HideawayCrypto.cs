using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DiskHaven.Core.Hideaway;

/// <summary>
/// Cryptography of the hidden folder. Format (also read by the offline recovery tool, see docs/hideaway.md):
/// <list type="bullet">
/// <item><b>slots.bin</b>: <see cref="SlotCount"/> fixed-size slots (8 × 136 bytes). A used slot holds the vault's 256-bit master key
/// wrapped twice with AES-256-GCM: once under PBKDF2-SHA256(password, 600 000 rounds) and once under the recovery key
/// (HKDF). An unused slot is random bytes of the same size, so nobody can tell how many slots are in use.</item>
/// <item><b>blobs</b>: every file and the folder index are separate encrypted objects with random names. A blob is
/// "DHK1" | chunk size (4) | nonce prefix (8) | chunks, each chunk AES-256-GCM sealed (ciphertext + 16-byte tag)
/// under a per-blob key HKDF(master, blob id). The nonce is prefix + chunk counter; the chunk number and a
/// "last chunk" flag are authenticated, so chunks cannot be reordered, dropped or the blob truncated.</item>
/// </list>
/// </summary>
public static class HideawayCrypto
{
    public const int SlotCount = 8;
    public const int Pbkdf2Rounds = 600_000;
    private const int KeySize = 32, SaltSize = 16, NonceSize = 12, TagSize = 16;
    private const int WrapSize = NonceSize + KeySize + TagSize;                 // 60
    public const int SlotSize = SaltSize + WrapSize + WrapSize;                  // 136
    public const int ChunkSize = 1 << 20;
    private static readonly byte[] Magic = "DHK1"u8.ToArray();
    private const int HeaderSize = 4 + 4 + 8;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);
    public static byte[] RandomSlot() => RandomNumberGenerator.GetBytes(SlotSize);

    /// <summary>Recovery key shown to the user: 32 random bytes as 52 Base32 characters in groups of four.</summary>
    public static string FormatRecoveryKey(byte[] key) =>
        string.Join('-', Enumerable.Range(0, 13).Select(i => Base32(key).Substring(i * 4, 4)));

    public static byte[]? ParseRecoveryKey(string text)
    {
        var clean = new string(text.ToUpperInvariant().Where(c => c is >= 'A' and <= 'Z' or >= '2' and <= '7').ToArray());
        if (clean.Length != 52) return null;
        try { return FromBase32(clean)[..KeySize]; } catch (FormatException) { return null; }
    }

    /// <summary>A slot that opens <paramref name="master"/> with the password or with the recovery key.</summary>
    public static byte[] MakeSlot(byte[] master, string password, byte[] recoveryKey)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var slot = new byte[SlotSize];
        salt.CopyTo(slot, 0);
        Wrap(PasswordKey(password, salt), master, slot.AsSpan(SaltSize, WrapSize));
        Wrap(RecoveryWrapKey(recoveryKey, salt), master, slot.AsSpan(SaltSize + WrapSize, WrapSize));
        return slot;
    }

    /// <summary>Same slot with a new password; the recovery-key half is kept.</summary>
    public static byte[] Rekey(byte[] slot, byte[] master, string newPassword)
    {
        var copy = (byte[])slot.Clone();
        var salt = copy.AsSpan(0, SaltSize).ToArray();
        Wrap(PasswordKey(newPassword, salt), master, copy.AsSpan(SaltSize, WrapSize));
        return copy;
    }

    public static byte[]? OpenWithPassword(ReadOnlySpan<byte> slot, string password) =>
        Unwrap(PasswordKey(password, slot[..SaltSize].ToArray()), slot.Slice(SaltSize, WrapSize));

    public static byte[]? OpenWithRecoveryKey(ReadOnlySpan<byte> slot, byte[] recoveryKey) =>
        Unwrap(RecoveryWrapKey(recoveryKey, slot[..SaltSize].ToArray()), slot.Slice(SaltSize + WrapSize, WrapSize));

    /// <summary>Blob id of a vault's folder index: derived from its key, so it never has to be stored in the clear.</summary>
    public static string IndexId(byte[] master) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(master, "diskhaven-hideaway-index"u8))[..32];

    public static string NewBlobId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static byte[] PasswordKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Pbkdf2Rounds, HashAlgorithmName.SHA256, KeySize);

    private static byte[] RecoveryWrapKey(byte[] recoveryKey, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, recoveryKey, KeySize, salt, "diskhaven-hideaway-recovery"u8.ToArray());

    private static void Wrap(byte[] kek, byte[] master, Span<byte> dest)
    {
        var nonce = dest[..NonceSize];
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(kek, TagSize);
        gcm.Encrypt(nonce, master, dest.Slice(NonceSize, KeySize), dest.Slice(NonceSize + KeySize, TagSize));
    }

    private static byte[]? Unwrap(byte[] kek, ReadOnlySpan<byte> wrapped)
    {
        var master = new byte[KeySize];
        using var gcm = new AesGcm(kek, TagSize);
        try { gcm.Decrypt(wrapped[..NonceSize], wrapped.Slice(NonceSize, KeySize), wrapped.Slice(NonceSize + KeySize, TagSize), master); }
        catch (AuthenticationTagMismatchException) { return null; }
        return master;
    }

    // ---- blobs -----------------------------------------------------------------------------

    private static byte[] BlobKey(byte[] master, string blobId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, master, KeySize, Encoding.ASCII.GetBytes(blobId), "diskhaven-hideaway-blob"u8.ToArray());

    /// <summary>Encrypts <paramref name="input"/> into <paramref name="output"/>. Returns the plaintext length.</summary>
    public static long Encrypt(byte[] master, string blobId, Stream input, Stream output, CancellationToken ct = default)
    {
        using var gcm = new AesGcm(BlobKey(master, blobId), TagSize);
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), ChunkSize);
        RandomNumberGenerator.Fill(header.AsSpan(8, 8));
        output.Write(header);

        var plain = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var nonce = new byte[NonceSize];
        header.AsSpan(8, 8).CopyTo(nonce);
        long total = 0;
        var n = ReadFull(input, plain);
        for (uint i = 0; ; i++)
        {
            ct.ThrowIfCancellationRequested();
            // look one chunk ahead so the last chunk can be marked
            var m = n == ChunkSize ? ReadFull(input, next) : 0;
            var last = m == 0;
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), i);
            gcm.Encrypt(nonce, plain.AsSpan(0, n), cipher.AsSpan(0, n), tag, Aad(blobId, i, last));
            output.Write(cipher, 0, n);
            output.Write(tag);
            total += n;
            if (last) return total;
            (plain, next) = (next, plain);
            n = m;
        }
    }

    /// <summary>Decrypts a blob to <paramref name="output"/>. Throws <see cref="CryptographicException"/> if anything was changed.</summary>
    public static long Decrypt(byte[] master, string blobId, Stream input, Stream output, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        if (ReadFull(input, header) != HeaderSize || !header.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("Not an encrypted DiskHaven object.");
        var chunk = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        if (chunk is <= 0 or > 16 << 20)
            throw new CryptographicException("Damaged object header.");
        using var gcm = new AesGcm(BlobKey(master, blobId), TagSize);
        var nonce = new byte[NonceSize];
        header.AsSpan(8, 8).CopyTo(nonce);
        var cur = new byte[chunk + TagSize];
        var nxt = new byte[chunk + TagSize];
        var plain = new byte[chunk];
        long total = 0;
        var n = ReadFull(input, cur);
        for (uint i = 0; ; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (n < TagSize)
                throw new CryptographicException("Object is truncated.");
            // look one chunk ahead: the chunk with nothing after it must carry the "last" flag
            var m = n == cur.Length ? ReadFull(input, nxt) : 0;
            var last = m == 0;
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), i);
            var len = n - TagSize;
            gcm.Decrypt(nonce, cur.AsSpan(0, len), cur.AsSpan(len, TagSize), plain.AsSpan(0, len), Aad(blobId, i, last));
            output.Write(plain, 0, len);
            total += len;
            if (last) return total;
            (cur, nxt) = (nxt, cur);
            n = m;
        }
    }

    private static byte[] Aad(string blobId, uint index, bool last)
    {
        var id = Encoding.ASCII.GetBytes(blobId);
        var aad = new byte[id.Length + 5];
        id.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(id.Length), index);
        aad[^1] = last ? (byte)1 : (byte)0;
        return aad;
    }

    private static int ReadFull(Stream s, Span<byte> buf)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = s.Read(buf[total..]);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static int ReadFull(Stream s, byte[] buf) => ReadFull(s, buf.AsSpan());

    private const string B32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(B32[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(B32[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] FromBase32(string s)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s)
        {
            var v = B32.IndexOf(c);
            if (v < 0) throw new FormatException();
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        return bytes.ToArray();
    }
}
