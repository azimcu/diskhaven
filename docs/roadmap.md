# Roadmap

| Phase | Scope | Status |
|---|---|---|
| 1 | Project skeleton, safe path layer, read-only file API, dummy data, simulated USB disks | ✅ done |
| 2 | Explorer-style web UI; verified resumable uploads (tus); rename / move / delete with recycle bin; authentication; folder sizes | ✅ done |
| 3 | Disk agent (mount, power off / on, standby, SMART); folder-to-disk assignment; one-way verified sync with plan, report, history, daily schedule | ✅ done (virtual disks); real USB disks to be tested |
| 4 | Move to the production server; migrate the real 4 TB disk and backups | |
| 5 | Previews and video streaming; integrity scans; Android and Windows apps; PWA for Apple devices | |
| 6 | Share links, second user, Docker Compose install, CI, first public release | |

## Known gaps / next improvements

- USB power-on after power-off depends on the USB enclosure; to be tested with the real disks.
- Move detection matches files by size and modification time and checks the content (SHA-256) before moving; a file that was both moved and edited is copied again.
