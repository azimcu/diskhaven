# DiskHaven

**A self-hosted personal cloud drive that keeps your files as plain folders.**

[Türkçe](README.tr.md) · License: [AGPL-3.0](LICENSE) · Status: early development

DiskHaven turns a home server and a few ordinary hard disks into a private
"Google Drive": a Windows Explorer–style web interface, verified uploads that
resume after interruptions, and one-click sync to external USB backup disks.

## Principles

1. **Your files are never locked in.** Everything is stored as normal files in
   normal folders on NTFS / exFAT disks. If DiskHaven breaks or is removed,
   plug the disk into any Windows, macOS or Linux computer and your files are there.
2. **No silent failures.** Every transfer is checked end to end with SHA-256.
   A file is either fully and correctly stored, or the transfer is reported
   and retried — never "137 of 200 photos arrived".
3. **Backups you can hold in your hand.** Folders on the main disk are
   assigned to external USB disks. Plug a disk in (or keep it attached and
   asleep), press *Sync*, and get a verified report.
4. **Boring, proven building blocks.** ASP.NET Core, PostgreSQL, the tus
   resumable-upload protocol, ffmpeg. DiskHaven glues them together.

## Features

A small core, everything else is a plugin you install from the built-in store
(and remove again without losing data). Plugins that are not installed are never
downloaded by the browser and their API answers 404.

**Core**
- Windows 11 Explorer–like web UI: folder tree, drives with fill bars, address bar,
  details / list / icons views with an icon size slider (Ctrl + wheel), context menu,
  keyboard shortcuts, drag and drop, preview pane, Properties (Alt+Enter)
- Hidden items like Windows (stored as an extended attribute, follows renames) and
  "Show hidden items"
- Resumable, chunked uploads verified with SHA-256; video streaming and previews
- Recycle bin, search across all subfolders (case- and accent-insensitive)
- Light and dark theme; follows the device until you pick one
- Admin panel: overview, plugin store, users, activity log

**Plugins**
| Plugin | What it adds |
|---|---|
| ZIP archives | Compress to ZIP, extract all, download folders / selections as one ZIP |
| Share links | Links with an expiry and an optional password, no account needed; a folder link can also be a drop box that only accepts files |
| Disks and backup | External disks, one-way verified sync, schedules, SMART, power control |
| Server gauge | Live CPU, memory, temperature and disk read/write in the status bar |
| Quick access | Favourites and recently opened files |
| Activity log | Who changed what and when, searchable in the admin panel |
| Hidden folder | A separate, encrypted folder tree behind a password, listed under an inconspicuous name; recovery key, decoy password, encrypted copy on backup disks, offline recovery tool ([format](docs/hideaway.md)) |

Planned: Android and Windows apps (Flutter) with background transfers; iPhone / iPad /
Mac through the installable web app.

Photos are intentionally out of scope: DiskHaven is designed to run next to
[Immich](https://immich.app).

## Repository layout

| Path | Contents |
|---|---|
| `src/DiskHaven.Core` | Storage logic (safe path handling, listings, later uploads and sync) |
| `src/DiskHaven.Api` | ASP.NET Core HTTP API, plugins |
| `src/DiskHaven.Recover` | Offline recovery tool for the hidden folder |
| `tests/` | Unit tests |
| `dev/` | Development helpers: dummy data, simulated USB disks |
| `docs/` | Architecture and roadmap |

## Install (Ubuntu Server 24.04)

```bash
git clone <this repository> diskhaven && cd diskhaven
sudo deploy/install.sh        # Docker, disk agent, DiskHaven container; safe to re-run
```

Open `http://<server>:8095` and create the admin account with the setup code from
`/srv/diskhaven/config/setup-token.txt`. `sudo deploy/uninstall.sh` removes DiskHaven
but keeps your data and settings.

## Development

Requirements: .NET 10 SDK, Linux.

```bash
python3 dev/seed-dummy-data.py ~/diskhaven-dev/data   # ~85 MB of sample files
dev/virtual-disks.sh up ~/diskhaven-dev/disks          # three small exFAT "USB disks"
DiskHaven__DataRoot=~/diskhaven-dev/data dotnet run --project src/DiskHaven.Api
dotnet test
```

The API listens on `http://127.0.0.1:5143`. Try
`curl "http://127.0.0.1:5143/api/files?path=Belgeler"`.

See [docs/architecture.md](docs/architecture.md) and [docs/roadmap.md](docs/roadmap.md).

## License

DiskHaven is free software under the GNU Affero General Public License v3.0.
