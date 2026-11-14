#!/usr/bin/env python3
"""diskhaven-diskd: the only part of DiskHaven that runs as root.

It does a short, fixed list of disk operations for the DiskHaven container: list disks,
mount / unmount, spin down, power off / on (USB), read SMART health. The container talks to
it over a Unix socket with one JSON request per connection. Disks are identified by their
filesystem UUID only; the agent never accepts device paths from the client and never formats.

Every USB, SATA and NVMe disk is listed, but a disk is only ever touched when it is free:
  * a disk holding any mount outside the agent's mount root (/, /boot, /mnt/...) is "system",
  * a disk with a partition named in /etc/fstab (by UUID, LABEL, PARTUUID or device) is "system",
    even when that mount is missing at the moment (nofail disks that did not come up),
  * a disk whose serial or a filesystem UUID is in "protected" is "system".
System disks are shown read-only: no mount, unmount, standby or power operation is accepted.

Read-only is the default and is enforced by the kernel, not by DiskHaven: the partition is
set read-only with `blockdev --setro` and mounted with `ro` plus the option that stops journal
replay (ext4 noload, xfs norecovery, btrfs nologreplay), so not a single block is written.

Config file (default /etc/diskhaven/diskd.json):
  {"mountRoot": "/srv/diskhaven/disks", "socket": "/run/diskhaven/diskd.sock",
   "uid": 1000, "gid": 1000, "usb": true, "sata": true, "virtualDir": null, "protected": []}
"usb"/"sata" allow managing free USB / SATA disks (NVMe is always system-only). "virtualDir"
allows loop devices whose image file is in that folder (development and tests).
Python standard library only.
"""
import json, os, re, socketserver, subprocess, sys, threading, time

CONFIG_PATH = sys.argv[1] if len(sys.argv) > 1 else "/etc/diskhaven/diskd.json"
CFG = {"mountRoot": "/srv/diskhaven/disks", "socket": "/run/diskhaven/diskd.sock", "uid": 1000, "gid": 1000,
       "usb": True, "sata": False, "virtualDir": None, "protected": [],
       "stateFile": "/var/lib/diskhaven/diskd-known.json", "fstab": "/etc/fstab"}
if os.path.exists(CONFIG_PATH):
    with open(CONFIG_PATH) as f:
        CFG.update(json.load(f))
LOCK = threading.Lock()
UUID_RE = re.compile(r"^[A-Za-z0-9-]{4,64}$")
FS_TYPES = {"exfat", "ntfs", "vfat", "ext4", "btrfs", "xfs"}
TRANSPORTS = {"usb", "sata", "nvme"}
# options that make a read-only mount truly write-free (no journal / log replay)
RO_EXTRA = {"ext4": "noload", "xfs": "norecovery", "btrfs": "nologreplay"}


class Refused(Exception):
    pass


def run(*cmd, check=True, timeout=60):
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    if check and p.returncode != 0:
        raise Refused(f"{os.path.basename(cmd[0])} failed: {(p.stderr or p.stdout).strip()[:300]}")
    return p.stdout


def load_known():
    try:
        with open(CFG["stateFile"]) as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_known(known):
    os.makedirs(os.path.dirname(CFG["stateFile"]), exist_ok=True)
    tmp = CFG["stateFile"] + ".tmp"
    with open(tmp, "w") as f:
        json.dump(known, f, indent=2)
    os.replace(tmp, CFG["stateFile"])


def loop_backing():
    out = json.loads(run("losetup", "-J", "-l", check=False) or '{"loopdevices": []}')
    return {d["name"]: d.get("back-file") or "" for d in out.get("loopdevices", [])}


def block_devices():
    out = json.loads(run("lsblk", "-J", "-b", "-o",
                         "NAME,PATH,TYPE,SIZE,MODEL,SERIAL,TRAN,FSTYPE,LABEL,UUID,PARTUUID,MOUNTPOINTS,PKNAME"))
    return out["blockdevices"]


