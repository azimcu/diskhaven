#!/usr/bin/env python3
"""homevault-diskd: the only part of HomeVault that runs as root.

It does a short, fixed list of disk operations for the HomeVault container: list disks,
mount / unmount, spin down, power off / on (USB), read SMART health. The container talks to
it over a Unix socket with one JSON request per connection. Disks are identified by their
filesystem UUID only; the agent never accepts device paths from the client, never formats,
and refuses any disk that holds a system mount.

Which disks it may touch is set in the config file (default /etc/homevault/diskd.json):
  {"mountRoot": "/srv/homevault/disks", "socket": "/run/homevault/diskd.sock",
   "uid": 1000, "gid": 1000, "usb": true, "virtualDir": null}
"usb": true allows USB disks. "virtualDir" allows loop devices whose image file is in that
folder (used for development and tests, so real disks can be excluded entirely with usb=false).
Python standard library only.
"""
import json, os, re, socketserver, subprocess, sys, threading, time

CONFIG_PATH = sys.argv[1] if len(sys.argv) > 1 else "/etc/homevault/diskd.json"
CFG = {"mountRoot": "/srv/homevault/disks", "socket": "/run/homevault/diskd.sock", "uid": 1000, "gid": 1000,
       "usb": True, "virtualDir": None, "stateFile": "/var/lib/homevault/diskd-known.json"}
CFG.update(json.load(open(CONFIG_PATH)))
LOCK = threading.Lock()
UUID_RE = re.compile(r"^[A-Za-z0-9-]{4,64}$")
FS_TYPES = {"exfat", "ntfs", "vfat", "ext4", "btrfs", "xfs"}


class Refused(Exception):
    pass


def run(*cmd, check=True, timeout=60):
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
    if check and p.returncode != 0:
        raise Refused(f"{os.path.basename(cmd[0])} failed: {(p.stderr or p.stdout).strip()[:300]}")
    return p.stdout


def load_known():
    try:
        return json.load(open(CFG["stateFile"]))
    except (OSError, ValueError):
        return {}


def save_known(known):
    os.makedirs(os.path.dirname(CFG["stateFile"]), exist_ok=True)
    tmp = CFG["stateFile"] + ".tmp"
    json.dump(known, open(tmp, "w"), indent=2)
    os.replace(tmp, CFG["stateFile"])


def loop_backing():
    out = json.loads(run("losetup", "-J", "-l", check=False) or '{"loopdevices": []}')
    return {d["name"]: d.get("back-file") or "" for d in out.get("loopdevices", [])}


def block_devices():
    out = json.loads(run("lsblk", "-J", "-b", "-o",
                         "NAME,PATH,TYPE,SIZE,MODEL,SERIAL,TRAN,FSTYPE,LABEL,UUID,MOUNTPOINTS,PKNAME"))
    return out["blockdevices"]


def flatten(devs, parent=None):
    for d in devs:
        d["_parent"] = parent
        yield d
        yield from flatten(d.get("children") or [], d)


def holds_system_mount(disk):
    """True if the disk or any partition on it is mounted somewhere outside our mount root."""
    for d in flatten([disk]):
        for m in d.get("mountpoints") or []:
            # anything mounted elsewhere (/, /mnt/..., [SWAP]) means the disk is in use by the system
            if m and not m.startswith(CFG["mountRoot"] + "/"):
                return True
    return False


def candidates():
    """Filesystems the agent is allowed to manage, keyed by UUID."""
    result = {}
    backing = loop_backing() if CFG.get("virtualDir") else {}
    for disk in block_devices():
        if disk["type"] == "loop":
            if not CFG.get("virtualDir"):
                continue
            back = backing.get(disk["path"], "")
            if not back.startswith(os.path.realpath(CFG["virtualDir"]) + "/"):
                continue
            kind = "virtual"
        elif disk["type"] == "disk" and disk.get("tran") == "usb" and CFG.get("usb"):
            kind = "usb"
        else:
            continue
        if holds_system_mount(disk):
            continue
        for part in flatten([disk]):
            if part.get("uuid") and part.get("fstype") in FS_TYPES:
                mounts = [m for m in part.get("mountpoints") or [] if m]
                result[part["uuid"]] = {
                    "id": part["uuid"], "kind": kind, "device": part["path"], "disk": disk["path"],
                    "label": part.get("label") or "", "fsType": part["fstype"], "size": int(part.get("size") or 0),
                    "model": (disk.get("model") or "").strip(), "serial": (disk.get("serial") or "").strip(),
                    "mountPoint": mounts[0] if mounts else None,
                    "backingFile": backing.get(disk["path"]) if kind == "virtual" else None,
                }
    return result


