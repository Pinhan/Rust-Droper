# 08 — Sürüm ve değişiklik kaydı (Semantic Versioning)

> **KALICI KURAL (kullanıcı şartı, 2026-10-20):** projede yapılan **her** değişiklik için `changelog.json`'a (TR+EN) giriş + doğru türde sürüm artışı + `CHANGELOG.md` / `CHANGELOG.tr.md` üretimi **zorunludur**. Bu kural testlerle zorlanır (`ChangelogRulesTests`): giriş eklenmeden sürüm/markdown tutarsız kalırsa test kırılır.

## 1. Tek kaynak
- **`src/RustDroper.App/Resources/changelog.json`** — gömülü kaynak (`LogicalName=changelog.json`). Uygulamadaki **Güncelleme notları** sayfası (başlıkta ⓘ'nin solundaki geçmiş düğmesi) bunu okur; veri yalnız sayfa açıkken yüklenir, kapanınca bırakılır (tepside bellek maliyeti yok).
- **`CHANGELOG.md`** (İngilizce) ve **`CHANGELOG.tr.md`** (Türkçe) bu JSON'dan üretilir — elle düzenlenmez. Üretim: `powershell -File build\changelog.ps1` (kontrol: `-Check`; dosya güncel değilse çıkış kodu 1). Mantık Core'da (`ChangelogMarkdown`), komut CLI'da (`RustDroper.Cli changelog`).
- **Sürüm numarası** `Directory.Build.props` içindeki `<Version>`'dır (Hakkında, alt satır, derleme sürümü buradan gelir). **En üst changelog girişi == `<Version>`** (test).

## 2. Sürüm türleri (MAJOR.MINOR.PATCH)
| Tür | Ne zaman | Örnek artış | `type` alanı |
|---|---|---|---|
| **MAJOR** | Mimari değişiklik, büyük refaktör, **geriye uyumsuz** değişiklik (örn. ayar/veri biçimi uyumsuz değişir) | 1.4.2 → **2.0.0** | `major` |
| **MINOR** | Yeni özellik, yeni sayfa/modül, yeni kullanıcı fonksiyonu | 1.0.3 → **1.1.0** | `minor` |
| **PATCH** | Hata düzeltme, CSS/görünüm, performans, güvenlik, metin (çeviri dahil) ve küçük UX düzeltmeleri | 1.1.0 → **1.1.1** | `patch` |
| **BASELINE** | Yalnız ilk (referans) sürüm: **v1.0.0** — projenin temiz tek commit olarak yayınlandığı hal. Geçmiş geliştirme commit'leri tek tek sürüm yapılmaz. | — | `baseline` |

SemVer sıfırlama kuralı: MAJOR artınca MINOR ve PATCH **0** olur; MINOR artınca PATCH **0** olur. Karar zor olduğunda **daha büyük** olanı seç (kullanıcıya görünen yeni davranış = en az MINOR).

## 3. Her değişiklikte yapılacaklar (sıra)
1. Değişikliği türüne göre sınıflandır (tablo) → yeni sürümü hesapla (`Directory.Build.props` `<Version>`).
2. `changelog.json`'un **en üstüne** yeni giriş ekle (yeni → eski sıra):
   ```json
   {
     "version": "1.1.0",
     "date": "2026-11-12",
     "type": "minor",
     "tag":     { "tr": "Yeni sayfa", "en": "New page" },
     "summary": { "tr": "Bir iki cümlelik özet.", "en": "A sentence or two of summary." },
     "added":   [ { "tr": "…", "en": "…" } ],
     "changed": [ ],
     "fixed":   [ ],
     "removed": [ ]
   }
   ```
   - **Her metin hem `tr` hem `en`** (test: ikisi de dolu). Maddeler kısa, doğru, kullanıcı diliyle; yalnız gerçekten yapılanı yaz.
   - Bölümler: `added` (Eklenenler), `changed` (Değişenler), `fixed` (Düzeltilenler), `removed` (Kaldırılanlar). **Boş bölüm bırakılabilir; sayfada/markdown'da gösterilmez.**
   - `type` önceki girişe göre hesaplanan artışla **aynı** olmalı (test).
3. `powershell -File build\changelog.ps1` → `CHANGELOG.md` + `CHANGELOG.tr.md` yeniden üretilir.
4. `dotnet build` (0 uyarı) + `dotnet test` (hepsi yeşil) → değişikliği ve sürümü PR açıklamasına yaz.
5. Yayın: `vX.Y.Z` etiketi gönderilince `.github/workflows/release.yml` portable exe + zip + SHA256 üretir; exe'nin Hakkında sayfasındaki sürüm == en üst changelog girişi == etiket.

Sürüm gerektirmeyen işler: yalnız `docs/`, test ve geliştirici araçları (kullanıcıya/ürüne yansımayan). Şüphede giriş ekle (PATCH).

## 4. Testler (kurallar kod olarak) — `tests/RustDroper.App.Tests/ReleaseNotesTests.cs`, `tests/RustDroper.Core.Tests/ChangelogTests.cs`
JSON geçerli ve atlanan giriş yok · sürümler SemVer, yeni→eski sıralı, yinelenen yok · her giriş için etiket/özet/madde iki dilde dolu · ardışık girişlerde artışın türü `type` ile uyumlu, yalnız en eski giriş `baseline` · en üst sürüm == derleme sürümü == `Directory.Build.props` · `CHANGELOG.md`/`CHANGELOG.tr.md` JSON ile birebir senkron · sayfa: boş bölüm gösterilmez, dil değişince metinler anında yenilenir, kapanınca veri bırakılır · bozuk JSON/giriş uygulamayı çökertmez (boş sayfa).

## 5. Sayfa tasarımı (özet)
Başlıkta ⓘ'nin solunda ikonlu **Güncelleme notları / Release notes** düğmesi (tooltip iki dilli; sayfa açıkken geri oku). Sayfa: üstte *Semantic Versioning* açıklama kartı + MAJOR/MINOR/PATCH üç mini kart; altında yeni → eski sürüm kartları (solda dikey zaman çizgisi + nokta; büyük `vX.Y.Z`, renkli tür rozeti, etiket çipi, yerelleştirilmiş tarih, sağda `önceki → bu` çipi [baseline'da yok], *Özet* kutusu, ikonlu renkli bölümler: Eklenenler yeşil · Değişenler mavi · Düzeltilenler sarı · Kaldırılanlar kırmızı). Hakkında/Sağlık/Ayarlar ile karşılıklı dışlayıcı; platform ekranı değişince kapanır. Görüntüler: `docs\ekran-goruntuleri\16-gercek-guncelleme-notlari-{tr,en}.png` (gerçek veri), `29-guncelleme-notlari-ornek-{tr,en}.png` (çok sürümlü ÖRNEK veriyle yerleşim denemesi; gerçek changelog değildir).
