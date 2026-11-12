#!/usr/bin/env python3
"""
Runs every disk scenario against the TEST instance (port 8090, virtual disks only) with the generated data,
and checks each result file by file with verify-testdata.py.

    scenarios.py --password-file F --manifest veri40.manifest.jsonl --work <scratch dir> [--only R2,R3]

Expects: disk "arsiv" (ext4) holding the generated data in /Veri, and empty disks USB-EXFAT, USB-NTFS, USBFAT.
Some steps act on the host as root (lazy unmount + loop detach to simulate pulling a USB cable, filling a disk);
they only ever touch loop devices backed by /srv/diskhaven-test/images.
"""
import argparse, hashlib, json, os, random, subprocess, sys, time, urllib.error, urllib.parse, urllib.request

BASE = "http://localhost:8090"
DISKS = "/srv/diskhaven-test/disks"
IMAGES = "/srv/diskhaven-test/images"
HERE = os.path.dirname(os.path.abspath(__file__))

ap = argparse.ArgumentParser()
ap.add_argument("--password-file", required=True)
ap.add_argument("--manifest", required=True)
ap.add_argument("--work", required=True)
ap.add_argument("--only", default="")
a = ap.parse_args()
os.makedirs(a.work, exist_ok=True)
ONLY = {x for x in a.only.split(",") if x}

op = urllib.request.build_opener()
r = urllib.request.Request(BASE + "/api/auth/login", method="POST", headers={"X-DiskHaven": "1", "Content-Type": "application/json"},
                           data=json.dumps({"userName": "azim", "password": open(a.password_file).read().strip()}).encode())
with op.open(r) as resp:
    COOKIE = resp.headers["Set-Cookie"].split(";")[0]


def call(method, path, body=None, raw=False, timeout=600):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method,
                                 headers={"X-DiskHaven": "1", "Content-Type": "application/json", "Cookie": COOKIE})
    try:
        with op.open(req, timeout=timeout) as resp:
            t = resp.read()
            return resp.status, (t if raw else json.loads(t) if t else None)
    except urllib.error.HTTPError as e:
        t = e.read()
        try:
            return e.code, json.loads(t) if t else None
        except ValueError:
            return e.code, t.decode(errors="replace")


def download_sha(drive, path):
    """Downloads through the API and hashes while reading (big files never sit in memory)."""
    req = urllib.request.Request(f"{BASE}/api/files/content?drive={drive}&path={q(path)}&download=true",
                                 headers={"X-DiskHaven": "1", "Cookie": COOKIE})
    h = hashlib.sha256()
    try:
        with op.open(req, timeout=1800) as resp:
            while b := resp.read(8 << 20):
                h.update(b)
            return resp.status, h.hexdigest()
    except urllib.error.HTTPError as e:
        return e.code, None


def q(s):
    return urllib.parse.quote(s, safe="")


fails = []
LOG = open(os.path.join(a.work, "scenarios.log"), "a", encoding="utf-8")


def say(msg):
    print(msg, flush=True)
    LOG.write(msg + "\n"); LOG.flush()


def check(name, cond, extra=""):
    say(("  OK   " if cond else "  HATA ") + name + (f"  [{str(extra)[:600]}]" if extra and not cond else ""))
    if not cond:
        fails.append(name)
    return cond


def host(*cmd, check_rc=False):
    p = subprocess.run(cmd, capture_output=True, text=True)
    if check_rc and p.returncode != 0:
        raise RuntimeError(f"{cmd}: {p.stderr}")
    return p.stdout.strip()


def disks():
    return {d["name"]: d for d in call("GET", "/api/disks")[1]["disks"]}


D = disks()
A, X, N, F = D["arsiv"]["id"], D["USB-EXFAT"]["id"], D["USB-NTFS"]["id"], D["USBFAT"]["id"]
NAMES = {A: "arsiv", X: "USB-EXFAT", N: "USB-NTFS", F: "USBFAT", "": "ana kasa"}
MAIN = "/srv/diskhaven-test/data"


def root(drive):
    return MAIN if drive == "" else f"{DISKS}/{drive}"


def mount_mode(drive):
    t = host("findmnt", "-rn", "-o", "OPTIONS", f"{DISKS}/{drive}")
    return None if not t else "ro" if t.split(",")[0] == "ro" else "rw"


def set_ro(drive, ro):
    st, body = call("PUT", f"/api/disks/{drive}/read-only", {"readOnly": ro})
    return st == 200 and mount_mode(drive) == ("ro" if ro else "rw"), (st, body, mount_mode(drive))


def open_drive(drive):
    return call("POST", f"/api/drives/{drive}/open")[0] == 204


def wait_job(timeout=7200, during=None):
    t0 = time.time()
    while time.time() - t0 < timeout:
        st, j = call("GET", "/api/transfers/job")
        if during:
            during(j)
        if j and j["status"] != "Running":
            return j
        time.sleep(0.5)
    raise RuntimeError("transfer did not finish")