def fstab_sources():
    """Everything /etc/fstab names as a source: ('uuid', x), ('label', x), ('partuuid', x), ('path', x)."""
    found = set()
    try:
        with open(CFG["fstab"]) as f:
            lines = f.read().splitlines()
    except OSError:
        return found
    for line in lines:
        line = line.split("#", 1)[0].strip()
        if not line:
            continue
        # fstab writes a space as \040 (octal); /dev/disk/by-label/ links write it as \x20
        src = re.sub(r"\\([0-7]{3})", lambda m: chr(int(m.group(1), 8)), line.split()[0])
        for prefix, key in (("UUID=", "uuid"), ("LABEL=", "label"), ("PARTUUID=", "partuuid")):
            if src.upper().startswith(prefix):
                found.add((key, src[len(prefix):].strip('"').lower()))
                break
        else:
            m = re.match(r"^/dev/disk/by-(uuid|label|partuuid)/(.+)$", src)
            if m:
                found.add((m.group(1), re.sub(r"\\x([0-9a-fA-F]{2})", lambda x: chr(int(x.group(1), 16)), m.group(2)).lower()))
            elif src.startswith("/dev/"):
                found.add(("path", os.path.realpath(src)))
    return found


def flatten(devs, parent=None):
    for d in devs:
        d["_parent"] = parent
        yield d
        yield from flatten(d.get("children") or [], d)


def system_reason(disk, fstab, protected):
    """Why a disk must never be touched, or None when it is free for DiskHaven."""
    if (disk.get("serial") or "").strip() and (disk.get("serial") or "").strip() in protected:
        return "protected"
    for d in flatten([disk]):
        for m in d.get("mountpoints") or []:
            # anything mounted elsewhere (/, /mnt/..., [SWAP]) means the disk is in use by the system
            if m and not m.startswith(CFG["mountRoot"] + "/"):
                return "mounted"
        if d.get("type") in ("lvm", "crypt", "raid0", "raid1", "raid5", "raid6", "raid10", "md"):
            return "mounted"
        for key in ("uuid", "label", "partuuid"):
            v = (d.get(key) or "").lower()
            if v and (key, v) in fstab:
                return "fstab"
        if d.get("path") and ("path", os.path.realpath(d["path"])) in fstab:
            return "fstab"
        if d.get("uuid") and d["uuid"] in protected:
            return "protected"
        if (d.get("fstype") or "") in ("LVM2_member", "linux_raid_member", "crypto_LUKS", "swap", "zfs_member"):
            return "mounted"
    return None


def mount_modes():
    """Mount point -> True when mounted read-only."""
    modes = {}
    try:
        with open("/proc/self/mounts") as f:
            lines = f.readlines()
        for line in lines:
            parts = line.split()
            if len(parts) >= 4:
                target = parts[1].replace("\\040", " ")
                modes[target] = "ro" in parts[3].split(",")
    except OSError:
        pass
    return modes


def scan():
    """All disks the agent shows, keyed by filesystem UUID. Each entry says whether it is 'system'."""
    result = {}
    backing = loop_backing() if CFG.get("virtualDir") else {}
    fstab = fstab_sources()
    protected = set(CFG.get("protected") or [])
    for disk in block_devices():
        if disk["type"] == "loop":
            if not CFG.get("virtualDir"):
                continue
            back = backing.get(disk["path"], "")
            if not back.startswith(os.path.realpath(CFG["virtualDir"]) + "/"):
                continue
            kind = "virtual"
        elif disk["type"] == "disk" and disk.get("tran") in TRANSPORTS:
            kind = disk["tran"]
        else:
            continue
        reason = system_reason(disk, fstab, protected)
        if reason is None and (kind == "nvme" or (kind == "usb" and not CFG.get("usb")) or (kind == "sata" and not CFG.get("sata"))):
            reason = "not-allowed"
        for part in flatten([disk]):
            if not part.get("uuid"):
                continue
            # system disks list every filesystem (to show them), free disks only the ones we can mount
            if reason is None and part.get("fstype") not in FS_TYPES:
                continue
            if part.get("type") not in ("disk", "part", "loop"):
                continue
            mounts = [m for m in part.get("mountpoints") or [] if m]
            result[part["uuid"]] = {
                "id": part["uuid"], "kind": kind, "device": part["path"], "disk": disk["path"],
                "label": part.get("label") or "", "fsType": part.get("fstype") or "", "size": int(part.get("size") or 0),
                "model": (disk.get("model") or "").strip(), "serial": (disk.get("serial") or "").strip(),
                "mountPoint": mounts[0] if mounts else None,
                "system": reason is not None, "systemReason": reason,
                "backingFile": backing.get(disk["path"]) if kind == "virtual" else None,
            }
    return result