def usb_device_dir(disk_path):
    """sysfs folder of the USB device a disk hangs off (has an 'authorized' file)."""
    path = os.path.realpath(f"/sys/block/{os.path.basename(disk_path)}")
    while path != "/":
        path = os.path.dirname(path)
        if os.path.exists(os.path.join(path, "authorized")) and os.path.exists(os.path.join(path, "idVendor")):
            return path
    return None


def power_state(disk):
    out = run("hdparm", "-C", disk, check=False)
    m = re.search(r"drive state is:\s*(.+)", out)
    return m.group(1).strip() if m else "unknown"


def op_list(_):
    with LOCK:
        live = candidates()
        known = load_known()
        changed = False
        for uid, d in live.items():
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
        disks = []
        for uid, d in live.items():
            state = "mounted" if d["mountPoint"] else "attached"
            power = power_state(d["disk"]) if d["kind"] == "usb" else "active/idle"
            disks.append({**{k: v for k, v in d.items() if k != "backingFile"}, "state": state, "power": power})
        for uid, k in known.items():
            if uid not in live:
                disks.append({"id": uid, "kind": k["kind"], "label": k["label"], "fsType": k["fsType"], "size": k["size"],
                              "model": k.get("model", ""), "serial": k.get("serial", ""), "device": None, "disk": None,
                              "mountPoint": None, "state": "off", "power": "off"})
        return {"disks": disks}


def find(uid):
    if not isinstance(uid, str) or not UUID_RE.match(uid):
        raise Refused("invalid disk id")
    d = candidates().get(uid)
    if not d:
        raise Refused("disk not found or not allowed")
    return d


def op_mount(req):
    with LOCK:
        d = find(req.get("id"))
        if d["mountPoint"]:
            return {"mountPoint": d["mountPoint"]}
        target = os.path.join(CFG["mountRoot"], d["id"])
        os.makedirs(target, exist_ok=True)
        uid, gid = CFG["uid"], CFG["gid"]
        base = "nosuid,nodev,noexec,noatime"
        if d["fsType"] in ("exfat", "vfat"):
            run("mount", "-t", d["fsType"], "-o", f"{base},uid={uid},gid={gid},fmask=0133,dmask=0022", d["device"], target)
        elif d["fsType"] == "ntfs":
            run("mount", "-t", "ntfs3", "-o", f"{base},uid={uid},gid={gid},fmask=0133,dmask=0022", d["device"], target)
        else:
            run("mount", "-o", base, d["device"], target)
        return {"mountPoint": target}


def unmount(d):
    if d["mountPoint"]:
        run("sync")
        run("umount", d["mountPoint"])
        if d["mountPoint"].startswith(CFG["mountRoot"] + "/"):
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
        if d["kind"] == "usb":
            run("hdparm", "-y", d["disk"])
        return {}


def op_power_off(req):
    with LOCK:
        d = find(req.get("id"))
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
        if uid in candidates():
            return {}
        k = load_known().get(uid)
        if not k:
            raise Refused("unknown disk")
        if k["kind"] == "virtual":
            img = k.get("backingFile") or ""
            if not CFG.get("virtualDir") or not os.path.realpath(img).startswith(os.path.realpath(CFG["virtualDir"]) + "/"):
                raise Refused("image not allowed")
            run("losetup", "-f", img)
        else:
            usb = k.get("usbDir")
            if not usb or not usb.startswith("/sys/devices/") or not os.path.exists(os.path.join(usb, "authorized")):
                raise Refused("disk is unplugged; connect it again")
            # re-authorising the USB device makes the kernel enumerate the disk again
            for v in ("0", "1"):
                with open(os.path.join(usb, "authorized"), "w") as f:
                    f.write(v)
                time.sleep(1)
    for _ in range(30):
        if uid in candidates():
            return {}
        time.sleep(1)
    raise Refused("disk did not come back; unplug and reconnect it")


def op_smart(req):
    d = find(req.get("id"))
    if d["kind"] != "usb":
        return {"available": False}
    p = subprocess.run(["smartctl", "-j", "-H", "-A", "-n", "standby", "-d", "sat", d["disk"]],
                       capture_output=True, text=True, timeout=60)
    try:
        j = json.loads(p.stdout)
    except ValueError:
        return {"available": False}
    attrs = {a["name"]: a["raw"]["value"] for a in j.get("ata_smart_attributes", {}).get("table", [])}
    return {
        "available": "smart_status" in j,
        "passed": j.get("smart_status", {}).get("passed"),
        "temperature": j.get("temperature", {}).get("current"),
        "powerOnHours": j.get("power_on_time", {}).get("hours"),
        "reallocated": attrs.get("Reallocated_Sector_Ct"),
        "pending": attrs.get("Current_Pending_Sector"),
        "uncorrectable": attrs.get("Offline_Uncorrectable"),
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
        print(f"homevault-diskd listening on {sock} (usb={CFG['usb']}, virtualDir={CFG['virtualDir']})", flush=True)
        server.serve_forever()


if __name__ == "__main__":
    main()
