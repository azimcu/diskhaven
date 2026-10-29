#!/usr/bin/env python3
"""Builds the sample files for the public demo: real PDFs, PNG pictures and WAV audio that open in
the preview, plus text, code and a folder with many files. About 15 MB, same result every run.

Usage: python3 seed-demo.py <empty-target-folder>
Needs Playwright with Chromium (renders the PDFs and pictures). Nothing outside the target is touched.
"""
import math, os, random, struct, sys, wave, datetime

from playwright.sync_api import sync_playwright

DOCS = {
    "Belgeler/Proje teklifi.pdf": ("Proje teklifi", ["Amaç", "Kapsam", "Takvim", "Bütçe"]),
    "Belgeler/Toplantı notları - Ekim.pdf": ("Toplantı notları", ["Katılımcılar", "Kararlar", "Yapılacaklar"]),
    "Belgeler/Faturalar/2026/Fatura 09-2026.pdf": ("Fatura", ["Hizmet", "Tutar", "Ödeme"]),
    "Belgeler/Faturalar/2026/Fatura 10-2026.pdf": ("Fatura", ["Hizmet", "Tutar", "Ödeme"]),
    "Documents/Quarterly report.pdf": ("Quarterly report", ["Summary", "Numbers", "Next steps"]),
}
PICTURES = [f"Fotoğraflar/2026 Kapadokya/IMG_{1040 + i}.png" for i in range(6)] + \
           [f"Fotoğraflar/2025 Ege/IMG_{2210 + i}.png" for i in range(4)]
TEXT = {
    "BENİ OKU.md": "# DiskHaven demosu\n\nBu klasördeki her şey örnektir. Dosya yükle, taşı, sil, ara; çöp kutusunu dene.\n"
                   "Her saat başı her şey ilk haline döner.\n\nKaynak kod: https://github.com/azimcu/diskhaven\n",
    "Belgeler/Okuma listesi.md": "# Okuma listesi\n\n- Designing Data-Intensive Applications\n- Clean Architecture\n- The Pragmatic Programmer\n",
    "Belgeler/Notlar.txt": "Alışveriş: süt, ekmek, kahve\nPazartesi: sunucu yedeğini kontrol et\nçğıöşü ÇĞİÖŞÜ\n",
    "Projeler/hello-dotnet/Program.cs": 'var app = WebApplication.CreateBuilder(args).Build();\napp.MapGet("/", () => "Merhaba DiskHaven");\napp.Run();\n',
    "Projeler/hello-dotnet/README.md": "# hello-dotnet\n\n`dotnet run` ile çalışır.\n",
    "Projeler/Çok/Derin/Bir/Klasör/Yapısı/son.txt": "En derindeki dosya.\n",
}


def doc_html(title, sections, rng):
    body = "".join(
        f"<h2>{s}</h2><p>{' '.join(rng.choice(WORDS) for _ in range(rng.randint(40, 90)))}.</p>" for s in sections)
    return f"""<html><head><meta charset="utf-8"><style>
      body{{font-family:'DejaVu Sans',sans-serif;margin:48px;color:#222}} h1{{color:#7a1f2b;border-bottom:2px solid #7a1f2b}}
      h2{{font-size:16px;margin-top:28px}} p{{line-height:1.6;font-size:12px}}</style></head>
      <body><h1>{title}</h1><p><i>DiskHaven demo belgesi · {datetime.date(2026, 10, 1)}</i></p>{body}</body></html>"""


def picture_svg(rng):
    h1, h2 = rng.randint(0, 360), rng.randint(0, 360)
    hills = "".join(
        f'<path d="M0 {700 + i * 60} Q 400 {560 + i * 70 + rng.randint(-80, 80)} 800 {680 + i * 50} T 1600 {640 + i * 60} V1000 H0Z" '
        f'fill="hsl({h2},{30 + i * 10}%,{40 - i * 8}%)"/>' for i in range(4))
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="1600" height="1000">
      <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="hsl({h1},60%,70%)"/>
      <stop offset="1" stop-color="hsl({(h1 + 40) % 360},70%,88%)"/></linearGradient></defs>
      <rect width="1600" height="1000" fill="url(#g)"/><circle cx="{rng.randint(200, 1400)}" cy="{rng.randint(150, 400)}" r="90" fill="#fff6d5"/>
      {hills}</svg>"""


def tone(path, notes):
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(22050)
        for f in notes:
            w.writeframes(b"".join(struct.pack("<h", int(9000 * math.sin(2 * math.pi * f * t / 22050) * (1 - t / 6615)))
                                   for t in range(6615)))


WORDS = ("veri yedek disk klasör dosya paylaşım kullanıcı güvenlik sunucu ağ plan takvim bütçe karar toplantı "
         "proje test sürüm kurulum arşiv fotoğraf belge rapor özet hedef").split()


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    target = os.path.abspath(sys.argv[1])
    if os.path.exists(target) and os.listdir(target):
        sys.exit(f"Refusing: {target} is not empty.")
    rng = random.Random(7)
    p = lambda rel: (os.makedirs(os.path.dirname(os.path.join(target, rel)), exist_ok=True), os.path.join(target, rel))[1]

    with sync_playwright() as pw:
        b = pw.chromium.launch()
        page = b.new_page()
        for rel, (title, sections) in DOCS.items():
            page.set_content(doc_html(title, sections, rng)); page.pdf(path=p(rel), format="A4")
        page.set_viewport_size({"width": 1600, "height": 1000})
        for rel in PICTURES:
            page.set_content(f"<body style='margin:0'>{picture_svg(rng)}</body>"); page.screenshot(path=p(rel))
        b.close()

    for rel, text in TEXT.items():
        with open(p(rel), "w", encoding="utf-8") as f:
            f.write(text)
    tone(p("Müzik/Do majör gam.wav"), [262, 294, 330, 349, 392, 440, 494, 523])
    tone(p("Müzik/Kapı zili.wav"), [659, 523])
    for i in range(1, 401):
        with open(p(f"Kayıtlar/kayıt_{i:04d}.log"), "w") as f:
            f.write(f"{i} OK\n" * rng.randint(1, 30))
    os.makedirs(os.path.join(target, "Boş klasör"), exist_ok=True)

    now = datetime.datetime.now()
    for d, _, files in os.walk(target):
        for name in files:
            t = (now - datetime.timedelta(days=rng.randint(0, 400), minutes=rng.randint(0, 1440))).timestamp()
            os.utime(os.path.join(d, name), (t, t))


if __name__ == "__main__":
    main()