def candidates():
    """Filesystems the agent is allowed to manage, keyed by UUID."""
    return {k: v for k, v in scan().items() if not v["system"]}


def usb_device_dir(disk_path):
    """sysfs folder of the USB device a disk hangs off (has an 'authorized' file)."""
    path = os.path.realpath(f"/sys/block/{os.path.basename(disk_path)}")
    while path != "/":
        path = os.path.dirname(path)
        if os.path.exists(os.path.join(path, "authorized")) and os.path.exists(os.path.join(path, "idVendor")):
            return path
    return None


def power_state(disk):
    # -C asks the drive without waking it up
    out = run("hdparm", "-C", disk, check=False)
    m = re.search(r"drive state is:\s*(.+)", out)
    return m.group(1).strip() if m else "unknown"


def op_list(_):
    with LOCK:
        live = scan()
        known = load_known()
        changed = False
        for uid, d in live.items():
            if d["system"]:
                continue
            entry = {k: d[k] for k in ("label", "fsType", "size", "model", "serial", "kind")}
            if d["kind"] == "usb":
                entry["usbDir"] = usb_device_dir(d["disk"]) or known.get(uid, {}).get("usbDir")
            if d["kind"] == "virtual":
                entry["backingFile"] = d["backingFile"]
            if known.get(uid) != entry:
                known[uid] = entry
                changed = True
        if changed:
            save_known(known)
        modes = mount_modes()
        disks = []
        powers = {}   # one hdparm per physical disk, not per partition; system disks are not asked at all
        for uid, d in live.items():
            state = "system" if d["system"] else "mounted" if d["mountPoint"] else "attached"
            if d["kind"] in ("usb", "sata") and not d["system"]:
                if d["disk"] not in powers:
                    powers[d["disk"]] = power_state(d["disk"])
                power = powers[d["disk"]]
            else:
                power = "active/idle"
            ro = modes.get(d["mountPoint"]) if d["mountPoint"] else None
            disks.append({**{k: v for k, v in d.items() if k not in ("backingFile",)},
                          "state": state, "power": power, "readOnly": ro})
        for uid, k in known.items():
            if uid not in live:
                disks.append({"id": uid, "kind": k["kind"], "label": k["label"], "fsType": k["fsType"], "size": k["size"],
                              "model": k.get("model", ""), "serial": k.get("serial", ""), "device": None, "disk": None,
                              "mountPoint": None, "system": False, "systemReason": None,
                              "state": "off", "power": "off", "readOnly": None})
        return {"disks": disks}


def find(uid):
    if not isinstance(uid, str) or not UUID_RE.match(uid):
        raise Refused("invalid disk id")
    d = scan().get(uid)
    if d and d["system"]:
        raise Refused("system disk; DiskHaven never touches it")
    if not d:
        raise Refused("disk not found or not allowed")
    return d


def set_device_ro(device, read_only):
    run("blockdev", "--setro" if read_only else "--setrw", device)