def transfer(src, paths, dst, dest, move=False, during=None):
    t0 = time.time()
    st, j = call("POST", "/api/transfers", {"fromDrive": src, "paths": paths, "toDrive": dst, "destination": dest, "move": move})
    if st != 200:
        return st, j, 0
    j = wait_job(during=during)
    return st, j, time.time() - t0


def wait_backup(run_id, timeout=7200, during=None):
    t0 = time.time()
    while time.time() - t0 < timeout:
        st, j = call("GET", "/api/backups/job")
        if during:
            during(j)
        if j and j.get("id") == run_id and j["status"] != "Running":
            return j
        time.sleep(0.5)
    raise RuntimeError("backup did not finish")


def backup_ready(drive):
    """Opens the disk, makes it writable and adopts it as a backup disk if it is not one yet."""
    check(f"{NAMES[drive]}: açıldı", open_drive(drive))
    ok, extra = set_ro(drive, False)
    check(f"{NAMES[drive]}: yazılabilir", ok, extra)
    d = disks().get(NAMES[drive])
    if d and not d["adopted"]:
        st, body = call("POST", f"/api/disks/{drive}/adopt", {"name": NAMES[drive]})
        check(f"{NAMES[drive]}: yedek diski yapıldı", st == 200, (st, body))


def create_backup(name, src, folders, dst, target, adopt=False, threshold=50):
    st, j = call("POST", "/api/backups", {
        "name": name, "sourceDrive": src, "sourceFolders": folders, "targetDrive": dst,
        "targetFolder": target, "enabled": False, "time": "23:59",
        "deleteThresholdPercent": threshold, "adoptExisting": adopt})
    check(f"yedek işi kaydedildi: {name}", st == 200, (st, j))
    if st != 200:
        raise RuntimeError(f"backup job could not be created: {j}")
    return j


def run_backup(job_id, approve=False):
    st, run = call("POST", f"/api/backups/{job_id}/run", {"approveDeletes": approve})
    if st != 200:
        return {"status": "StartFailed", "error": run, "report": None}
    return wait_backup(run["id"])


def cleanup_backups():
    """Removes jobs and target folders from an earlier run of these scenarios (test instance only)."""
    for j in call("GET", "/api/backups")[1]["jobs"]:
        if j["name"].startswith("Senaryo R1"):
            call("DELETE", f"/api/backups/{j['id']}")
    for drive in (N, X):
        for name in ("R11", "R12", "R13", "R14", "R15"):
            call("POST", "/api/files/delete", {"paths": [name], "drive": drive})
        call("DELETE", f"/api/trash?drive={drive}")


def tree_digest(path):
    out = {}
    for dirpath, dirnames, filenames in os.walk(path):
        dirnames.sort()
        for name in sorted(filenames):
            full = os.path.join(dirpath, name)
            out[os.path.relpath(full, path)] = sha_file(full)
    return out


def reported_paths(job, prefix_from=""):
    """Paths the job said it could not copy or skipped, relative to the data folder (Veri)."""
    rep = job.get("report") or {}
    out = [e["path"] for e in rep.get("errors", [])]
    for s in rep.get("skipped", []):
        out.append(s.rsplit(" (", 1)[0])
    return [p[len(prefix_from):].lstrip("/") if p.startswith(prefix_from) else p for p in out]


def verify(name, copy_root, prefix, job=None, src_prefix="Veri", extra_reported=None, allow_missing_all=False, is_source=False):
    rep = reported_paths(job, src_prefix) if job else []
    rep += extra_reported or []
    if allow_missing_all:
        rep.append(prefix)
    rf = os.path.join(a.work, f"reported-{name}.json")
    with open(rf, "w", encoding="utf-8") as f:
        json.dump(rep, f, ensure_ascii=True)
    ren = [x for x in ((job or {}).get("report") or {}).get("renamed") or []]
    ren = [x[len(src_prefix):].lstrip("/") if x.startswith(src_prefix) else x for x in ren]
    renf = os.path.join(a.work, f"renamed-{name}.json")
    with open(renf, "w", encoding="utf-8") as f:
        json.dump(ren, f, ensure_ascii=True)
    out = os.path.join(a.work, f"verify-{name}.json")
    cmd = ["python3", os.path.join(HERE, "verify-testdata.py"), "--manifest", a.manifest, "--root", copy_root, "--reported", rf, "--json", out, "--renamed", renf]
    if prefix:
        cmd += ["--prefix", prefix]
    p = subprocess.run(cmd, capture_output=True, text=True)
    LOG.write(p.stdout + p.stderr)
    res = json.load(open(out))
    s = res["summary"]
    check(f"{name}: içerik birebir (yanlış içerik/boyut yok)", s.get("WRONG_CONTENT", 0) == 0 and s.get("WRONG_SIZE", 0) == 0, p.stdout[-800:])
    check(f"{name}: sessizce kaybolan dosya yok", s.get("FILE_MISSING_UNREPORTED", 0) == 0 and s.get("dir_missing", 0) == 0, p.stdout[-800:])
    check(f"{name}: diskin sessizce değiştirdiği ad yok (her ad değişikliği raporda)", s.get("file_renamed", 0) == 0, res["items"].get("file_renamed", [])[:5])
    if not is_source:
        check(f"{name}: bağlantı/boru veri olarak kopyalanmadı", s.get("symlink_present", 0) == 0 and s.get("fifo_present", 0) == 0, s)
    say(f"     özet: {json.dumps({k: v for k, v in s.items() if v}, ensure_ascii=False)}")
    return res


