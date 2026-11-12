#!/usr/bin/env python3
"""
Checks a copy of the test data against the manifest written by make-testdata.py, item by item.

    verify-testdata.py --manifest X.manifest.jsonl --root /path/of/copy [--prefix "Fotoğraflar/2019"]
                       [--reported errors.json] [--json result.json]

--root    the folder that corresponds to the data folder (or to --prefix, when only part was copied)
--prefix  only the manifest items under this path are expected (a partial copy)
--reported a JSON list of paths (relative to the data folder) that the copying program said it could not
          copy or skipped; such an item missing from the copy is fine, anything else missing is not

Names are compared exactly as the directory lists them (never opened by name, so a case-insensitive
target cannot hide a clash). A manifest file that is not found by name but whose exact content sits in
the same folder under another name (e.g. "rapor (2).txt" after a case clash) counts as "renamed".

Exit status 0 only when no file has wrong content and nothing is missing without being reported.
"""
import argparse, hashlib, json, os, sys
from collections import defaultdict

INTERNAL = {".diskhaven", ".homevault"}


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while b := f.read(8 << 20):
            h.update(b)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--root", required=True)
    ap.add_argument("--prefix", default="")
    ap.add_argument("--reported")
    ap.add_argument("--json")
    ap.add_argument("--renamed", help='JSON list of "path -> new name" (relative to the data folder) the copying program reported')
    ap.add_argument("--no-content", action="store_true", help="sizes only (fast)")
    a = ap.parse_args()
    prefix = a.prefix.strip("/")
    reported = set()
    if a.reported:
        with open(a.reported, encoding="utf-8") as f:
            reported = {p.strip("/") for p in json.load(f)}

    def shown(p):
        # how a program that cannot hold invalid UTF-8 names (like .NET) writes the path: U+FFFD per bad byte
        return os.fsencode(p).decode("utf-8", "replace")

    reported = {shown(p) for p in reported}

    def is_reported(path):
        # an item counts as reported when it, or a folder above it, was reported
        parts = shown(path).split("/")
        return any("/".join(parts[:i]) in reported for i in range(1, len(parts) + 1))

    renames = []   # (source rel path below prefix, new last name), longest first
    if a.renamed:
        with open(a.renamed, encoding="utf-8") as f:
            for line in json.load(f):
                src, new = line.split(" -> ", 1)
                new = new.split(" (", 1)[0] if new.endswith(")") and " (this disk" in new else new
                src = src.strip("/")
                if prefix and not (src == prefix or src.startswith(prefix + "/")):
                    continue
                renames.append((src[len(prefix):].lstrip("/") if prefix else src, new))
        renames.sort(key=lambda r: -len(r[0]))

    def renamed_path(rel):
        for src, new in renames:
            if rel == src or rel.startswith(src + "/"):
                return os.path.join(os.path.dirname(src), new) + rel[len(src):]
        return rel

    expected = []
    with open(a.manifest, encoding="utf-8") as f:
        for line in f:
            it = json.loads(line)
            p = it["path"]
            if prefix and p != prefix and not p.startswith(prefix + "/"):
                continue
            it["rel"] = p[len(prefix):].lstrip("/") if prefix else p
            if it["rel"]:
                new = renamed_path(it["rel"])
                if new != it["rel"]:
                    it["rel"] = new
                    it["was_renamed"] = True
                expected.append(it)

    # what is really there, by the exact names the directory lists
    present_files, present_dirs, present_other = {}, set(), {}
    walk_errors = []
    for root, dirs, files in os.walk(a.root, onerror=lambda e: walk_errors.append(os.path.relpath(e.filename, a.root))):
        relroot = os.path.relpath(root, a.root)
        relroot = "" if relroot == "." else relroot
        if relroot == "":
            dirs[:] = [d for d in dirs if d not in INTERNAL]
        for d in list(dirs):
            p = os.path.join(root, d)
            r = os.path.join(relroot, d)
            if os.path.islink(p):
                present_other[r] = "symlink"
                dirs.remove(d)
            else:
                present_dirs.add(r)
        for n in files:
            p = os.path.join(root, n)
            r = os.path.join(relroot, n)
            st = os.lstat(p)
            if os.path.islink(p):
                present_other[r] = "symlink"
            elif not os.path.isfile(p):
                present_other[r] = "special"
            else:
                present_files[r] = st

    res = defaultdict(list)
    res["unreadable_dir"] = walk_errors
    claimed = set()
    by_dir = defaultdict(list)
    for r in present_files:
        by_dir[os.path.dirname(r)].append(r)
    hash_cache = {}

    def content(r):
        if r not in hash_cache:
            try:
                hash_cache[r] = sha(os.path.join(a.root, r))
            except PermissionError:
                hash_cache[r] = "unreadable"
                res["unreadable"].append(r)
        return hash_cache[r]

    for it in expected:
        r, kind = it["rel"], it["kind"]
        if kind == "dir":
            (res["dir_ok"] if r in present_dirs else res["dir_missing_reported" if is_reported(it["path"]) else "dir_missing"]).append(r)
            continue
        if kind in ("symlink", "fifo"):
            if r in present_files or r in present_other:
                res[f"{kind}_present"].append(r)   # links/pipes must not be copied as data
            else:
                res[f"{kind}_not_copied"].append(r)
            continue
        st = present_files.get(r)
        if st is not None:
            claimed.add(r)
            if st.st_size != it["size"]:
                res["WRONG_SIZE"].append(f"{r}: {st.st_size} != {it['size']}")
            elif not a.no_content and content(r) == "unreadable":
                pass   # listed under "unreadable"
            elif not a.no_content and content(r) != it["sha256"]:
                res["WRONG_CONTENT"].append(r)
            else:
                res["file_ok"].append(r)
                if abs(st.st_mtime_ns - it["mtime"]) > 2_000_000_000:
                    res["mtime_differs"].append(r)
            continue
        # renamed in the same folder (name clash on the target)?
        twin = None
        for cand in by_dir.get(os.path.dirname(r), []):
            if cand in claimed or present_files[cand].st_size != it["size"]:
                continue
            if a.no_content or content(cand) == it["sha256"]:
                twin = cand
                break
        if twin:
            claimed.add(twin)
            res["file_renamed"].append(f"{r} -> {twin}")
        elif is_reported(it["path"]):
            res["file_missing_reported"].append(r)
        else:
            res["FILE_MISSING_UNREPORTED"].append(r)

    res["extra"] = [r for r in present_files if r not in claimed]
    summary = {k: len(v) for k, v in sorted(res.items())}
    ok = not res["WRONG_SIZE"] and not res["WRONG_CONTENT"] and not res["FILE_MISSING_UNREPORTED"] and not res["dir_missing"]
    print(json.dumps(summary, ensure_ascii=False))
    for k in ("WRONG_SIZE", "WRONG_CONTENT", "FILE_MISSING_UNREPORTED", "dir_missing", "symlink_present", "fifo_present", "extra"):
        for x in res[k][:20]:
            print(f"  {k}: {x!r}")
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump({"ok": ok, "summary": summary, "items": res}, f, ensure_ascii=True, indent=1)
    print("RESULT:", "OK" if ok else "FAIL")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