def do_mount(d, read_only):
    target = os.path.join(CFG["mountRoot"], d["id"])
    os.makedirs(target, exist_ok=True)
    if os.path.ismount(target):
        # something (e.g. a cloned disk with the same UUID) is already mounted here: never stack a second mount
        raise Refused("another filesystem is already mounted at this disk's folder; unplug the duplicate disk first")
    uid, gid = CFG["uid"], CFG["gid"]
    base = "nosuid,nodev,noexec,noatime"
    if read_only:
        base = "ro," + base
        if d["fsType"] in RO_EXTRA:
            base += "," + RO_EXTRA[d["fsType"]]
    set_device_ro(d["device"], read_only)
    try:
        if d["fsType"] in ("exfat", "vfat"):
            run("mount", "-t", d["fsType"], "-o", f"{base},uid={uid},gid={gid},fmask=0133,dmask=0022", d["device"], target)
        elif d["fsType"] == "ntfs":
            run("mount", "-t", "ntfs3", "-o", f"{base},uid={uid},gid={gid},fmask=0133,dmask=0022", d["device"], target)
        else:
            run("mount", "-t", d["fsType"], "-o", base, d["device"], target)
    except Refused:
        try:
            os.rmdir(target)
        except OSError:
            pass
        raise
    if not read_only:
        adopt_fresh_root(target)
    return target


def adopt_fresh_root(target):
    """A freshly formatted ext4 / xfs / btrfs disk has a root folder owned by root, so DiskHaven (uid 1000) could
    not write to it. Only when that root folder is empty (apart from lost+found) is its owner set to DiskHaven's
    user; a disk that already holds anything is left exactly as it is."""
    try:
        st = os.stat(target)
        if st.st_uid != 0 or set(os.listdir(target)) - {"lost+found"}:
            return
        os.chown(target, CFG["uid"], CFG["gid"])
    except OSError:
        pass


def op_mount(req):
    # read-only unless the client explicitly asks for a writable mount
    read_only = req.get("readOnly") is not False
    with LOCK:
        d = find(req.get("id"))
        if d["mountPoint"]:
            current = mount_modes().get(d["mountPoint"])
            if current == read_only:
                return {"mountPoint": d["mountPoint"], "readOnly": current}
            # mode change: unmount and mount again (a remount cannot drop noload / norecovery)
            unmount(d)
        target = do_mount(d, read_only)
        return {"mountPoint": target, "readOnly": mount_modes().get(target, read_only)}


def unmount(d):
    if d["mountPoint"]:
        if not d["mountPoint"].startswith(CFG["mountRoot"] + "/"):
            raise Refused("disk is mounted outside DiskHaven; not touching it")
        run("sync")
        run("umount", d["mountPoint"])
        try:
            os.rmdir(d["mountPoint"])
        except OSError:
            pass


def op_unmount(req):
    with LOCK:
        unmount(find(req.get("id")))
        return {}


def op_standby(req):
    with LOCK:
        d = find(req.get("id"))
        if d["kind"] in ("usb", "sata"):
            run("hdparm", "-y", d["disk"])
        return {}


def op_power_off(req):
    with LOCK:
        d = find(req.get("id"))
        if d["kind"] == "sata":
            raise Refused("internal disks cannot be powered off; use standby")
        unmount(d)
        if d["kind"] == "virtual":
            run("losetup", "-d", d["disk"])
        else:
            known = load_known()
            known.setdefault(d["id"], {})["usbDir"] = usb_device_dir(d["disk"])
            save_known(known)
            run("udisksctl", "power-off", "--no-user-interaction", "-b", d["disk"])
        return {}