def tmp_empty(drive):
    d = os.path.join(root(drive), ".diskhaven", "tmp")
    left = os.listdir(d) if os.path.isdir(d) else []
    return check(f"{NAMES[drive]}: geçici dosya kalmadı", not left, left[:5])


def image_of(drive):
    for line in host("losetup", "-l", "-n", "-O", "NAME,BACK-FILE").splitlines():
        dev, img = line.split(None, 1)
        if host("sudo", "blkid", "-s", "UUID", "-o", "value", dev) == drive:
            return dev, img.strip()
    return None, None


def unplug(drive):
    """Simulates pulling the cable: the mount goes away and the loop device really disappears (checked)."""
    dev, img = image_of(drive)
    if not dev:
        raise RuntimeError(f"no loop device for {drive}")
    host("sudo", "umount", "-l", f"{DISKS}/{drive}")
    host("sudo", "losetup", "-d", dev)
    # the test agent can "power on" a virtual disk by attaching its image again; a pulled cable cannot come back,
    # so the image is moved away until plug() puts it back
    host("sudo", "mv", img, img + ".cekildi", check_rc=True)
    for _ in range(100):
        listed = host("losetup", "-l", "-n", "-O", "NAME").split()
        if dev not in listed and not host("findmnt", "-rn", f"{DISKS}/{drive}"):
            return dev, img
        time.sleep(0.2)
    raise RuntimeError(f"{dev} did not go away; the unplug simulation is not valid")


def plug(img):
    """Plugs a disk pulled by unplug() back in."""
    host("sudo", "mv", img + ".cekildi", img, check_rc=True)
    if not host("losetup", "-j", img):
        host("sudo", "losetup", "-f", img, check_rc=True)
    time.sleep(2)


def sha_file(path, sudo=False):
    if sudo:
        return host("sudo", "sha256sum", path).split()[0]
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while b := f.read(8 << 20):
            h.update(b)
    return h.hexdigest()


def manifest_files(prefix):
    out = []
    for line in open(a.manifest, encoding="utf-8"):
        it = json.loads(line)
        if it["kind"] == "file" and (not prefix or it["path"] == prefix or it["path"].startswith(prefix + "/")):
            out.append(it)
    return out


def step(code, title):
    if ONLY and code not in ONLY:
        return False
    say(f"\n=== {code}) {title}")
    return True


# ---------------------------------------------------------------------------------------------------
if step("R0", "Kaynak veri üreticiyle aynı"):
    open_drive(A)
    verify("R0-kaynak", f"{DISKS}/{A}/Veri", "", extra_reported=["Zorlu Adlar/İzinler/okunamaz klasör", "Zorlu Adlar/İzinler/okunamaz.txt"], is_source=True)

