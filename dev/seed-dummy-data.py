#!/usr/bin/env python3
"""Creates a small, realistic-looking folder tree for development (about 60 MB).

Usage: python3 dev/seed-dummy-data.py <target-folder>
The target must be empty or not exist. Nothing outside it is touched.
"""
import os, random, sys, datetime

TREE = {
    "Belgeler": {
        "Okul": ["Bitirme Projesi Raporu.docx", "Sunum.pptx", "Notlar.txt"],
        "Faturalar/2025": [f"Fatura {m:02d}-2025.pdf" for m in range(1, 13)],
        "Faturalar/2026": [f"Fatura {m:02d}-2026.pdf" for m in range(1, 10)],
        "": ["Özgeçmiş.pdf", "Kimlik fotokopisi.jpg", "Şifreler DEĞİL.txt"],
    },
    "Projeler": {
        "CoffeeCraft/src": ["index.ts", "app.module.ts", "README.md"],
        "Arduino Çalışmaları": [f"deneme{i}.ino" for i in range(1, 21)],
        "Çok/Derin/Bir/Klasör/Yapısı/Örnek": ["son.txt"],
    },
    "Müzik": {"Türkçe": [f"Şarkı {i}.mp3" for i in range(1, 16)]},
    "Videolar": {"Tatil 2025": ["Plaj.mp4", "Akşam yemeği.mp4"], "": ["Uzun video.mkv"]},
    "İndirilenler": {"": ["kurulum.exe", "arşiv.zip", "çok uzun bir dosya adı " * 6 + ".txt"]},
    "Binlerce Dosya": {"": [f"kayıt_{i:04d}.log" for i in range(1, 1201)]},
    "Boş Klasör": {},
}

SIZES = {  # rough sizes in KB by extension
    "pdf": (40, 900), "docx": (20, 400), "pptx": (300, 3000), "jpg": (200, 2500),
    "mp3": (1500, 4000), "mp4": (3000, 8000), "mkv": (6000, 9000), "zip": (500, 4000),
    "exe": (800, 3000),
}

def write(path, rng):
    ext = path.rsplit(".", 1)[-1].lower()
    if ext in SIZES:
        lo, hi = SIZES[ext]
        with open(path, "wb") as f:
            f.write(rng.randbytes(rng.randint(lo, hi) * 1024))
    else:
        with open(path, "w", encoding="utf-8") as f:
            f.write(f"DiskHaven sahte dosya: {os.path.basename(path)}\nçğıöşü ÇĞİÖŞÜ\n" * rng.randint(1, 40))
    # spread modification times over the last two years
    t = (datetime.datetime.now() - datetime.timedelta(days=rng.randint(0, 730), minutes=rng.randint(0, 1440))).timestamp()
    os.utime(path, (t, t))

def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    target = os.path.abspath(sys.argv[1])
    if os.path.exists(target) and os.listdir(target):
        sys.exit(f"Refusing: {target} is not empty.")
    rng = random.Random(42)  # deterministic: same tree every time
    count = 0
    for top, sub in TREE.items():
        os.makedirs(os.path.join(target, top), exist_ok=True)
        for folder, files in sub.items():
            d = os.path.join(target, top, folder)
            os.makedirs(d, exist_ok=True)
            for name in files:
                write(os.path.join(d, name), rng)
                count += 1
    print(f"Created {count} files in {target}")

if __name__ == "__main__":
    main()