def op_power_on(req):
    uid = req.get("id")
    if not isinstance(uid, str) or not UUID_RE.match(uid):
        raise Refused("invalid disk id")
    with LOCK:
        live = scan()
        if uid in live:
            if live[uid]["system"]:
                raise Refused("system disk; DiskHaven never touches it")
            return {}
        k = load_known().get(uid)
        if not k:
            raise Refused("unknown disk")
        if k["kind"] == "virtual":
            img = k.get("backingFile") or ""
            if not CFG.get("virtualDir") or not os.path.realpath(img).startswith(os.path.realpath(CFG["virtualDir"]) + "/"):
                raise Refused("image not allowed")
            if not os.path.exists(img):
                raise Refused("disk is unplugged; connect it again")
            if run("losetup", "-j", img, check=False):
                raise Refused("image is already attached")
            run("losetup", "-f", img)
        elif k["kind"] == "usb":
            usb = k.get("usbDir")
            if not usb or not usb.startswith("/sys/devices/") or not os.path.exists(os.path.join(usb, "authorized")):
                raise Refused("disk is unplugged; connect it again")
            # re-authorising the USB device makes the kernel enumerate the disk again
            for v in ("0", "1"):
                with open(os.path.join(usb, "authorized"), "w") as f:
                    f.write(v)
                time.sleep(1)
        else:
            raise Refused("disk is unplugged; connect it again")
    for _ in range(30):
        if uid in candidates():
            return {}
        time.sleep(1)
    raise Refused("disk did not come back; unplug and reconnect it")


def op_smart(req):
    # reading health data writes nothing, so it is allowed for system disks too
    uid = req.get("id")
    if not isinstance(uid, str) or not UUID_RE.match(uid):
        raise Refused("invalid disk id")
    d = scan().get(uid)
    if not d:
        raise Refused("disk not found or not allowed")
    if d["kind"] not in ("usb", "sata", "nvme"):
        return {"available": False}
    args = ["smartctl", "-j", "-H", "-A", "-n", "standby"]
    if d["kind"] == "usb":
        args += ["-d", "sat"]
    p = subprocess.run(args + [d["disk"]], capture_output=True, text=True, timeout=60)
    try:
        j = json.loads(p.stdout)
    except ValueError:
        return {"available": False}
    attrs = {a["name"]: a["raw"]["value"] for a in j.get("ata_smart_attributes", {}).get("table", [])}
    nvme = j.get("nvme_smart_health_information_log", {})
    return {
        "available": "smart_status" in j,
        "passed": j.get("smart_status", {}).get("passed"),
        "temperature": j.get("temperature", {}).get("current"),
        "powerOnHours": j.get("power_on_time", {}).get("hours"),
        "reallocated": attrs.get("Reallocated_Sector_Ct"),
        "pending": attrs.get("Current_Pending_Sector"),
        "uncorrectable": attrs.get("Offline_Uncorrectable", nvme.get("media_errors")),
    }


OPS = {"list": op_list, "mount": op_mount, "unmount": op_unmount, "standby": op_standby,
       "powerOff": op_power_off, "powerOn": op_power_on, "smart": op_smart}


class Handler(socketserver.StreamRequestHandler):
    def handle(self):
        try:
            req = json.loads(self.rfile.readline(65536))
            op = OPS.get(req.get("op"))
            if not op:
                raise Refused("unknown operation")
            res = {"ok": True, **op(req)}
        except Refused as e:
            res = {"ok": False, "error": str(e)}
        except Exception as e:  # never crash the agent on one bad request
            res = {"ok": False, "error": f"internal error: {type(e).__name__}"}
            print(f"error in request: {e!r}", file=sys.stderr, flush=True)
        self.wfile.write((json.dumps(res) + "\n").encode())


class Server(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
    daemon_threads = True


def main():
    sock = CFG["socket"]
    os.makedirs(os.path.dirname(sock), exist_ok=True)
    os.makedirs(CFG["mountRoot"], exist_ok=True)
    if os.path.exists(sock):
        os.unlink(sock)
    with Server(sock, Handler) as server:
        os.chown(sock, 0, CFG["gid"])
        os.chmod(sock, 0o660)
        print(f"diskhaven-diskd listening on {sock} (usb={CFG['usb']}, sata={CFG['sata']}, virtualDir={CFG['virtualDir']})", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