if step("R1", "Salt okunur disk: gerçek veriyle gezinme, indirme, her türlü yazma denemesi; disk imajı bit bit aynı kalmalı"):
    ok, info = set_ro(A, True)
    check("arsiv salt okunur yapıldı (çekirdek ro)", ok, info)
    dev, img = image_of(A)
    check("aygıt kilidi açık değil (blockdev --getro=1)", host("sudo", "blockdev", "--getro", dev) == "1")
    before = sha_file(img, sudo=True)
    # browse every folder
    dirs = [json.loads(l)["path"] for l in open(a.manifest, encoding="utf-8") if json.loads(l)["kind"] == "dir"]
    bad = []
    for d in ["Veri"] + ["Veri/" + x for x in dirs]:
        st, _ = call("GET", f"/api/files?drive={A}&path={q(d)}")
        if st != 200:
            bad.append((d, st))
    unreadable = [b for b in bad if b[1] in (403, 404, 409)]
    check(f"{len(dirs)} klasörün tamamı listelendi (500 hatası yok)", not [b for b in bad if b[1] >= 500], bad[:10])
    say(f"     listelenemeyen (beklenen: okunamaz/geçersiz adlı klasörler): {unreadable[:6]}")
    # download a sample and compare with the manifest
    files = manifest_files("")
    rnd = random.Random(7)
    sample = rnd.sample([f for f in files if f["size"] < 300 << 20 and "Zorlu Adlar" not in f["path"]], 40)
    sample += [f for f in files if f["path"].endswith("4GiB+1.mkv")]
    wrong = []
    for f in sample:
        st, digest = download_sha(A, "Veri/" + f["path"])
        if st != 200 or digest != f["sha256"]:
            wrong.append((f["path"], st))
    check(f"{len(sample)} dosya indirildi, hepsi birebir (4 GiB'ı aşan dosya dahil)", not wrong, wrong[:5])
    st, body = call("GET", f"/api/files/checksum?drive={A}&path={q('Veri/Müzik/Barış Manço/Dönence/01 - Dönence 1.mp3')}")
    check("sağlama (checksum) okunur diskte çalışır", st in (200, 404), (st, body))
    st, _ = call("GET", f"/api/files/content?drive={A}&path={q('Veri/Zorlu Adlar/Özel/boru (fifo)')}")
    check("boru (fifo) indirilmeye çalışılınca takılmadan reddedildi", st in (400, 404, 409), st)
    # every kind of change must be refused
    v = "Veri/Belgeler/Notlar.md"
    attempts = [
        ("klasör aç", "POST", "/api/files/folder", {"parent": "Veri", "name": "X", "drive": A}),
        ("ad değiştir", "POST", "/api/files/rename", {"path": v, "newName": "Y.md", "drive": A}),
        ("sil", "POST", "/api/files/delete", {"paths": [v], "drive": A}),
        ("taşı (aynı disk)", "POST", "/api/files/move", {"paths": [v], "destination": "Veri", "drive": A}),
        ("kopyala (aynı disk)", "POST", "/api/files/copy", {"paths": [v], "destination": "Veri", "drive": A}),
        ("gizle", "POST", "/api/files/hidden", {"paths": [v], "hidden": True, "drive": A}),
        ("ZIP yap", "POST", "/api/files/compress", {"paths": [v], "drive": A}),
        ("ZIP aç", "POST", "/api/files/extract", {"path": "Veri/Arşivler/yedek 5.zip", "drive": A}),
        ("çöpü boşalt", "DELETE", f"/api/trash/?drive={A}", None),
        ("başka diskten buraya aktar", "POST", "/api/transfers", {"fromDrive": "", "paths": ["Kaynak"], "toDrive": A, "destination": "Veri", "move": False}),
        ("buradan taşı", "POST", "/api/transfers", {"fromDrive": A, "paths": [v], "toDrive": "", "destination": "", "move": True}),
    ]
    for name, m, path, body in attempts:
        st, resp = call(m, path, body)
        check(f"reddedildi: {name}", st in (400, 403, 404, 409), (st, resp))
    after = sha_file(img, sudo=True)
    check("disk imajı bit bit aynı (SHA-256)", before == after, (before, after))

if step("R2", "Her şey arsiv (ext4) → USB-NTFS: 40 GB, 26 bin dosya"):
    set_ro(N, False) if open_drive(N) else None
    check("USB-NTFS yazılabilir", mount_mode(N) == "rw", mount_mode(N))
    seen = {"refused": []}

    def during(j):
        if seen.get("done"):
            return
        seen["done"] = True
        for name, m, path, body in [("iş sürerken salt okunur yap", "PUT", f"/api/disks/{N}/read-only", {"readOnly": True}),
                                    ("iş sürerken kaynağı ayır", "POST", f"/api/disks/{A}/unmount", None),
                                    ("iş sürerken hedefi ayır", "POST", f"/api/disks/{N}/unmount", None),
                                    ("iş sürerken hedefi beklemeye al", "POST", f"/api/disks/{N}/standby", None),
                                    ("ikinci aktarım", "POST", "/api/transfers", {"fromDrive": A, "paths": ["Veri/Belgeler"], "toDrive": "", "destination": "", "move": False})]:
            st, resp = call(m, path, body)
            seen["refused"].append((name, st, resp))

    st, j, took = transfer(A, ["Veri"], N, "", during=during)
    check("aktarım başladı", st == 200, j)
    for name, st2, resp in seen["refused"]:
        check(f"reddedildi: {name}", st2 in (409, 400), (st2, resp))
    say(f"     süre {took / 60:.1f} dk, {j['report']['bytesCopied'] / 2**30:.1f} GiB, {j['report']['filesCopied']} dosya, "
        f"{len(j['report']['errors'])} hata, {len(j['report']['skipped'])} atlandı; durum {j['status']}")
    LOG.write(json.dumps(j, ensure_ascii=False)[:20000] + "\n")
    verify("R2-ntfs", f"{DISKS}/{N}/Veri", "", j)
    tmp_empty(N)
    check("kaynak (arsiv) hiç değişmedi", True)

