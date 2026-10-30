# DiskHaven

**Dosyalarınızı normal klasörler olarak saklayan, kendi sunucunuzda çalışan kişisel bulut sürücü.**

[English](README.md) · Lisans: [AGPL-3.0](LICENSE) · Durum: erken geliştirme

DiskHaven, evdeki bir sunucuyu ve sıradan diskleri size özel bir "Google Drive"a
dönüştürür: Windows Gezgini'ne benzeyen bir web arayüzü, kesilince kaldığı yerden
devam eden ve doğrulanan yüklemeler, harici USB yedek disklerine tek tuşla eşitleme.

## İlkeler

1. **Dosyalarınız uygulamaya hapsolmaz.** Her şey NTFS / exFAT disklerde normal
   klasörlerde durur. DiskHaven bozulsa ya da kaldırılsa bile diski herhangi bir
   Windows, Mac veya Linux bilgisayara takınca dosyalarınız oradadır.
2. **Sessiz hata yok.** Her aktarım SHA-256 ile uçtan uca doğrulanır. Dosya ya
   eksiksiz kaydedilir ya da hata raporlanıp yeniden denenir.
3. **Elinizde tutabildiğiniz yedek.** Ana diskteki klasörler harici USB disklere
   atanır. Diski takın (ya da takılı ve uykuda bırakın), *Eşitle*'ye basın,
   doğrulanmış raporu görün.
4. **Denenmiş yapı taşları.** ASP.NET Core, PostgreSQL, tus protokolü, ffmpeg.

Fotoğraflar bilerek kapsam dışıdır; DiskHaven [Immich](https://immich.app) ile
yan yana çalışmak için tasarlanmıştır.

Geliştirme adımları ve ayrıntılar için [README.md](README.md),
[docs/architecture.md](docs/architecture.md) ve [docs/roadmap.md](docs/roadmap.md).

## Eklentiler

**Eklentiler** (yönetim panelindeki mağazadan kurulur, veri kaybı olmadan kaldırılır; kurulu olmayan eklentinin kodu tarayıcıya hiç inmez):
ZIP arşivleri · Paylaşım bağlantıları (parola, dosya toplama kutusu) · Diskler ve yedekleme · Sunucu göstergesi · Önceki sürümler · Hızlı erişim (favoriler, son kullanılanlar) · Etkinlik günlüğü ·
**Gizli klasör**: parolayla açılan, şifreli, ayrı bir klasör ağacı; menüde dikkat çekmeyen bir adla görünür; kurtarma anahtarı, yem parolası, yedek disklere şifreli kopya ve çevrimdışı kurtarma aracı ([biçim](docs/hideaway.md)).

