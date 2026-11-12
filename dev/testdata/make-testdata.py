#!/usr/bin/env python3
"""
Builds a realistic home user's disk for testing DiskHaven with real data volumes: photos by year, phone and
camera videos, music albums, documents, code with git history and thousands of tiny files, archives, disk
images, phone backups, hidden files, and a folder of names and file kinds that break careless programs.

Every file is written once and recorded in a manifest (one JSON line per item: kind, path, size, sha256,
mtime, mode) next to - never inside - the data folder. verify-testdata.py compares any copy with it.

    make-testdata.py --out /mnt/x/Veri --size 50G --seeds <dir from make-seeds.sh>

The size is a target; the tree scales with it (2T gives ~150k photos and ~1M small files).
Only --out is written to (plus the manifest); --out must not exist or be empty.
"""
import argparse, hashlib, io, json, os, random, stat, subprocess, sys, tarfile, time, unicodedata, zipfile
from datetime import datetime, timezone

CHUNK = 8 << 20
GiB = 1 << 30
MiB = 1 << 20


def parse_size(s):
    units = {"K": 1 << 10, "M": MiB, "G": GiB, "T": 1 << 40}
    return int(float(s[:-1]) * units[s[-1].upper()]) if s[-1].upper() in units else int(s)


def ts(year, rnd):
    """A random moment in that year (UTC), with nanoseconds."""
    start = datetime(year, 1, 1, tzinfo=timezone.utc).timestamp()
    return int((start + rnd.random() * 365 * 86400) * 1e9)


class Gen:
    def __init__(self, out, manifest, seeds, rnd, quiet):
        self.out, self.rnd, self.seeds, self.quiet = out, rnd, seeds, quiet
        self.man = open(manifest, "w", encoding="utf-8")
        self.bytes = 0
        self.files = 0
        self.t0 = time.time()
        self.last = 0

    # --- recording -------------------------------------------------------------------------------
    def rec(self, **item):
        self.man.write(json.dumps(item, ensure_ascii=True) + "\n")

    def rel(self, path):
        return os.path.relpath(path, self.out)

    def progress(self):
        if self.quiet or time.time() - self.last < 5:
            return
        self.last = time.time()
        rate = self.bytes / max(1, time.time() - self.t0) / MiB
        print(f"  {self.files:>9,} files  {self.bytes / GiB:8.1f} GiB  {rate:6.0f} MiB/s", file=sys.stderr, flush=True)

    # --- writing ---------------------------------------------------------------------------------
    def write(self, path, data=b"", size=None, head=b"", mtime=None, mode=None, tail=b""):
        """Writes head + data, then random bytes until the file is `size` long (when given), then tail."""
        os.makedirs(os.path.dirname(path), exist_ok=True)
        h = hashlib.sha256()
        n = 0
        with open(path, "xb") as f:
            def put(b):
                nonlocal n
                f.write(b); h.update(b); n += len(b); self.bytes += len(b)
            put(head); put(data)
            if size is not None:
                left = size - n - len(tail)
                while left > 0:
                    b = self.rnd.randbytes(min(CHUNK, left))
                    put(b); left -= len(b)
                    self.progress()
            put(tail)
        if mode is not None:
            os.chmod(path, mode)
        if mtime is not None:
            os.utime(path, ns=(mtime, mtime))
        st = os.lstat(path)
        self.files += 1
        self.rec(kind="file", path=self.rel(path), size=n, sha256=h.hexdigest(), mtime=st.st_mtime_ns, mode=stat.S_IMODE(st.st_mode))
        self.progress()
        return n

    def seed(self, name):
        with open(os.path.join(self.seeds, name), "rb") as f:
            return f.read()

    def media(self, path, seed, size, mtime):
        """A real media file made unique: the seed, then random bytes up to `size` (players stop at the end
        of the real stream; trailing data is what many cameras write too)."""
        data = self.seed(seed)
        tag = os.urandom(16)
        return self.write(path, data=data, size=max(size, len(data) + 16), tail=tag, mtime=mtime)

    def finish_dirs(self):
        """Records every folder (after all files, so folder times are final) - empty ones included."""
        for root, dirs, _ in os.walk(self.out):
            for d in dirs:
                p = os.path.join(root, d)
                if os.path.islink(p):
                    continue
                st = os.lstat(p)
                self.rec(kind="dir", path=self.rel(p), mode=stat.S_IMODE(st.st_mode), mtime=st.st_mtime_ns)


# --- the tree ------------------------------------------------------------------------------------
MONTHS = ["01-Ocak", "02-Şubat", "03-Mart", "04-Nisan", "05-Mayıs", "06-Haziran", "07-Temmuz", "08-Ağustos",
          "09-Eylül", "10-Ekim", "11-Kasım", "12-Aralık"]