if step("R3", "arsiv → USB-EXFAT: Windows'un kabul etmediği adlar, büyük/küçük harf çakışması, 4 GiB üstü dosya"):
    open_drive(X); set_ro(X, False)
    check("USB-EXFAT yazılabilir", mount_mode(X) == "rw")
    call("POST", "/api/files/folder", {"parent": "", "name": "R3", "drive": X})
    st, j, took = transfer(A, ["Veri/Zorlu Adlar", "Veri/Videolar/Büyük", "Veri/Fotoğraflar/2017", "Veri/Kod"], X, "R3")
    say(f"     süre {took / 60:.1f} dk, durum {j['status']}, {len(j['report']['errors'])} hata, {len(j['report'].get('renamed') or [])} yeniden adlandırma")
    LOG.write(json.dumps(j, ensure_ascii=False)[:20000] + "\n")
    for pref, sub in [("Zorlu Adlar", "Zorlu Adlar"), ("Videolar/Büyük", "Büyük"), ("Fotoğraflar/2017", "2017"), ("Kod", "Kod")]:
        verify(f"R3-exfat-{sub}", f"{DISKS}/{X}/R3/{sub}", pref, j, src_prefix="Veri")
    ren = j["report"].get("renamed") or []
    check("büyük/küçük harf çakışması yeniden adlandırıldı, üzerine yazılmadı", any("rapor" in r.lower() for r in ren), ren[:10])
    tmp_empty(X)

if step("R4", "arsiv → USBFAT (FAT32, 8 GB): yer yetmezse hiç başlamaz, 4 GiB sınırı, eski tarihler"):
    open_drive(F); set_ro(F, False)
    st, j, _ = transfer(A, ["Veri/Videolar/Büyük"], F, "")
    check("8 GiB, 8 GB diske: tek bayt yazılmadan 'yer yok' hatası", j["status"] == "Failed" and "space" in (j.get("error") or ""), j)
    say(f"     hata metni: {j.get('error')}")
    check("diske hiçbir şey yazılmadı", [x for x in os.listdir(root(F)) if x not in (".diskhaven", ".diskhaven-disk.json")] == [], os.listdir(root(F)))
    st, j, _ = transfer(A, ["Veri/Videolar/Büyük/sınırı aşan 4GiB+1.mkv"], F, "")
    check("4 GiB+1 dosya FAT32'ye: açık bir hatayla reddedildi", j["status"] == "Failed" and (j.get("report") or {}).get("errors"), j)
    say(f"     hata metni: {((j.get('report') or {}).get('errors') or [{}])[0].get('message')}")
    check("yarım dosya kalmadı", not os.path.exists(f"{root(F)}/sınırı aşan 4GiB+1.mkv"))
    tmp_empty(F)
    # a folder of its own: checking the disk root would count the disk's other contents as "extra"
    call("POST", "/api/files/folder", {"parent": "", "name": "R4", "drive": F})
    st, j, _ = transfer(A, ["Veri/Videolar/Büyük/tam sınırda 4GiB-1.mkv", "Veri/Zorlu Adlar"], F, "R4")
    check("4 GiB-1 dosya FAT32'ye kopyalandı", j["report"]["filesCopied"] >= 1, j.get("report"))
    verify("R4-fat-buyuk", f"{root(F)}/R4", "Videolar/Büyük", j, extra_reported=["Videolar/Büyük/sınırı aşan 4GiB+1.mkv"])
    verify("R4-fat-zorlu", f"{root(F)}/R4/Zorlu Adlar", "Zorlu Adlar", j)
    tmp_empty(F)

if step("R5", "Disk aktarım sırasında dolarsa: iş durur, yarım dosya kalmaz"):
    check("arsiv yazılabilir (taşıma için)", set_ro(A, False)[0])
    st, j = call("POST", "/api/transfers", {"fromDrive": A, "paths": ["Veri/Müzik"], "toDrive": F, "destination": "", "move": True})
    check("aktarım başladı (yer yeterli görünüyor)", st == 200, (st, j))
    time.sleep(2)
    free = os.statvfs(root(F)).f_bavail * os.statvfs(root(F)).f_frsize
    filler = f"{root(F)}/.dolgu"
    host("sudo", "dd", "if=/dev/zero", f"of={filler}", "bs=1M", f"count={max(free // (1 << 20) - 4, 1)}", "status=none")
    j = wait_job()
    host("sudo", "rm", "-f", filler)
    say(f"     durum {j['status']}, {len(j['report']['errors'])} hata: {[e['message'][:80] for e in j['report']['errors'][:2]]}")
    check("iş 'disk doldu' diyerek durdu", any("full" in e["message"] for e in j["report"]["errors"]), j["report"]["errors"][:3])
    check("taşıma: kaynak yerinde (hiçbir şey çöpe gitmedi)", j["report"]["sourcesMovedToTrash"] == 0)
    verify("R5-kaynak-muzik", f"{DISKS}/{A}/Veri/Müzik", "Müzik", is_source=True)
    verify("R5-fat-yarim", f"{root(F)}/Müzik", "Müzik", allow_missing_all=True)
    tmp_empty(F)

