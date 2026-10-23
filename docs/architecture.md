# Architecture

```
 Browser (Explorer-style UI, installable PWA)   Android / Windows app (Flutter)
                         \                          /
                          \   HTTPS over Tailscale /
                           v                      v
                    ┌──────────────────────────────────┐
                    │  HomeVault.Api (ASP.NET Core)     │
                    │   files · uploads (tus) · disks   │
                    │   sync · integrity · previews     │
                    └──────┬──────────────┬────────────┘
                           │              │
                     PostgreSQL       Main disk (NTFS)          USB disks (exFAT)
                  (metadata only:   plain folders ───sync───▶  plain folders
                   checksums, jobs)  + .homevault/            + .homevault-disk.json
```

## Storage model

- **The disk is the source of truth.** Files live as ordinary files; the database
  only caches metadata (size, mtime, SHA-256, sync state). Losing the database
  loses no data — it is rebuilt by scanning.
- **`.homevault/` folder** at the root of the main disk holds HomeVault's own
  working data: temporary upload chunks, the recycle bin. It is hidden from clients.
- **Path safety.** Clients send paths relative to the vault root with `/`.
  `VaultRoot` rejects `..`, `.`, `:`, control characters and the internal
  folder, and refuses to follow symbolic links, so no request can reach a file
  outside the vault.

## Upload integrity (planned)

1. Client computes SHA-256 and starts a tus upload with it in metadata.
2. Chunks (10–50 MB) are written to `.homevault/uploads/<id>`; interrupted
   uploads resume from the last confirmed offset.
3. When complete the server re-hashes the file. Mismatch → rejected, client retries.
4. Match → `fsync`, then atomic rename to the destination. No partial files ever
   appear in the user's folders.

## External disk sync (planned)

- Each external disk carries `.homevault-disk.json` (disk id, name) so it is
  recognised on any USB port.
- An admin assigns folders of the main disk to disks.
- *Sync* computes a plan (new / changed / deleted), shows it, copies with
  verification, and writes a report. Deleted files are moved to the disk's
  `.homevault-trash/` and kept for 30 days instead of being removed.
- Detach = unmount + `udisksctl power-off` (spins the disk down); attach =
  USB re-authorise. Idle disks are put to standby with `hdparm`. Per-port power
  cut needs a hub that supports it (uhubctl) and is optional.

## Single-disk integrity

With one main disk there is no RAID mirror, so HomeVault keeps a SHA-256 for
every file and periodically re-reads and compares. A corrupted file is reported
and can be restored from the external disk copy.