EVENTS = ["Bayram", "Doğum günü", "Tatil - Ayvalık", "Düğün", "Okul gösterisi", "Kar", "Piknik", "Yılbaşı", "Kapadokya"]
ARTISTS = {"Barış Manço": ["Dönence", "Sözüm Meclisten Dışarı"], "Sezen Aksu": ["Gülümse", "Işık Doğudan Yükselir"],
           "Cem Karaca": ["Yoksulluk Kader Olamaz"], "Ajda Pekkan": ["Süperstar"], "Müslüm Gürses": ["Aşk Tesadüfleri Sever"],
           "Pink Floyd": ["The Dark Side of the Moon", "Wish You Were Here"], "AC/DC": ["Back in Black"],
           "Sigur Rós": ["Ágætis byrjun"], "坂本龍一": ["戦場のメリークリスマス"]}
WORDS = "ev araba okul fatura kira sigorta tapu sözleşme dilekçe özgeçmiş rapor sunum not liste plan bütçe".split()


def photos(g, budget, years):
    rnd = g.rnd
    kinds = [("photo-phone.jpg", "IMG_{n:04d}.JPG", 3 * MiB, 6 * MiB), ("photo-portrait.jpg", "IMG_{n:04d}.jpg", 2 * MiB, 5 * MiB),
             ("photo-camera.jpg", "DSC{n:05d}.JPG", 8 * MiB, 14 * MiB), ("photo-iphone.heic", "IMG_{n:04d}.HEIC", 1 * MiB, 3 * MiB),
             ("screenshot.png", "Ekran Resmi {d} {n}.png", 200 << 10, 2 * MiB), ("web.webp", "indirilen-{n}.webp", 30 << 10, 400 << 10),
             ("old.bmp", "tarama{n}.bmp", 1 * MiB, 3 * MiB), ("scan.tiff", "Belge Tarama {n}.tiff", 4 * MiB, 20 * MiB),
             ("anim.gif", "komik-{n}.gif", 200 << 10, 3 * MiB)]
    weights = [40, 15, 12, 15, 8, 4, 1, 2, 3]
    used, n = 0, 1
    base = os.path.join(g.out, "Fotoğraflar")
    while used < budget:
        year = rnd.choice(years)
        folder = os.path.join(base, str(year), rnd.choice(MONTHS))
        if rnd.random() < 0.3:
            folder = os.path.join(folder, rnd.choice(EVENTS))
        for _ in range(rnd.randint(5, 60)):
            seed, pat, lo, hi = rnd.choices(kinds, weights)[0]
            name = pat.format(n=n, d=f"{year}-{rnd.randint(1, 12):02d}-{rnd.randint(1, 28):02d}")
            used += g.media(os.path.join(folder, name), seed, rnd.randint(lo, hi), ts(year, rnd))
            n += 1
            if rnd.random() < 0.05:   # edited copy next to the original, like phone galleries do
                used += g.media(os.path.join(folder, name.rsplit(".", 1)[0] + " (düzenlenmiş).jpg"), "photo-phone.jpg", rnd.randint(lo, hi), ts(year, rnd))
            if used >= budget:
                break
        if rnd.random() < 0.2:   # Windows and macOS leave these behind
            g.write(os.path.join(folder, "Thumbs.db"), size=rnd.randint(8 << 10, 300 << 10), head=b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1")
            g.write(os.path.join(folder, ".DS_Store"), size=6148, head=b"\x00\x00\x00\x01Bud1")
            g.write(os.path.join(folder, "desktop.ini"), data="[.ShellClassInfo]\r\nIconResource=C:\\Windows\\System32\\imageres.dll,-113\r\n".encode("utf-16"))


def videos(g, budget, years, big):
    rnd = g.rnd
    seeds = [("phone.mp4", "VID_{d}_{n}.mp4", 80 * MiB, 900 * MiB, 40), ("iphone.mov", "IMG_{n:04d}.MOV", 50 * MiB, 700 * MiB, 25),
             ("film.mkv", "{t}.mkv", 700 * MiB, 3 * GiB, 10), ("camcorder.avi", "Kaset {n}.avi", 300 * MiB, 2 * GiB, 5),
             ("web.webm", "ekran-kaydı-{n}.webm", 10 * MiB, 300 * MiB, 10), ("old.3gp", "Video{n:03d}.3gp", 1 * MiB, 20 * MiB, 10)]
    titles = ["Babamın 60. yaşı", "Nişan", "Ali'nin ilk adımları", "Belgesel - Göbeklitepe", "Konser 2019", "Mezuniyet"]
    used, n = 0, 1
    base = os.path.join(g.out, "Videolar")
    # every real seed once, untouched, so previews and players have a plain sample of each kind
    for s in ["phone.mp4", "iphone.mov", "film.mkv", "camcorder.avi", "web.webm", "old.3gp"]:
        data = g.seed(s)
        used += g.write(os.path.join(base, "Örnekler", s), data=data, mtime=ts(2024, rnd))
    for size, name in big:   # the files that cross the FAT32 4 GiB limit and buffer boundaries
        used += g.media(os.path.join(base, "Büyük", name), "film.mkv", size, ts(2023, rnd))
    while used < budget:
        seed, pat, lo, hi, _ = rnd.choices(seeds, [s[4] for s in seeds])[0]
        year = rnd.choice(years)
        size = min(rnd.randint(lo, hi), max(budget - used, len(g.seed(seed)) + 16))
        name = pat.format(n=n, d=f"{year}{rnd.randint(1, 12):02d}{rnd.randint(1, 28):02d}", t=rnd.choice(titles) + f" {n}")
        used += g.media(os.path.join(base, str(year), name), seed, size, ts(year, rnd))
        n += 1


def music(g, budget):
    rnd = g.rnd
    used = 0
    base = os.path.join(g.out, "Müzik")
    kinds = [("song.mp3", "mp3", 4 * MiB, 12 * MiB), ("song.flac", "flac", 20 * MiB, 50 * MiB), ("song.m4a", "m4a", 4 * MiB, 10 * MiB),
             ("apple.m4a", "m4a", 15 * MiB, 40 * MiB), ("voice.ogg", "ogg", 2 * MiB, 8 * MiB), ("song.wav", "wav", 30 * MiB, 60 * MiB)]
    while used < budget:
        for artist, albums in ARTISTS.items():
            for album in albums:
                seed, ext, lo, hi = rnd.choices(kinds, [50, 15, 15, 5, 5, 3])[0]
                folder = os.path.join(base, artist, album)
                for t in range(1, rnd.randint(6, 14)):
                    name = f"{t:02d} - {album} {t}.{ext}" if rnd.random() < 0.8 else f"{t:02d}. Şarkı #{t} (Canlı).{ext}"
                    if os.path.exists(os.path.join(folder, name)):
                        continue
                    used += g.media(os.path.join(folder, name), seed, rnd.randint(lo, hi), ts(rnd.randint(2005, 2022), rnd))
                if not os.path.exists(os.path.join(folder, "cover.jpg")):
                    used += g.media(os.path.join(folder, "cover.jpg"), "web.webp", 300 << 10, ts(2015, rnd))
                if used >= budget:
                    return
        base = os.path.join(g.out, "Müzik", f"Eski CD'ler {rnd.randint(1, 999)}")
    g.write(os.path.join(g.out, "Müzik", "Çalma listesi.m3u8"), data="#EXTM3U\n#EXTINF:-1,Barış Manço - Dönence\nBarış Manço/Dönence/01 - Dönence 1.mp3\n".encode())


def pdf(text):
    """A small but valid PDF with one page of text."""
    content = f"BT /F1 18 Tf 72 720 Td ({text}) Tj ET".encode("latin-1", "replace")
    objs = [b"<< /Type /Catalog /Pages 2 0 R >>", b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            b"<< /Length %d >>\nstream\n" % len(content) + content + b"\nendstream", b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"]
    out = io.BytesIO(); out.write(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n"); offs = []
    for i, o in enumerate(objs, 1):
        offs.append(out.tell()); out.write(b"%d 0 obj\n" % i + o + b"\nendobj\n")
    x = out.tell()
    out.write(b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1) + b"".join(b"%010d 00000 n \n" % o for o in offs))
    out.write(b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, x))
    return out.getvalue()


def office(kind, text):
    """Minimal valid .docx / .xlsx / .odt (all are zip files)."""
    b = io.BytesIO()
    with zipfile.ZipFile(b, "w", zipfile.ZIP_DEFLATED) as z:
        if kind == "odt":
            z.writestr(zipfile.ZipInfo("mimetype"), "application/vnd.oasis.opendocument.text")
            z.writestr("content.xml", f'<?xml version="1.0"?><office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"><office:body><office:text><text:p>{text}</text:p></office:text></office:body></office:document-content>')
            z.writestr("META-INF/manifest.xml", '<?xml version="1.0"?><manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0"><manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/></manifest:manifest>')
        elif kind == "docx":
            z.writestr("[Content_Types].xml", '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
            z.writestr("_rels/.rels", '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
            z.writestr("word/document.xml", f'<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>')
        else:
            z.writestr("[Content_Types].xml", '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>')
            z.writestr("_rels/.rels", '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>')
            z.writestr("xl/workbook.xml", '<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Bütçe" sheetId="1" r:id="rId1"/></sheets></workbook>')
            z.writestr("xl/_rels/workbook.xml.rels", '<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>')
            z.writestr("xl/worksheets/sheet1.xml", f'<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>{text}</t></is></c><c r="B1"><v>1250.75</v></c></row></sheetData></worksheet>')
    return b.getvalue()


def documents(g, budget):
    rnd = g.rnd
    base = os.path.join(g.out, "Belgeler")
    used = 0
    turkish = "Çağrı İşleri Güneş Özge Şükrü — ığüşöçİĞÜŞÖÇ € ₺ “tırnak” ‘tek’ … 你好 مرحبا Привет 🙂\n"
    while used < budget:
        year = rnd.randint(2008, 2026)
        folder = os.path.join(base, rnd.choice(["Faturalar", "Resmi", "İş", "Okul", "Sağlık", "Banka", "Tapu ve Kira"]), str(year))
        w = rnd.choice(WORDS)
        n = rnd.randint(1, 99999)
        k = rnd.randrange(12)
        if k == 0: used += g.write(os.path.join(folder, f"{w} {n}.pdf"), data=pdf(f"{w} {year} no {n}"), size=rnd.randint(30 << 10, 4 * MiB), mtime=ts(year, rnd))
        elif k == 1: used += g.write(os.path.join(folder, f"{w}-{n}.docx"), data=office("docx", f"{w} {turkish.strip()}"), mtime=ts(year, rnd))
        elif k == 2: used += g.write(os.path.join(folder, f"{w} {n}.xlsx"), data=office("xlsx", w), mtime=ts(year, rnd))
        elif k == 3: used += g.write(os.path.join(folder, f"{w}_{n}.odt"), data=office("odt", w), mtime=ts(year, rnd))
        elif k == 4: used += g.write(os.path.join(folder, f"not {n}.txt"), data=(turkish * rnd.randint(1, 400)).encode("utf-8"), mtime=ts(year, rnd))
        elif k == 5: used += g.write(os.path.join(folder, f"eski not {n}.txt"), data=(turkish.replace("\n", "\r\n") * 50).encode("utf-16"), mtime=ts(year, rnd))
        elif k == 6: used += g.write(os.path.join(folder, f"windows-1254 {n}.txt"), data=("Şekerci Ömer ığüşöç\r\n" * 80).encode("cp1254"), mtime=ts(year, rnd))
        elif k == 7: used += g.write(os.path.join(folder, f"{w} {n}.csv"), data=("tarih;tutar;açıklama\n" + "".join(f"2024-01-{d:02d};{rnd.randint(1, 9999)},50;{rnd.choice(WORDS)}\n" for d in range(1, 29)) * 20).encode(), mtime=ts(year, rnd))
        elif k == 8: used += g.write(os.path.join(folder, f"{w}.{n}.json"), data=json.dumps({"ad": "Özge", "liste": list(range(rnd.randint(1, 2000)))}, ensure_ascii=False).encode(), mtime=ts(year, rnd))
        elif k == 9: used += g.write(os.path.join(folder, f"{w} {n}.rtf"), data=rb"{\rtf1\ansi\ansicpg1254 {\fonttbl\f0 Arial;}\f0 \'de\'f0\'fd test\par}", mtime=ts(year, rnd))
        elif k == 10: used += g.write(os.path.join(folder, f"kişi {n}.vcf"), data="BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Şükrü Öztürk\r\nTEL:+90 555 000 00 00\r\nEND:VCARD\r\n".encode(), mtime=ts(year, rnd))
        else: used += g.write(os.path.join(folder, f"e-posta {n}.eml"), data=f"From: a@b.c\r\nSubject: =?UTF-8?B?w4dhxJ9yxLE=?=\r\n\r\n{turkish}".encode(), mtime=ts(year, rnd))
    g.write(os.path.join(base, "Notlar.md"), data=f"# Notlar\n\n- [ ] {turkish}".encode())
    g.write(os.path.join(base, "takvim.ics"), data=b"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nSUMMARY:Bayram\r\nDTSTART:20250330\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n")


def git(cwd, *args):
    env = dict(os.environ, GIT_AUTHOR_NAME="Test", GIT_AUTHOR_EMAIL="t@t", GIT_COMMITTER_NAME="Test", GIT_COMMITTER_EMAIL="t@t",
               GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_SYSTEM="/dev/null")
    subprocess.run(["git", *args], cwd=cwd, env=env, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def code(g, budget, small_files):
    """Projects with git history, a node_modules-like tree of tiny files, scripts with the exec bit."""
    rnd = g.rnd
    base = os.path.join(g.out, "Kod")
    used, p = 0, 0
    per_project = 3000
    while used < budget or small_files > 0:
        p += 1
        proj = os.path.join(base, f"proje-{p}")
        os.makedirs(proj)
        # git writes its own files; they are recorded after
        for i in range(rnd.randint(3, 12)):
            path = os.path.join(proj, "src", f"modül_{i}.py")
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "a", encoding="utf-8") as f:
                f.write(f"def f{i}():\n    return 'değer {i}'\n" * rnd.randint(1, 200))
            if i == 0:
                git(proj, "init", "-q", "-b", "main")
            git(proj, "add", "-A"); git(proj, "commit", "-q", "-m", f"değişiklik {i}")
        with open(os.path.join(proj, "run.sh"), "w") as f:
            f.write("#!/bin/sh\necho merhaba\n")
        os.chmod(os.path.join(proj, "run.sh"), 0o755)
        with open(os.path.join(proj, ".env"), "w") as f:
            f.write("SECRET=gizli\n")
        with open(os.path.join(proj, ".gitignore"), "w") as f:
            f.write("node_modules/\n")
        n = min(per_project, max(small_files, 0))
        for i in range(n):   # thousands of tiny files in deep folders, like node_modules
            pkg = f"paket-{i // 40}"
            d = os.path.join(proj, "node_modules", pkg, *(["lib"] * (i % 4)))
            used += g.write(os.path.join(d, "package.json" if i % 40 == 0 else f"index{i}.js"), data=(f"module.exports = {i};\n" * rnd.randint(1, 30)).encode())
            if i % 500 == 0:
                g.write(os.path.join(d, ".bin-stamp"), data=b"")  # empty hidden file
        small_files -= n
        used += g.write(os.path.join(proj, "build", "app.bin"), size=rnd.randint(1 * MiB, 40 * MiB), head=b"\x7fELF\x02\x01\x01", mode=0o755)
        # record what git and the shell wrote
        for root, dirs, files in os.walk(proj):
            if "node_modules" in root.split(os.sep) or os.path.join(proj, "build") == root:
                continue
            for f in files:
                path = os.path.join(root, f)
                with open(path, "rb") as fh:
                    data = fh.read()
                st = os.lstat(path)
                g.files += 1; g.bytes += len(data); used += len(data)
                g.rec(kind="file", path=g.rel(path), size=len(data), sha256=hashlib.sha256(data).hexdigest(), mtime=st.st_mtime_ns, mode=stat.S_IMODE(st.st_mode))
        if p > 10000:
            break


def archives(g, budget):
    rnd = g.rnd
    base = os.path.join(g.out, "Arşivler")
    os.makedirs(base, exist_ok=True)
    used, n = 0, 0
    while used < budget:
        n += 1
        inner = {f"klasör/dosya {i}.txt": (f"içerik {i} ğüşiöç\n" * rnd.randint(1, 5000)).encode() for i in range(rnd.randint(3, 40))}
        inner[f"foto {n}.jpg"] = g.seed("photo-phone.jpg")
        inner["rastgele.bin"] = rnd.randbytes(rnd.randint(1 * MiB, 60 * MiB))
        kind = n % 5
        b = io.BytesIO()
        if kind in (0, 1):
            with zipfile.ZipFile(b, "w", zipfile.ZIP_DEFLATED if kind == 0 else zipfile.ZIP_STORED) as z:
                for k, v in inner.items():
                    z.writestr(k, v)
            name = f"yedek {n}.zip" if kind == 0 else f"Fotoğraflar {n}.zip"
        else:
            mode = {2: "w:gz", 3: "w:xz", 4: "w"}[kind]
            if kind == 3:
                inner.pop("rastgele.bin")  # xz is slow; keep it small
            with tarfile.open(fileobj=b, mode=mode) as t:
                for k, v in inner.items():
                    ti = tarfile.TarInfo(k); ti.size = len(v); ti.mtime = 1600000000
                    t.addfile(ti, io.BytesIO(v))
            name = {2: f"arşiv {n}.tar.gz", 3: f"arşiv {n}.tar.xz", 4: f"arşiv {n}.tar"}[kind]
        data = b.getvalue()
        if kind == 4:   # one more format: the same tar compressed with zstd
            data = subprocess.run(["zstd", "-q", "-3", "-c"], input=data, check=True, capture_output=True).stdout
            name += ".zst"
        used += g.write(os.path.join(base, name), data=data, mtime=ts(rnd.randint(2010, 2026), rnd))
        if n % 7 == 0:   # formats this tool cannot make: right header, random body
            used += g.write(os.path.join(base, f"program {n}.7z"), head=b"7z\xbc\xaf\x27\x1c\x00\x04", size=rnd.randint(1 * MiB, 200 * MiB))
            used += g.write(os.path.join(base, f"oyun {n}.rar"), head=b"Rar!\x1a\x07\x01\x00", size=rnd.randint(1 * MiB, 200 * MiB))
    # a zip whose names are in the old Turkish DOS code page (no UTF-8 flag), as old Windows made them
    b = io.BytesIO()
    with zipfile.ZipFile(b, "w") as z:
        z.writestr("XXXXXX.txt", b"eski")   # ASCII placeholder of the same length, so no UTF-8 flag is set
    raw = b.getvalue().replace(b"XXXXXX.txt", "ŞĞÜÇÖİ.txt".encode("cp857"))
    g.write(os.path.join(base, "eski windows.zip"), data=raw)
    g.write(os.path.join(base, "bozuk.zip"), data=b"PK\x03\x04" + rnd.randbytes(5000))


def disk_images(g, budget):
    rnd = g.rnd
    base = os.path.join(g.out, "Disk İmajları")
    used, n = 0, 0
    os.makedirs(base, exist_ok=True)
    # a real FAT disk image with files inside
    img = os.path.join(base, "disket.img")
    with open(img, "wb") as f:
        f.truncate(32 * MiB)
    subprocess.run(["/usr/sbin/mkfs.vfat", "-n", "DISKET", img], check=True, stdout=subprocess.DEVNULL)
    with open(img, "rb") as f:
        data = f.read()
    os.remove(img)
    used += g.write(img, data=data)
    # a sparse image: mostly holes (copies must still come out byte-identical)
    sp = os.path.join(base, "seyrek-sanal-disk.img")
    with open(sp, "xb") as f:
        f.truncate(2 * GiB)
        f.seek(GiB); f.write(b"ORTA"); f.seek(2 * GiB - 4); f.write(b"SON!")
    h = hashlib.sha256()
    with open(sp, "rb") as f:
        while b := f.read(CHUNK):
            h.update(b)
    g.files += 1
    g.bytes += 2 * GiB
    g.rec(kind="file", path=g.rel(sp), size=2 * GiB, sha256=h.hexdigest(), mtime=os.lstat(sp).st_mtime_ns, mode=0o644, sparse=True)
    used += 2 * GiB
    heads = [("ubuntu-24.04-desktop-amd64.iso", b"\x00" * 0x8001 + b"CD001", 600 * MiB, 3 * GiB),
             ("Windows 7 SP1.iso", b"\x00" * 0x8001 + b"CD001", 500 * MiB, 2 * GiB), ("sanal makine.qcow2", b"QFI\xfb\x00\x00\x00\x03", 100 * MiB, 2 * GiB),
             ("eski pc.vhdx", b"vhdxfile", 100 * MiB, 2 * GiB), ("mac yedek.dmg", b"", 50 * MiB, 500 * MiB), ("vm.vmdk", b"KDMV\x01\x00\x00\x00", 50 * MiB, 1 * GiB)]
    while used < budget:
        n += 1
        name, head, lo, hi = rnd.choice(heads)
        used += g.write(os.path.join(base, f"{n} - {name}"), head=head, size=min(rnd.randint(lo, hi), max(budget - used, len(head) + 1)), mtime=ts(rnd.randint(2010, 2025), rnd))


def backups(g, budget, small_files):
    """A phone backup (thousands of small, oddly named files), a real SQLite database, mail."""
    rnd = g.rnd
    base = os.path.join(g.out, "Yedekler")
    used = 0
    db = os.path.join(base, "Telefon", "rehber.sqlite")
    os.makedirs(os.path.dirname(db))
    subprocess.run(["sqlite3", db], input="create table k(ad text, tel text);" + "".join(f"insert into k values('Kişi {i} Ğ', '+90{i:010d}');" for i in range(5000)),
                   text=True, check=True)
    with open(db, "rb") as f:
        data = f.read()
    os.remove(db)
    used += g.write(db, data=data)
    for i in range(small_files):
        h = hashlib.sha1(str(i).encode()).hexdigest()
        used += g.write(os.path.join(base, "Telefon", "WhatsApp", "Media", h[:2], h), data=rnd.randbytes(rnd.randint(0, 64 << 10)))
    used += g.write(os.path.join(base, "Posta", "Gelen Kutusu.mbox"), data=("From a@b Mon Jan 1 00:00:00 2024\nSubject: Merhaba\n\nŞükrü\n\n" * 2000).encode())
    while used < budget:
        used += g.write(os.path.join(base, "Eski Bilgisayar", f"C_yedek_{rnd.randint(1, 1 << 30)}.bak"), size=rnd.randint(10 * MiB, 500 * MiB))


def hostile(g):
    """Names and file kinds that break careless programs. Every item is listed in the manifest with its kind,
    so a copy can be checked item by item: copied exactly, or reported - never silently lost or changed."""
    rnd = g.rnd
    base = os.path.join(g.out, "Zorlu Adlar")
    os.makedirs(base)
    # names that Windows filesystems (exFAT, NTFS, FAT) cannot hold
    for name in ['iki:nokta.txt', 'soru?.txt', 'yıldız*.txt', '"tırnak".txt', 'küçük<büyük>.txt', 'boru|.txt', 'ters\\bölü.txt',
                 'sonda nokta.', 'sonda boşluk ', ' başta boşluk.txt', 'CON.txt', 'nul', 'aux.c', 'COM1']:
        g.write(os.path.join(base, "Windows'ta olmaz", name), data=f"{name}\n".encode())
    # names that differ only in letter case (one folder on Linux, a clash on exFAT/NTFS/FAT)
    for name in ["Rapor.txt", "rapor.txt", "RAPOR.TXT"]:
        g.write(os.path.join(base, "Büyük küçük harf", name), data=name.encode())
    g.write(os.path.join(base, "Büyük küçük harf", "Klasör", "a.txt"), data=b"1")
    g.write(os.path.join(base, "Büyük küçük harf", "klasör", "b.txt"), data=b"2")
    # Turkish İ/ı: upper-casing rules differ between programs
    for name in ["İSTANBUL.txt", "istanbul.txt", "ISPARTA.txt", "ısparta.txt"]:
        g.write(os.path.join(base, "Türkçe İ ı", name), data=name.encode())
    # the same visible name in two Unicode forms (macOS writes the second)
    g.write(os.path.join(base, "Unicode", unicodedata.normalize("NFC", "şeker ğ ü.txt")), data=b"NFC")
    g.write(os.path.join(base, "Unicode", unicodedata.normalize("NFD", "şeker ğ ü.txt")), data=b"NFD")
    for name in ["emoji 😀🎉👨‍👩‍👧.txt", "日本語のファイル.txt", "العربية.txt", "Ελληνικά.txt", "русский.txt", "a\u200bb sıfır genişlik.txt",
                 "satır\nsonu.txt", "sekme\tkarakteri.txt", "-rf", "--help", "%2F yüzde.txt", "#diyez&ve+artı=.txt", "...üç nokta", "..txt",
                 "~yedek~", "$dolar.txt", "'tek tırnak'.txt", "noktasız", ".gizli", ".gizli klasör/içinde.txt", "uzantı.TXT.exe",
                 "ÇOK.UZUN.UZANTI.tar.gz.part"]:
        g.write(os.path.join(base, "Unicode", name), data=name.encode("utf-8", "surrogateescape"))
    # bytes that are not valid UTF-8 (old Windows/Linux names in ISO-8859-9): Linux allows them
    bad = os.fsdecode(b"eski \xfeirket \xd0\xfd.txt")   # ş, Ğ, ı in ISO-8859-9
    g.write(os.path.join(base, "Geçersiz UTF-8", bad), data=b"latin5")
    # longest names: 255 bytes ASCII, 255 bytes of 2-byte letters, and a path near the Linux limit
    g.write(os.path.join(base, "Uzun", "a" * 251 + ".txt"), data=b"255")
    g.write(os.path.join(base, "Uzun", "ş" * 125 + ".txt"), data=b"254 bytes")   # 125*2+4
    deep = os.path.join(base, "Derin")
    for i in range(30):
        deep = os.path.join(deep, f"{i:02d}-" + "klasör" * 15)   # ~100 bytes each -> ~3200 bytes
    g.write(os.path.join(deep, "en derindeki dosya.txt"), data=b"derin")
    deep2 = os.path.join(base, "Derin 2", *(["k"] * 200))   # 200 levels
    g.write(os.path.join(deep2, "dip.txt"), data=b"200")
    # sizes around buffer and block boundaries
    for size in [0, 1, 511, 512, 4095, 4096, 4097, 65535, 65536, 65537, MiB - 1, MiB, MiB + 1, CHUNK - 1, CHUNK, CHUNK + 1, 80 * MiB + 3]:
        g.write(os.path.join(base, "Boyutlar", f"{size}.bin"), size=size)
    g.write(os.path.join(base, "Boyutlar", "sıfırlar.bin"), data=b"\x00" * (3 * MiB))
    # empty folders, nested
    for d in ["Boş", "Boş/içi de boş/en içi boş", "Boş klasör ", ".boş gizli"]:
        os.makedirs(os.path.join(base, "Boş klasörler", d), exist_ok=True)
    # dates FAT cannot store, the far past and the future
    for name, ns in [("1970.txt", 0), ("1979.txt", ts(1979, rnd)), ("1970 + 1 ns.txt", 1), ("1950 (1970 öncesi).txt", -ts(1990, rnd) // 3), ("2038 sonrası.txt", ts(2040, rnd)),
                     ("2107 sonrası.txt", ts(2110, rnd)), ("gelecek.txt", ts(2030, rnd))]:
        g.write(os.path.join(base, "Tarihler", name), data=name.encode(), mtime=ns)
    # permissions: read-only file, read-only folder, an unreadable file and folder
    g.write(os.path.join(base, "İzinler", "salt okunur.txt"), data=b"ro", mode=0o444)
    g.write(os.path.join(base, "İzinler", "çalıştırılabilir.sh"), data=b"#!/bin/sh\n", mode=0o755)
    g.write(os.path.join(base, "İzinler", "kilitli klasör", "içerideki.txt"), data=b"x")
    g.write(os.path.join(base, "İzinler", "okunamaz.txt"), data=b"secret", mode=0o000)
    g.write(os.path.join(base, "İzinler", "okunamaz klasör", "gizli.txt"), data=b"y")
    # links: copied as links never; reported as skipped
    lk = os.path.join(base, "Bağlantılar")
    g.write(os.path.join(lk, "hedef.txt"), data=b"hedef")
    for name, target in [("dosyaya bağlantı", "hedef.txt"), ("kırık bağlantı", "yok.txt"), ("sistem dosyası", "/etc/passwd"),
                         ("üst klasöre", ".."), ("kök", "/"), ("döngü", "döngü")]:
        os.symlink(target, os.path.join(lk, name))
        g.rec(kind="symlink", path=g.rel(os.path.join(lk, name)), target=target)
    os.makedirs(os.path.join(lk, "gerçek klasör"))
    g.write(os.path.join(lk, "gerçek klasör", "a.txt"), data=b"a")
    os.symlink("gerçek klasör", os.path.join(lk, "klasöre bağlantı"))
    g.rec(kind="symlink", path=g.rel(os.path.join(lk, "klasöre bağlantı")), target="gerçek klasör")
    os.link(os.path.join(lk, "hedef.txt"), os.path.join(lk, "sabit bağlantı.txt"))
    g.files += 1
    g.rec(kind="file", path=g.rel(os.path.join(lk, "sabit bağlantı.txt")), size=5, sha256=hashlib.sha256(b"hedef").hexdigest(),
          mtime=os.lstat(os.path.join(lk, "hedef.txt")).st_mtime_ns, mode=0o644, hardlink="hedef.txt")
    # special files: reading a named pipe blocks forever if a program does not check first
    os.makedirs(os.path.join(base, "Özel"))
    fifo = os.path.join(base, "Özel", "boru (fifo)")
    os.mkfifo(fifo)
    g.rec(kind="fifo", path=g.rel(fifo))
    # a file with an extended attribute (Linux-only metadata)
    xp = os.path.join(base, "Özel", "etiketli.txt")
    g.write(xp, data=b"xattr")
    try:
        os.setxattr(xp, "user.comment", "Önemli".encode())
    except OSError:
        pass
    # locking the folders last, so their contents could be written first
    os.chmod(os.path.join(base, "İzinler", "kilitli klasör"), 0o555)
    os.chmod(os.path.join(base, "İzinler", "okunamaz klasör"), 0o000)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", required=True)
    ap.add_argument("--size", default="50G")
    ap.add_argument("--seeds", required=True)
    ap.add_argument("--manifest", help="default: <out>.manifest.jsonl next to the data folder")
    ap.add_argument("--random-seed", type=int, default=20261107)
    ap.add_argument("--quiet", action="store_true")
    a = ap.parse_args()
    out = os.path.abspath(a.out)
    if os.path.exists(out) and os.listdir(out):
        sys.exit(f"{out} is not empty; refusing to write into it")
    manifest = a.manifest or out.rstrip("/") + ".manifest.jsonl"
    if os.path.commonpath([out, os.path.abspath(manifest)]) == out:
        sys.exit("the manifest must be outside the data folder")
    os.makedirs(out, exist_ok=True)
    total = parse_size(a.size)
    rnd = random.Random(a.random_seed)
    g = Gen(out, manifest, a.seeds, rnd, a.quiet)
    gb = total / GiB
    small = int(max(3000, gb * 400))          # ~800k tiny files for 2 TB
    years = list(range(2006, 2027))
    big = []
    if total >= 30 * GiB:
        big += [(4 * GiB - 1, "tam sınırda 4GiB-1.mkv"), (4 * GiB + 1, "sınırı aşan 4GiB+1.mkv")]
    if total >= 500 * GiB:
        big += [(25 * GiB, "Blu-ray yedeği 25GB.mkv"), (60 * GiB, "tüm aile videoları 60GB.mkv")]
    print(f"writing ~{gb:.0f} GiB to {out}", file=sys.stderr)
    hostile(g)
    rest = total - g.bytes - sum(s for s, _ in big)
    plan = [("documents", 0.03), ("code", 0.03), ("backups", 0.02), ("archives", 0.06), ("disk_images", 0.07),
            ("music", 0.07), ("photos", 0.22)]
    for name, share in plan:
        print(f"- {name}", file=sys.stderr)
        b = int(rest * share)
        if name == "documents": documents(g, b)
        elif name == "code": code(g, b, small // 2)
        elif name == "backups": backups(g, b, small // 2)
        elif name == "archives": archives(g, b)
        elif name == "disk_images": disk_images(g, b)
        elif name == "music": music(g, b)
        elif name == "photos": photos(g, b, years)
    print("- videos", file=sys.stderr)
    videos(g, max(total - g.bytes, 0), years, big)
    g.finish_dirs()
    g.man.close()
    took = time.time() - g.t0
    print(f"done: {g.files:,} files, {g.bytes / GiB:.1f} GiB in {took / 60:.1f} min; manifest {manifest}", file=sys.stderr)


if __name__ == "__main__":
    main()