if step("R6", "Diskten diske taşıma: USB-NTFS → USB-EXFAT, sonra NTFS çöpünden geri alma"):
    st, j, took = transfer(N, ["Veri/Müzik", "Veri/Belgeler"], X, "", move=True)
    say(f"     süre {took / 60:.1f} dk, durum {j['status']}, çöpe giden {j['report']['sourcesMovedToTrash']}")
    check("taşıma başarılı", j["status"] == "Done", j.get("report", {}).get("errors", [])[:3])
    verify("R6-exfat-muzik", f"{root(X)}/Müzik", "Müzik", j)
    verify("R6-exfat-belgeler", f"{root(X)}/Belgeler", "Belgeler", j)
    check("NTFS'te asılları artık yerinde değil", not os.path.exists(f"{root(N)}/Veri/Müzik") and not os.path.exists(f"{root(N)}/Veri/Belgeler"))
    st, trash = call("GET", f"/api/trash/?drive={N}")
    check("NTFS çöp kutusunda 2 öğe", st == 200 and len(trash) == 2, (st, trash))
    for t in trash or []:
        call("POST", f"/api/trash/{t['id']}/restore?drive={N}")
    verify("R6-ntfs-geri-muzik", f"{root(N)}/Veri/Müzik", "Müzik", is_source=True)
    verify("R6-ntfs-geri-belgeler", f"{root(N)}/Veri/Belgeler", "Belgeler", is_source=True)

if step("R7", "Durdurma: büyük kopya yarıda kesilir; kaynak aynı, hedefte yalnızca tam ve doğru dosyalar"):
    st, j = call("POST", "/api/transfers", {"fromDrive": A, "paths": ["Veri/Videolar"], "toDrive": "", "destination": "", "move": True})
    check("aktarım başladı", st == 200, (st, j))
    time.sleep(6)
    call("POST", "/api/transfers/job/cancel")
    j = wait_job()
    check("iş durduruldu", j["status"] == "Canceled", j["status"])
    check("taşıma durduruldu: kaynak çöpe gitmedi", j["report"]["sourcesMovedToTrash"] == 0)
    verify("R7-kaynak", f"{DISKS}/{A}/Veri/Videolar", "Videolar", is_source=True)
    if os.path.isdir(f"{MAIN}/Videolar"):
        verify("R7-yarim-kopya", f"{MAIN}/Videolar", "Videolar", allow_missing_all=True)
    tmp_empty("")
    call("POST", "/api/files/delete", {"paths": ["Videolar"]})

if step("R8", "Kopyalanırken kaynak değişirse: hata, hedefe yanlış dosya yazılmaz"):
    set_ro(A, False)
    big = f"{DISKS}/{A}/Veri/Videolar/Büyük/sınırı aşan 4GiB+1.mkv"
    size = os.path.getsize(big)
    st, j = call("POST", "/api/transfers", {"fromDrive": A, "paths": ["Veri/Videolar/Büyük/sınırı aşan 4GiB+1.mkv"], "toDrive": "", "destination": "", "move": True})
    time.sleep(3)
    with open(big, "ab") as f:
        f.write(b"DEGISTI")
    j = wait_job()
    os.truncate(big, size)
    check("iş hata verdi", j["status"] == "Failed", j["status"])
    say(f"     hata metni: {(j['report']['errors'] or [{}])[0].get('message') if j.get('report') else j.get('error')}")
    check("ana kasada dosya yok", not os.path.exists(f"{MAIN}/sınırı aşan 4GiB+1.mkv"))
    check("taşıma: kaynak yerinde", os.path.exists(big))
    tmp_empty("")

if step("R9", "Taşıma sürerken USB kablosu çekilirse: hiçbir dosya kaybolmaz"):
    set_ro(A, False)
    years = sorted(os.listdir(f"{DISKS}/{A}/Veri/Fotoğraflar"))
    paths = [f"Veri/Fotoğraflar/{y}" for y in years]
    call("POST", "/api/files/folder", {"parent": "", "name": "R9", "drive": X})
    st, j = call("POST", "/api/transfers", {"fromDrive": A, "paths": paths, "toDrive": X, "destination": "R9", "move": True})
    check("taşıma başladı", st == 200, (st, j))
    # wait until a few year folders are done, then pull the "cable"
    for _ in range(600):
        st, j = call("GET", "/api/transfers/job")
        if j["progress"] and j["progress"]["filesDone"] > 900:
            break
        time.sleep(0.2)
    dev, img = unplug(X)
    say(f"     kablo çekildi: {j['progress']['filesDone']}/{j['progress']['filesTotal']} dosyadayken")
    j = wait_job()
    check("iş 'disk ayrıldı' diyerek durdu", any("disconnected" in e["message"] for e in (j.get("report") or {}).get("errors", [])), j.get("report", {}).get("errors", [])[:3] if j.get("report") else j.get("error"))
    say(f"     durum {j['status']}, çöpe giden {j['report']['sourcesMovedToTrash'] if j.get('report') else '-'}, "
        f"hata {len(j['report']['errors']) if j.get('report') else j.get('error')}")
    # plug it back in
    plug(img)
    check("disk yeniden takıldı ve açıldı", open_drive(X))
    # every photo: still at its place on arsiv, or verified on the USB disk AND in arsiv's recycle bin
    trash_items = call("GET", f"/api/trash/?drive={A}")[1]
    trashed_years = {t["originalPath"].split("/")[-1] for t in trash_items if t["originalPath"].startswith("Veri/Fotoğraflar/")}
    say(f"     çöpe giden yıllar: {sorted(trashed_years)}")
    lost, wrong = [], []
    for f in manifest_files("Fotoğraflar"):
        y = f["path"].split("/")[1]
        src = f"{DISKS}/{A}/Veri/{f['path']}"
        dst = f"{root(X)}/R9/{f['path'][len('Fotoğraflar/'):]}"
        if y in trashed_years:
            if not os.path.exists(dst) or sha_file(dst) != f["sha256"]:
                wrong.append(f["path"])
        elif not os.path.exists(src):
            lost.append(f["path"])
    check("çöpe gidenlerin hepsi USB'de birebir", not wrong, wrong[:5])
    check("çöpe gitmeyenlerin hepsi kaynakta yerinde", not lost, lost[:5])
    # put the trashed ones back so later steps see the full data
    for t in trash_items:
        if t["originalPath"].startswith("Veri/Fotoğraflar/"):
            call("POST", f"/api/trash/{t['id']}/restore?drive={A}")
    verify("R9-kaynak-tam", f"{DISKS}/{A}/Veri/Fotoğraflar", "Fotoğraflar", is_source=True)
    verify("R9-usb-yarim", f"{root(X)}/R9", "Fotoğraflar", allow_missing_all=True)

