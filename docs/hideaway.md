# Hidden folder (plugin `vault`)

A separate folder tree, encrypted on the server, opened with a password. It does not
appear anywhere in the normal file tree and cannot be reached through it: it lives in the
internal `.diskhaven` folder, which every path check rejects.

## What an observer sees

`<data>/.diskhaven/hideaway/<user id>/` contains

- `slots.bin`: exactly 8 × 136 random-looking bytes, always the same size
- `blobs/xx/<32 hex>`: encrypted objects with random names (files and folder indexes)

No file names, folder names, sizes per file or counts per vault are stored in the clear.
Unused slots are random bytes, so nobody can tell whether a decoy exists.

## Keys

- A **vault** has a random 256-bit master key.
- Its **slot** stores the master key twice, AES-256-GCM wrapped:
  - under PBKDF2-SHA256(password, 16-byte salt, 600 000 rounds)
  - under HKDF-SHA256(recovery key, salt) — the recovery key is 32 random bytes shown once
    as 52 Base32 characters (`XXXX-XXXX-…`)
- Unlocking tries every slot (same work whatever matches) and opens the first that fits.

## Decoy

A decoy password opens a second, independent vault in another slot. Every vault's index
holds an ordered list of all the other slots (`Chain`): its decoy goes into `Chain[0]`, and
that decoy gets `Chain[1..] + [creator's slot]`. Real vaults and decoys therefore look the
same (same list length, a random-looking order), and "set decoy password" works the same in
both. A vault's ancestors sit at the end of its list, so decoys would have to be nested
7 deep before one could overwrite the real vault. The recovery key opens only the real vault.

## Blobs

```
"DHK1" | chunk size (uint32 BE) | nonce prefix (8 random bytes) | chunk* 
chunk = AES-256-GCM(ciphertext ≤ chunk size bytes) || tag (16)
key   = HKDF-SHA256(master, salt = blob id, info = "diskhaven-hideaway-blob")
nonce = prefix || chunk index (uint32 BE)
AAD   = blob id || chunk index || last-chunk flag (1 byte)
```

Changed, reordered, dropped or appended chunks, a truncated blob or a blob renamed to
another id all fail to decrypt. The folder index of a vault is a blob whose id is
HMAC-SHA256(master, "diskhaven-hideaway-index")[0..32], holding JSON with every node
(id, parent, name, size, modified time, blob id).

## Backups and recovery

When the plugin is installed, every sync to a backup disk also copies the hideaway folder,
as it is (encrypted), to `<disk>/.diskhaven-data/hideaway`.

`diskhaven-recover` (in the image under `/app/recover`, downloadable from the hidden
folder's settings) opens such a copy on any PC with the .NET runtime:

```
dotnet diskhaven-recover.dll "E:\.diskhaven-data\hideaway" "C:\Recovered"
```

It asks for the password or the recovery key and writes the files as normal folders.

## Limits

- An unlocked vault's key lives in server memory until it is locked (button, sign-out,
  10 minutes idle, restart).
- Uploads pass through the server's temporary upload folder in plain form until they are
  encrypted (the file name is not stored there); the temp file is then deleted, not wiped.
- The total size of all blobs is visible; which vault they belong to is not.
