# HomeVault

**A self-hosted personal cloud drive that keeps your files as plain folders.**

[Türkçe](README.tr.md) · License: [AGPL-3.0](LICENSE) · Status: early development

HomeVault turns a home server and a few ordinary hard disks into a private
"Google Drive": a Windows Explorer–style web interface, verified uploads that
resume after interruptions, and one-click sync to external USB backup disks.

## Principles

1. **Your files are never locked in.** Everything is stored as normal files in
   normal folders on NTFS / exFAT disks. If HomeVault breaks or is removed,
   plug the disk into any Windows, macOS or Linux computer and your files are there.
2. **No silent failures.** Every transfer is checked end to end with SHA-256.
   A file is either fully and correctly stored, or the transfer is reported
   and retried — never "137 of 200 photos arrived".
3. **Backups you can hold in your hand.** Folders on the main disk are
   assigned to external USB disks. Plug a disk in (or keep it attached and
   asleep), press *Sync*, and get a verified report.
4. **Boring, proven building blocks.** ASP.NET Core, PostgreSQL, the tus
   resumable-upload protocol, ffmpeg. HomeVault glues them together.

## Planned features

- Windows Explorer–like web UI: folder tree, address bar, details / icons view,
  context menu, keyboard shortcuts, drag and drop, preview pane
- Resumable, chunked, verified uploads and downloads (hundreds of GB)
- Video streaming and image/document preview in the browser
- Disk manager: health (SMART), usage, attach / detach and spin-down of USB disks
- Sync to external disks with a pre-sync summary, verification and a 30-day
  safety net for deletions
- Monthly integrity scan against stored checksums
- Recycle bin, share links, optional second user
- Android and Windows apps (Flutter) with background transfers; iPhone / iPad /
  Mac through the installable web app

Photos are intentionally out of scope: HomeVault is designed to run next to
[Immich](https://immich.app).

## Repository layout

| Path | Contents |
|---|---|
| `src/HomeVault.Core` | Storage logic (safe path handling, listings, later uploads and sync) |
| `src/HomeVault.Api` | ASP.NET Core HTTP API |
| `tests/` | Unit tests |
| `dev/` | Development helpers: dummy data, simulated USB disks |
| `docs/` | Architecture and roadmap |

## Development

Requirements: .NET 10 SDK, Linux.

```bash
python3 dev/seed-dummy-data.py ~/homevault-dev/data   # ~85 MB of sample files
dev/virtual-disks.sh up ~/homevault-dev/disks          # three small exFAT "USB disks"
HomeVault__DataRoot=~/homevault-dev/data dotnet run --project src/HomeVault.Api
dotnet test
```

The API listens on `http://127.0.0.1:5143`. Try
`curl "http://127.0.0.1:5143/api/files?path=Belgeler"`.

See [docs/architecture.md](docs/architecture.md) and [docs/roadmap.md](docs/roadmap.md).

## License

HomeVault is free software under the GNU Affero General Public License v3.0.