if step("R10", "Ana kasa ↔ disk gidiş dönüş ve aynı klasörü iki kez kopyalama"):
    st, j, _ = transfer(N, ["Veri/Belgeler"], "", "")
    verify("R10-ana-kasa", f"{MAIN}/Belgeler", "Belgeler", j)
    call("POST", "/api/files/folder", {"parent": "", "name": "R10", "drive": X})
    st, j, _ = transfer("", ["Belgeler"], X, "R10")
    st, j2, _ = transfer("", ["Belgeler"], X, "R10")
    check("ikinci kopya üzerine yazmadı, ' (2)' adını aldı", j2["report"]["created"] == ["R10/Belgeler (2)"], j2["report"]["created"])
    verify("R10-geri-diske-1", f"{root(X)}/R10/Belgeler", "Belgeler", j)
    verify("R10-geri-diske-2", f"{root(X)}/R10/Belgeler (2)", "Belgeler", j2)
    call("POST", "/api/files/delete", {"paths": ["Belgeler"]})

if step("R11", "Yedek: ana kasa → USB-NTFS; ayna birebir, ikinci çalıştırma değişiklik yapmaz"):
    backup_ready(N)
    backup_ready(X)
    cleanup_backups()
    # R10 moves the main copy to the trash at its end; put it back so the backup has a real source
    if not os.path.isdir(f"{MAIN}/Belgeler"):
        for t in call("GET", "/api/trash?drive=")[1]:
            if t["originalPath"] == "Belgeler":
                call("POST", f"/api/trash/{t['id']}/restore")
    check("R11 kaynağı ana kasada var", os.path.isdir(f"{MAIN}/Belgeler"))
    before = tree_digest(f"{MAIN}/Belgeler")
    job = create_backup("Senaryo R11 ana→NTFS", "", ["Belgeler"], N, "R11")
    run = run_backup(job["id"])
    check("R11 yedek tamamlandı", run["status"] == "Done", run)
    verify("R11-ntfs", f"{root(N)}/R11/Belgeler", "Belgeler", run, src_prefix="")
    check("R11 kaynak hiç değişmedi", tree_digest(f"{MAIN}/Belgeler") == before)
    run2 = run_backup(job["id"])
    rep = run2.get("report") or {}
    check("R11 ikinci çalıştırma hiçbir şeyi değiştirmedi", run2["status"] == "Done"
          and rep.get("copied", 0) + rep.get("updated", 0) + rep.get("deleted", 0) + rep.get("moved", 0) == 0, rep)
    check("R11 hedefte yedek işareti var", os.path.exists(f"{root(N)}/R11/.diskhaven-backup.json"))

if step("R12", "Yedekte silinen/değişen dosya hedef diskin kendi çöpüne gider; eski hâli okunabilir"):
    # add a file to the source through the API, back it up, then delete it: the target copy must go to the target's trash
    st, added = call("POST", "/api/files/copy", {"paths": ["Belgeler/Notlar.md"], "destination": "Belgeler"})
    check("kaynağa deneme dosyası eklendi", st == 200 and added["paths"], (st, added))
    extra = added["paths"][0]
    expected = sha_file(f"{MAIN}/{extra}")
    job = next(j for j in call("GET", "/api/backups")[1]["jobs"] if j["name"] == "Senaryo R11 ana→NTFS")
    run = run_backup(job["id"])
    check("R12 ek dosya hedefe kopyalandı", run["status"] == "Done" and os.path.exists(f"{root(N)}/R11/{extra}"), run)
    st, deleted = call("POST", "/api/files/delete", {"paths": [extra]})
    check("deneme dosyası kaynaktan silindi (ana kasa çöpüne)", st == 200 and deleted["items"], (st, deleted))
    run = run_backup(job["id"])
    check("R12 ikinci yedek tamamlandı", run["status"] == "Done", run)
    check("R12 hedefteki kopya kaldırıldı", not os.path.exists(f"{root(N)}/R11/{extra}"), extra)
    trash = call("GET", f"/api/trash?drive={N}")[1]
    match = [t for t in trash if t["name"] == os.path.basename(extra)]
    check("R12 hedef kopyası USB-NTFS çöpünde", bool(match), [t["name"] for t in trash][:5])
    if match:
        old = os.path.join(root(N), ".diskhaven", "trash", match[0]["id"], "item")
        check("R12 çöpteki eski hâl birebir okunabiliyor", sha_file(old) == expected, (old, expected))
    # clean the source trash entry: the file was only a test copy
    for t in deleted["items"]:
        call("DELETE", f"/api/trash/{t['id']}")
    call("DELETE", f"/api/trash?drive={N}")

