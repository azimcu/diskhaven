# Users and permissions

DiskHaven has two roles and one kind of permission. The design follows what home NAS systems and Windows file
servers already do, so it behaves the way people expect:

| Idea | Where it comes from | In DiskHaven |
|---|---|---|
| Admins can do everything | every NAS (Synology DSM, QNAP QTS, TrueNAS, Unraid) | role **admin**: every drive, every folder, the admin panel, disks, users |
| Normal accounts reach only what was given to them | Synology/QNAP shared-folder permissions, Nextcloud shares | role **user**: sees nothing until something is shared |
| Permission on a folder holds for everything inside | NTFS / POSIX ACL inheritance | one permission per item, inherited downwards |
| No access → the folder is not listed at all | Windows *Access-based Enumeration*, Synology *Hide sub-folders and files from users without permissions* | unshared items are filtered from listings and searches, and answer **404**, never "access denied" |
| A personal folder per person | Synology `homes`, Windows `C:\Users\<name>` | **Owner** permission on a folder; "user folders" sets them up on a disk in one step |
| A folder everyone can use | Windows `Users\Public`, Unraid public share | permission for **everyone** (`*`) with Write |
| "Shared with me" | Nextcloud, Google Drive | sidebar list of what others gave you |

## Levels

| Level | Can |
|---|---|
| None | nothing; the item does not exist for this person |
| Read (*Görebilir*) | list, open, preview, download, copy elsewhere |
| Write (*Değiştirebilir*) | also create, upload, rename, move and delete **inside** the folder |
| Owner (*Sahibi*) | also share what is inside with other people (Read / Write); given by an admin |

The highest permission that applies wins; there are no deny rules. Changing an item itself (rename, move away,
delete, hide) needs Write on the folder that holds it, as on any Unix or Windows file system. So a folder shared with
Write can be filled by the other person, but the shared folder itself stays where it is, and a disk's top level is
only changed by admins.

A folder that only *leads* to something shared (for example `Belgeler` when `Belgeler/Okul` is shared) is listed with
just that path inside it; its other contents are invisible.

## Typical set-ups

- **Give a friend a whole disk:** Admin panel → Users → *Bir diskin tamamını ver*. They become the disk's owner: they
  see only that disk (and what else is shared with them), can do anything inside it and share from it.
- **One disk, several people:** Admin panel → Users → *Kullanıcı klasörleri kur*: a folder per chosen person at the
  top of the disk (theirs alone) and an optional common folder (`Ortak`) everyone can read and change.
- **Share your own files:** right-click a file or folder (on a phone: ⋮) → *Kişilerle paylaş* and tick who may see and
  who may change it. Folders carry a small people mark while they are shared.

## Details

- Permissions are stored in `config/permissions.json` (drive id, path, person, level). They follow a renamed or moved
  folder and are removed with a deleted one, so a new folder that later gets the same name is not shared by accident.
- Removing an account removes its permissions. Changing someone's role signs them out everywhere.
- The recycle bin of a drive shows a person only what was deleted from places they may change.
- Public share links can only be made for items the person may change, and each person sees only their own links.
- The gallery shows the main storage as a whole (the admin's photo library), so it is for admins; users see their
  photos and videos in the explorer, which has the same full-screen viewer.
- Planned: groups (a permission for "family" instead of each person) and per-user storage quotas.
