# Geliştirme rehberi — bir şeyi değiştirmeden önce oku

> English version: [DEVELOPMENT.md](DEVELOPMENT.md). Yapay zekâ asistanları için kısa kurallar: [../AGENTS.md](../AGENTS.md).

Rust Droper, Windows sistem tepsisinde çalışan (C# / .NET 10 / WPF) bir programdır; **Twitch ve Kick**'teki **Rust** drop'larını toplar.
Bu dosya ilk geliştiricinin devir notudur: neyin doğrulandığı, neyin **doğrulanmadığı** ve koddan kolay anlaşılmayan tuzaklar burada.

## 1. Proje durumu

- **v1.0.1 bitmiş, test edilmiş, çalışan bir temeldir** (derleme: 0 uyarı; ~535 Core + ~320 App testi yeşil; CI yeşil). Geliştirici geliştirmeyi durdurdu. Kodu kararlı say; küçük ve testli değişiklikler yap.
- Platformlar haber vermeden değişir (Twitch GraphQL hash'leri, Kick uç noktaları, Facepunch sayfa düzeni). **İleride çıkacak hataların çoğu "kod çürüdü" değil "platform değişti" olacaktır.** Bkz. §5.
- Kapsam bilinçlidir: yalnız Rust, bir Twitch + bir Kick hesabı, yalnız Windows. Çoklu hesap ve diğer oyunlar bilerek reddedildi (bkz. [06-arastirma-eszamanli-izleme-ve-coklu-hesap.md](06-arastirma-eszamanli-izleme-ve-coklu-hesap.md)).

## 2. Derleme, test, çalıştırma

```powershell
dotnet build RustDroper.slnx -c Release      # 0 uyarı olmalı (uyarılar hata sayılır)
dotnet test  RustDroper.slnx -c Release      # masaüstü/WebView2 olmayan makinede RUSTDROPER_SKIP_WEBVIEW2=1
powershell -File build\publish-portable.ps1  # tek dosya, self-contained exe (~127 MB), publish\ altına
powershell -File build\changelog.ps1         # CHANGELOG.md / CHANGELOG.tr.md üret (bkz. §4)
```

- SDK: .NET 10. Canlı testler isteğe bağlıdır (`RUSTDROPER_LIVE=1`); normal testler gerçek hesaba/ağa dokunmaz.
- Çalışan `RustDroper.exe` derleme çıktısını kilitler: `dotnet build` öncesi kapat.
- Geliştirme veri klasörü: `%LOCALAPPDATA%\RustDroper` (gizli). `RUSTDROPER_DATA=<klasör>` ile değişir — **denerken daima geçici klasör kullan**, böylece girişli gerçek profile dokunmazsın.
- Diğer ortam değişkenleri: `RUSTDROPER_RENDER=hardware`, `RUSTDROPER_MEMORY_LOG_SECONDS`, `RUSTDROPER_PLAYER_MODE=visible` (gizli Kick oynatıcısını göster), `RUSTDROPER_PLAYER_ARGS`, `RUSTDROPER_PLAYER_GPU=1`, `RUSTDROPER_SOAK_SCALE=N`, `RUSTDROPER_SKIP_WEBVIEW2=1`, `RUSTDROPER_SKIP_STARTUP`.
- Arayüzsüz platform yoklaması için CLI: `dotnet run --project src/RustDroper.Cli -- <komut> <twitch|kick> ...`; komutlar: `login`, `whoami`, `logout`, `campaigns`, `scan`, `watch`, `run`, `claim`, `progress` (Kick), ayrıca `changelog [--check]` (örn. `campaigns twitch rust --probe 100`). Kullanım satırları `src/RustDroper.Cli/Program.cs` içindedir. Aynı veri klasöründe uygulamayı ve CLI'yi aynı anda çalıştırma (belirteç yenileme yarışı).

## 3. Kod haritası

| Alan | Yer | Not |
|---|---|---|
| Alan modeli | `src/RustDroper.Core/Domain` | platformdan bağımsız kampanya/drop/envanter |
| Twitch | `Core/Platforms/Twitch` | GraphQL (hash'li kalıcı sorgular), PubSub, cihaz-kodu girişi, HLS izleyici, claim yedeği |
| Kick | `Core/Platforms/Kick`, `Core/Watching/KickWebViewWatcher.cs`, `App/Player` | telefondan QR girişi, gizli WebView2 gerçek oynatıcı |
| Motor | `Core/Orchestration` | `DropEngine` (watchdog, geçiş, takılma), `DropPlanner` (ne izlenir), `DropperService` (hesap + motor yaşam döngüsü), `EngineStateStore` |
| Rust filtresi | `Core/Platforms/RustCampaignFilter.cs` | Rust kampanyası sayılma kuralı (ayar `RustRewardsOnly`) |
| Drop görseli / etkinlik bilgisi | `Core/Media` | twitch.facepunch.com / kick.facepunch.com kazıma (ikincil kaynak, toleranslı ayrıştırıcı) |
| Yollar / taşıma | `Core/Infrastructure` | `AppPaths`, `LegacyDataMigrator`, atomik JSON depoları |
| Yerelleştirme | `Core/Localization/Loc.cs`, `App/Localization` | kod içi tr/en tabloları, uydu derleme yok |
| Arayüz | `src/RustDroper.App` | WPF, `ViewModels`, `Views`, `Themes`; tepsi simgesi kendi Win32 sarmalayıcımız |
| Çökme koruması | `App/Infrastructure/KeepAlive.cs` | zamanlanmış görev `RustDroperKeepAlive`, `run-state.json` |
| Değişiklik kaydı | `App/Resources/changelog.json` (tek kaynak), `Core/Changelog` | |

Tasarım kararları ve ölçümler: [02-mimari.md](02-mimari.md). Platform davranışı: [PLATFORM-NOTES.tr.md](PLATFORM-NOTES.tr.md). Kick riski: [04-kick-risk-arastirmasi.md](04-kick-risk-arastirmasi.md). Arayüz: [05-arayuz-tasarimi.md](05-arayuz-tasarimi.md).

## 4. Uyman gereken kurallar (birçoğunu testler zorlar)

1. **İki dil.** Kullanıcıya görünen her metin **Türkçe + İngilizce birlikte** eklenir/değiştirilir/silinir (`Loc` tabloları). Testler iki dilin varlığını, yer tutucu sayısı eşitliğini ve İngilizce metinde Türkçe harf olmamasını denetler. Belgeler: `USAGE.md` + `USAGE.tr.md`, `README.md` + `README.tr.md`, `CHANGELOG.md` + `CHANGELOG.tr.md`.
2. **Kullanıcıya yansıyan her değişiklik için sürüm + değişiklik kaydı** ([08-surum-ve-degisiklik-kaydi.md](08-surum-ve-degisiklik-kaydi.md)): `changelog.json`'a TR+EN giriş ekle, `Directory.Build.props` içindeki `<Version>`'ı artır (MAJOR = mimari / geriye uyumsuz, MINOR = yeni özellik/sayfa/modül, PATCH = hata, CSS, performans, güvenlik, metin, küçük UX), `build\changelog.ps1` çalıştır. Unutursan `ChangelogRulesTests` başarısız olur.
3. **Sırlar:** token/parola/çerezi asla günlüğe yazma, ekrana basma, commit'leme. Belirteçler DPAPI ile şifreli `secrets.dat` içindedir. Uygulama hiçbir parola görmez (Twitch cihaz-kodu girişi, Kick telefon QR'ı, Kick oynatıcı girişi için görünür WebView2 penceresi). Testler gerçek hesap kullanmaz.
4. **Uyarılar hatadır.** Yeşil olsun diye uyarı bastırma, test atlama/silme yapma.
5. **Motorda aynı anda iki şeyi değiştirme:** motor/izleme kodunun zamana bağlı testleri var (sahte saat, soak). Tek şey değiştir, tüm takımı çalıştır.

## 5. Bilinen tuzaklar (koddan kolay anlaşılmaz)

**Twitch**
- Keşif **kanal tabanlıdır**: `DirectoryPage_Game` (Drops açık Rust kanalları) → kanal başına `AvailableDrops`. Dashboard/details kampanya sorguları bu istemci profilinde (`SMARTBOX_TV`; diğer profiller engelli ya da 401) null döner.
- "Drops Enabled" kanalların çoğunda Rust kampanyası **yoktur** (site genelindeki abonelik kampanyası çok kanalı etiketler). Gerçek etkinlik kampanyası yalnız katılan kanallarda görünür (100'de ≈8). Bu yüzden keşif 100 kanala kadar tarar ve 8 uygun kanalda durur (v1.0.1 düzeltmesi; ilk ~6 kanalda durmak etkinlik başlayınca "Boşta" sorununa yol açtı). Yeniden ölçmeden tarama derinliğini azaltma: `dotnet run --project src/RustDroper.Cli -- campaigns twitch rust --probe 100`.
- Yalnız abonelik kampanyaları (`requiredSubs > 0`) asla izlenmez. "Genel" ve "yayıncı" kapsamı, drop taşıyan kanallar arasında belirlenir.
- GraphQL **kalıcı sorgu hash'leri** kullanır (`TwitchGqlTypes.cs` içindeki `GqlOperations`, her işlem için `DefaultHash`). Twitch birini değiştirirse `PersistedQueryNotFound` benzeri hatalar alırsın: yeni hash'i yakalayıp düzelt. Otomatik hash keşfi planlandı ama **yapılmadı**.
- Claim: Twitch-yerel ödüllerde sayaç eşiğe varınca claim olayı olmasa da ödül verilir. `drop-claim` olayı gelmezse `TwitchClaimFallback` tamamlanmadan 30 sn sonra kimliği (`{kullanıcıNo}#{kampanyaId}#{dropId}`) kurup claim eder.
- Ödül-sınıfı (yayıncı ödülü) kampanyalarda sayaç eşikten sonra da artar; motordaki "son drop eşiği geçildi + claim bekleme süresi doldu ⇒ kampanya bitti" kuralı bunun için vardır — kaldırma.
- Küçük kanallarda en düşük HLS kalitesi kaynak kalitesine eşit olabilir (≈2,5 GB/sa); izleyici >2 Mbps'lik video durumunda yalnız-ses'e düşer.
- Ödül adları kaynaklar arasında farklıdır (Facepunch "Rug", Twitch "Twitch Rivals Spooky Rug"); `DropNames.Key` toleranslı eşleştirir. Kozmetik çift kartlar olabilir.

**Kick**
- Düz HLS trafiği drop ilerlemesi **vermez** (token kullanıcıyı değil yalnız kanalı taşır). İlerleme için gerçek web oynatıcısı gerekir: gizli WebView2 oynatıcı (`KickWebViewWatcher`; ekran dışı pencere, sessiz, en düşük kalite, `--disable-gpu`). İzlerken WebView2 belleği ≈ 0,7–0,8 GB.
- Kick girişi, **telefondaki Kick uygulamasıyla** açılan bağlantıyla (QR) yapılır; aynı bağlantı masaüstü tarayıcıda 404 verir. Kick oynatıcısı için ayrıca tek seferlik giriş gerekir (ayrı WebView2 profili `webview2\`).
- Sunucu sayacı yaklaşık 2 dakikada bir 2'şer birim artar. Kick otomatik claim'i canlıda **hiç doğrulanmadı**.
- Cloudflare alışılmadık istemcileri engeller: `KickConstants`'taki tarayıcı benzeri User-Agent / HTTP sürümlerini koru; JSON olmayan 403 = engel/geçici (token korunur), JSON'lu 403 = geçersiz token.

**Testler**
- Bazı testler sabit tarihli örnek (fixture) kampanyalara karşı **gerçek saati** kullanır. Takvim o tarihleri geçince kod sağlam olsa bile test kırılır (2026-11-06'da oldu: Kick örnekleri 2026-10-30'da bitiyordu, 2036'ya çekilerek düzeltildi). Kod değişmeden CI kırmızıya dönerse önce örnek tarihlerine bak; örneklere çok ileri bitiş tarihi ver ya da `FakeTimeProvider` kullan.

**Uygulama / Windows**
- Pencere kapanınca pencere yok edilir ve bellek kırpılır (tepside ≈ 55–65 MB). WPF bilerek yazılım çizimine (`SoftwareOnly`) alınmıştır; donanım çizimi her aç/kapa'da birkaç MB sızdırıyordu. Düşünmeden geri açma.
- Paylaşılan `SolidColorBrush` kaynakları şablonlarca dondurulur → fırça rengini animasyonla değiştirmek hata verir ya da etkisiz kalır; fırçayı yenisiyle değiştir.
- WPF süreç başına tek `Application` ister: ekran çizen arayüz önizleme testleri ayrı süreçte ve isteğe bağlı koşar (`RUSTDROPER_PREVIEW_*`).
- Tek örnek mutex'i veri klasöründen türetilir. Testler `RUSTDROPER_DATA` vermeli/başlangıcı atlamalı, yoksa gerçek veriye dokunur.
- Kilit ekranı izlemeyi **durdurmaz**; uyku durdurur (uyanınca uygulama devam eder). Windows kapanırken durum dondurulur, sonraki açılışta devam edilir.
- Çökme koruması: çalışırken `run-state.json {Running, CleanExit}` yazılır ve 5 dk'da bir `RustDroper.exe --keepalive` çalıştıran zamanlanmış görev kurulur. Duraklat/Çıkış görevi kaldırır. Kullanıcının kapattığı uygulama asla yeniden başlatılmaz.

## 6. Doğrulananlar ve DOĞRULANMAYANLAR

Canlıda (gerçek hesap) doğrulandı: Twitch girişi, envanter, keşif, HLS ilerleme, PubSub, claim mutasyonu ve yedeği (Twitch-yerel rozetler), Kick QR girişi, gizli oynatıcıyla Kick ilerlemesi, çökme-yeniden-başlatma zinciri, kalıcı giriş, 4 saatlik soak, tepsi bellek hedefleri.

**Doğrulanmadı — dikkatli ol:**
- Claim sonrası Rust ödülünün **oyuna** (Facepunch/Steam) teslimi ve bunun için `drop-claim` olayının gelip gelmediği.
- Bir kampanyadaki birden çok drop'un paralel mi sıralı mı ilerlediği (arayüzdeki süre tahmini paralel varsayar).
- Kick otomatik claim, Kick'te çok uzun (≥ 2 sa) izleme, Kick olgun-içerik kapısı.
- İngilizce Windows'ta `auto` dil yolu (yalnız birim testli), Kick'te gerçek kilit ekranı davranışı (Windows ekran dışı WebView2'yi kısabilir).
- Facepunch sayfa ayrıştırıcıları sayfa yapısına bağlıdır; değişirse kartlar kampanya verisinden yer tutucuya düşer (uygulama çalışmaya devam eder).

## 7. "X'i değiştirirsen Y'yi kontrol et"

| Değişiklik | Ayrıca kontrol et |
|---|---|
| Herhangi bir arayüz metni | iki `Loc` tablosu, `USAGE*.md`, yerleşim değiştiyse `docs/screenshots` |
| Keşif / planlayıcı | `TwitchCampaignDiscoveryTests`, `DropPlannerTests`, canlı `campaigns twitch rust --probe 100` |
| Motor / izleme | sahte saatli motor + soak testleri; sınırlarını asla gevşetme |
| Ayarlar (`AppSettings`) | eski `settings.json` uyumu (`AutoClaimDefaultMigrated` gibi geçiş bayrakları) |
| Yollar / veri | `AppPaths`, `LegacyDataMigrator`; testler geçici veri klasörü kullanmalı |
| Kick oynatıcı | `PlayerMonitor` testleri; sayfa ajanı salt okunur kalmalı (çerez/token/localStorage okumaz) |
| Kullanıcıya görünen her şey | changelog girişi + SemVer artışı (kural 2) |

## 8. Yayınlama

1. Değişikliği yap, testler yeşil, changelog + sürüm artışı, `build\changelog.ps1`.
2. `main`'e commit + push; `ci` iş akışını bekle.
3. `vX.Y.Z` etiketi (`<Version>` ile aynı) oluşturup etiketi it: `release` iş akışı derler, test eder, `build\package-release.ps1` çalıştırır ve `RustDroper-X.Y.Z-win-x64.zip` + `SHA256SUMS.txt` dosyalarını GitHub Releases'a koyar.
4. Kullanıcılar zip'i **Releases** sayfasından indirmeli, "Code → Download ZIP"ten değil (onda exe yok).

`build\yayin-commit.ps1` ve `build\yayin-tara.ps1` ilk herkese açık commit için kullanılan tek seferlik yardımcılardı; gerek yok.