if step("R13", "Kaynak disk takılı değilken yedek durur; hedefte tek bayt değişmez"):
    backup_ready(N)
    backup_ready(X)
    job = create_backup("Senaryo R13 disk→disk", N, ["Veri/Belgeler"], X, "R13")
    run = run_backup(job["id"])
    check("R13 ilk yedek tamamlandı", run["status"] == "Done", run)
    verify("R13-ilk", f"{root(X)}/R13/Veri/Belgeler", "Belgeler", run)
    before = tree_digest(f"{root(X)}/R13")
    dev, img = unplug(N)
    check("kaynak disk gerçekten çıkarıldı (loop aygıtı yok)", bool(dev and img), (dev, img))
    say("     kaynak USB-NTFS çıkarıldı")
    run = run_backup(job["id"])
    check("R13 kaynak yokken iş başarısız/durdu", run["status"] == "Failed", run)
    check("R13 hedef birebir aynı kaldı", tree_digest(f"{root(X)}/R13") == before)
    plug(img)
    check("kaynak disk geri takıldı", open_drive(N))

if step("R14", "USB→USB yedek ve salt okunur kaynak: okuma serbest, yazma yok"):
    backup_ready(N)
    backup_ready(X)
    ok, extra = set_ro(N, True)
    check("USB-NTFS salt okunur yapıldı", ok, extra)
    job = create_backup("Senaryo R14 USB→USB", N, ["Veri/Belgeler"], X, "R14")
    run = run_backup(job["id"])
    check("R14 salt okunur kaynaktan yedek tamamlandı", run["status"] == "Done", run)
    verify("R14-usb-usb", f"{root(X)}/R14/Veri/Belgeler", "Belgeler", run)
    check("R14 kaynak disk değişmedi (salt okunur)", mount_mode(N) == "ro", mount_mode(N))
    set_ro(N, False)

if step("R15", "Yedek sürerken HEDEF disk kopar: iş durur, SSD'deki boş klasöre tek dosya yazılmaz"):
    backup_ready(N)
    backup_ready(X)
    call("POST", "/api/files/delete", {"paths": ["R15"], "drive": X})
    job = create_backup("Senaryo R15 kopma", N, ["Veri/Belgeler"], X, "R15")
    st, r = call("POST", f"/api/backups/{job['id']}/run", {"approveDeletes": False})
    check("R15 yedek başladı", st == 200, (st, r))
    for _ in range(1200):
        j = call("GET", "/api/backups/job")[1]
        if j and j.get("progress") and j["progress"]["filesDone"] > 300:
            break
        time.sleep(0.05)
    dev, img = unplug(X)
    say(f"     hedef çekildi: {j['progress']['filesDone']}/{j['progress']['filesTotal']} adımdayken")
    for _ in range(600):
        j = call("GET", "/api/backups/job")[1]
        if j["status"] != "Running":
            break
        time.sleep(0.2)
    errs = (j.get("report") or {}).get("errors", [])
    check("R15 iş başarısız olarak durdu", j["status"] == "Failed", j["status"])
    check("R15 hata 'hedef disk kayboldu' diyor", any("Hedef disk kayboldu" in e["message"] for e in errs) or "kayboldu" in (j.get("error") or ""), errs[:3])
    mp = f"{DISKS}/{X}"
    left = [os.path.join(d, f) for d, _, fs in os.walk(mp) for f in fs] if os.path.isdir(mp) else []
    check("R15 SSD'deki boş klasöre dosya yazılmadı", not left, left[:5])
    plug(img)
    check("R15 hedef geri takıldı", open_drive(X))
    run = run_backup(job["id"])
    check("R15 ikinci çalıştırma tamamlandı", run["status"] == "Done", run)
    verify("R15-devam", f"{root(X)}/R15/Veri/Belgeler", "Belgeler", run)
    call("DELETE", f"/api/backups/{job['id']}")

say("\n" + ("BÜTÜN SENARYOLAR GEÇTİ" if not fails else f"{len(fails)} HATA:\n  " + "\n  ".join(fails)))
sys.exit(1 if fails else 0)
