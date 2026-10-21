# 02 — Mimari ve Teknoloji Kararları

## 1. Hedefler (kullanıcı gereksinimleri)
1. Twitch **veya** Kick hesabıyla giriş; **tek butonla** başlat.
2. Bilgisayarı/kullanıcıyı meşgul etmeden arka planda çalış (sistem tepsisi), drop'ları sırayla otomatik topla ve **claim et**.
3. Her drop için: yayıncı adı, yayıncı linki, **% ilerleme**, izlenen süre, **kalan süre**, durum.
4. Genel (saat bazlı) ve yayıncıya özel drop'ları birlikte yönet.
5. **Kararlılık** her şeyden önce: hata verirse çökmesin, kendini toparlasın, ne olduğunu söylesin.
6. Mümkünse **portable**; olmuyorsa/boyut büyükse kurulumlu.
7. Sadece **Rust** (diğer oyunlar kapsam dışı — ama kod "oyun slug'ı" ile ayarlanabilir kalır, ucuza genelleşir).
8. İleride GitHub'da yayınlanabilir (temiz repo, MIT lisans, sır içermeyen).

## 2. Teknoloji kararı

| Seçenek | Artı | Eksi | Karar |
|---|---|---|---|
| **C# / .NET 10 + WPF** | SDK zaten kurulu (10.0.302); tipli model (referanstaki `KeyError` sınıfı hatalar yok); referansı olan yığın (StreamDropCollector); Windows sertifika/DPAPI yerel; WebView2 yedek yol | Self-contained boyut ~50–80 MB (ölçülecek); sadece Windows | **SEÇİLDİ** |
| Python + PyInstaller | Miner'ın yığını | Şemasız dict'ler, GUI/asyncio kararsızlığı (issue'larda görüldü), antivirüs yanlış alarmı | Hayır |
| Rust + Tauri | Küçük exe | Rust toolchain kurulu değil; GUI/WebView/tray için ek karmaşa | Hayır |
| Electron | Kolay UI | 150 MB+, bellek hungry — "bilgisayarı meşgul etme" hedefine ters | Hayır |

- **UI:** WPF (koyu tema, .NET 10 Fluent/özel stiller). Tepsi: `H.NotifyIcon.Wpf` (veya WinForms `NotifyIcon`; M0'da seçilecek).
- **Sadece Windows hedefi** (Rust oyuncuları Windows'ta). Core katmanı UI'dan bağımsız → ileride Linux/CLI mümkün.
- **WebView2** yalnızca (a) Kick giriş yedek yolu, (b) izleme "Tier B" yedeği için. Ana yol WebView2'siz → portable'da ek bağımlılık yok.

### Paketleme — M0.3'te **ölçüldü** (boş WPF penceresi, win-x64, Release; 2026-10-15)
| Varyant | exe | ZIP | Boşta RAM (özel) | Gereksinim |
|---|---|---|---|---|
| A. Self-contained, tek dosya, **sıkıştırmalı** | 61,8 MB | 56,4 MB | **~149 MB** | Yok |
| B. Self-contained, tek dosya, **sıkıştırmasız** | 133,2 MB | **56,0 MB** | **~95 MB** | Yok |
| C. Self-contained + ReadyToRun (sıkıştırmalı) | 66,2 MB | 60,7 MB | (ölçülmedi; A gibi) | Yok |
| D. Framework-dependent, tek dosya ("lite") | **0,2 MB** | 0,1 MB | ~98 MB | .NET 10 Desktop Runtime |

Bulgular:
- Sıkıştırma exe'yi küçültür ama çalışma anında **~55 MB fazladan RAM** yer; gün boyu arka planda çalışan uygulama için RAM, disk boyutundan önemli → **B tercih**.
- ZIP indirme boyutu A ve B'de aynı (~56 MB): sıkıştırmasız exe zaten ZIP içinde sıkışıyor.
- Kurulumlu (Inno Setup) paket **boyutu küçültmez** (aynı runtime) → boyut gerekçesiyle kurulum gerekmez. Portable yeterli.
- ReadyToRun +4,4 MB; açılış hızı bir tepsi uygulaması için önemsiz → kullanılmayacak.
- Boş WPF penceresinin kendisi ~95 MB özel bellek: tepsiye küçülünce pencereyi **kapatıp yok etme** + GC/working-set kırpma → M5.4'te ölçüldü: yalnızca bunlar özel belleği düşürmez (110 MB kalır); **yazılım çizimi** ile ~52 MB (aşağıya bkz.).
- Tek dosya WPF, yerel kütüphaneleri `%TEMP%\.net\RustDroper.App` altına açar (kullanıcı verisi değil, önbellek). Kullanıcı verisi yalnızca `%LOCALAPPDATA%\RustDroper` (gizli; `AppPaths.DefaultDirectory`, `RUSTDROPER_DATA` ile ezilir). Eski `<exe>\data` ilk açılışta `LegacyDataMigrator` ile TAŞINIR (kopya değil; yalnız `secrets.dat` varsa ve hedefte yoksa).

### Bellek ölçümü ve iyileştirme — M5.4 (2026-10-16; `build\measure-memory.ps1`, Release self-contained B varyantı, oturumsuz uygulama)
"Özel bellek" = `PrivateMemorySize64` (ayrılan/commit; çalışma kümesi kırpılsa da düşmez). "Özel ÇK" = Görev Yöneticisi'nin "Bellek" sütunu.
| Aşama | **Önce** (donanım çizimi) | **Sonra** (yazılım çizimi + GC ayarları) |
|---|---|---|
| Pencere açık | 115,8 MB özel · 86,7 MB özel ÇK · 628 tutamaç · 53 iş parçacığı | **60,5 MB** · 33,9 MB · 574 · 16 |
| **Tepside** (pencere yok edildi, +10 sn / +40 sn) | **110,5 / 110,5 MB** özel · 5,4 MB özel ÇK · 674 · 51 | **51,6 / 52,1 MB** · 3,4 MB · 609 · 20 |
| 15 pencere aç/kapa sonrası tepside (+40 sn) | **154,4 MB** (≈ +44 MB; 8 döngülü koşularda +50–60 MB) · 679 tutamaç | **54,1 MB** (+2 MB) · 600 tutamaç · 13 iş parçacığı |

Bulgular:
- **Yönetilen yığın yalnız ~2,4 MB (tepside) / ~4,9 MB (pencere açık):** özel belleğin neredeyse tamamı *yerel* (WPF/D3D/GPU sürücüsü, çalışma zamanı). Bu yüzden `gcConcurrent=0`, `GCConserveMemory=7`, `TieredCompilation=0` denemeleri özel belleği değiştirmedi (±2 MB).
- **Asıl etken donanım çizimi:** D3D yüzeyleri/GPU sürücü ayırmaları tepside ~110 MB'ı tutuyor ve her pencere aç/kapa'da **~3–6 MB sızdırıyor**. `RenderOptions.ProcessRenderMode = SoftwareOnly` ile tepside ~52 MB, sızıntı yok, iş parçacığı 58→20. Arayüz (koyu tema, ilerleme çubukları) GPU gerektirmiyor; görsel olarak doğrulandı. Geri dönüş: `RUSTDROPER_RENDER=hardware` (ölçüldü: eski sayılar).
- Uygulanan: yazılım çizimi (varsayılan), `ConcurrentGarbageCollection=false`, `TieredPGO=false`, `GCConserveMemory=5` (yığın küçük olduğundan etkisi ölçülemez ama çalışan motor için güvenli), tepsideyken **10 dk'da bir kırpma + günlüğe `bellek (...)` satırı** (`MemoryMonitor`; `RUSTDROPER_MEMORY_LOG_SECONDS` ile ayarlanır), pencere kapanınca `GC + EmptyWorkingSet` (zaten vardı).
- **Hedef (tepside özel bellek <100 MB): sağlandı** (oturumsuz ~52 MB). Motor çalışırken (HLS + PubSub + GQL) ek yük yönetilen yığında birkaç MB'dır (aşağıdaki soak); **gerçek hesap/canlı yayında ölçülmedi** → canlı kullanımda `data\logs\rustdroper-*.log` içindeki `bellek (tepside): özel=…` satırlarından izlenir.
- **Hızlandırılmış soak** (`tests\RustDroper.Core.Tests\SoakTests.cs`, sahte saat/HTTP; `RUSTDROPER_SOAK_SCALE=N` ile N kat uzatılır; gerçek 24 sa soak ertelendi): gerçek `TwitchHlsWatcher` 10 dk (1200 parça ≈ 120 MB trafik): yığın 3,06→3,60 MB, tutamaç 449→446, iş parçacığı 27→26 · servis 40 başlat/durdur + 10 uyku/uyanış: yığın 2,87→2,89 MB, her bağlantı kapatıldı (500 döngüde tutamaç +19'da düzleşir, doğrusal artmaz) · motor 3 sa (204 yoklama, ağ hatası + kanal kapanması): yığın 2,35→2,55 MB, tutamaç 411→412, iş parçacığı 23→22. Üç test de büyümeyi sınırlar (yığın <3 MB, tutamaç ≤15–20, iş parçacığı ≤5).

### Kick gizli oynatıcı (Tier B, WebView2) bellek/CPU — M4-B (2026-10-19; gerçek Kick sayfası, 160p, ekran dışı pencere 1280×720)
Ölçü = WebView2'ye ait TÜM işlemlerin (`CoreWebView2Environment.GetProcessInfos`) toplam özel belleği (commit); uygulamanın kendi belleği ayrıdır (**77–86 MB**, tepsi değil pencere açık).
| Durum | Toplam özel (WebView2) | Döküm (Browser / GPU / Renderer / Utility) | CPU (bir çekirdek) |
|---|---|---|---|
| Girişsiz, GPU açık (ilk 5 sn, Auto kalite 360p→720p) | tepe **925–985 MB** | — | %75 (geçici) |
| Girişsiz, **160p**, GPU açık | **715–830 MB** (12 işlem) | 50 / **192** / 476 / 104 | %15–30 |
| Girişsiz, 160p, **`--disable-gpu`** | **570–660 MB** | 51 / **30** / 387–472 / 103 | %18–25 |
| `--disable-gpu` + `--renderer-process-limit=1` + site izolasyonu kapalı | 577–678 MB | 50 / 29 / 394–495 / 104 | %26–32 (kazanç yok) |
| `--disable-gpu`, pencere 1024×600 (1280×720 yerine) | 573–667 MB | 50 / 27 / 394–486 / 104 | %18–32 (kazanç yok) |
| **GİRİŞLİ canlı sınama, 160p, GPU açık (13 dk, uygulamadaki gerçek yol)** | **764–829 MB** (10–11 işlem; ilk 5 sn tepe 985) | 42–45 / 245–306 / 391–506 / 104–130 | **%40–50** |
- **Hedef (≤ 400 MB) TUTMADI:** ilk tahmin (200–400 MB) yanlıştı; 13 dk'da sızıntı yok (764→829 MB dalgalanma, düzleşiyor). Kalem kalem: **Renderer ~390–480 MB** (Kick'in tam sayfası: JS yığını + sohbet; pencere boyutu ve site izolasyonu etkisiz), **GPU 190–300 MB** (yazılım oluşturma ile ~30 MB: **−160 MB**, CPU farkı ölçülemedi), Utility ~104, Browser ~45.
- **Uygulanan:** `--disable-gpu` oynatıcı için varsayılan (`RUSTDROPER_PLAYER_GPU=1` ile eski); oynatıcı yalnızca Kick **çalışırken** vardır: Duraklat/Dur → sayfa boşaltılır, 20 sn sonra pencere + tüm WebView2 işlemleri kapanır (önceki ölçümlerdeki ~50 MB uygulama bellek hedefi Kick durunca geri gelir). Girişli sınama GPU AÇIK sürümle yapıldı; `--disable-gpu`'lu girişli değer ölçülmedi (girişsizde −130…−160 MB).
- **Daha fazla azaltma (öneri, ölçülmedi):** (1) `player.kick.com/<kanal>` (sohbetsiz gömülü oynatıcı) — renderer'ı belirgin küçültür ama **izleme kredisi verip vermediği bilinmiyor** (sunucu sayacıyla sınanmalı); (2) `Network.setBlockedURLs` ile reklam/izleme alan adlarını engelleme (bellek/CPU düşer ama platform kuralları ve "normal izleyici" izi açısından önerilmez); (3) kullanıcı için "Kick'te kesintisiz izleme süresi sınırı / dinlenme" ayarı (varsayılan KAPALI; hem kaynak hem docs/04 risk gerekçesiyle).

**Öneri:** Varsayılan dağıtım = **B (portable ZIP)**; isteyen için **D (lite, 0,2 MB)**. Veri `data\` klasöründe; kayıt defterine yazmaz (Windows ile başlat hariç, HKCU\Run, kullanıcı açarsa).

## 3. Çözüm yapısı

```
RustDroper\
  docs\                     ← bu dokümanlar
  src\
    RustDroper.Core\        ← UI'sız iş mantığı (netstandard-benzeri, net10.0)
      Domain\               ← Campaign, Drop, DropProgress, Reward, Streamer, Platform...
      Platforms\
        Twitch\             ← Auth, GqlClient, HashRegistry, PubSub, Directory, Watchers
        Kick\               ← Auth, RestClient, ViewerWatcher
      Watching\             ← IWatcher + Tier A/B uygulamaları
      Orchestration\        ← Scheduler, Watchdog, ProgressReconciler, ClaimService
      Infrastructure\       ← AtomicJsonStore, Dpapi, RateLimiter, RetryPolicy, Logging, Clock
    RustDroper.App\         ← WPF: tepsi, ana pencere, ayarlar, bildirim
    RustDroper.Cli\         ← (opsiyonel) başsız hata ayıklama / canlı smoke test
  tests\
    RustDroper.Core.Tests\  ← xUnit; fixtures\ (Kick gerçek JSON, Twitch kayıtlı cevaplar)
    RustDroper.FakePlatform\← sahte Twitch/Kick sunucusu (uçtan uca test için)
  build\                    ← publish betikleri (portable zip)
```

## 4. Çalışma modeli

### 4.1 Tek orkestratör, açık durum makinesi
Referansların (özellikle Miner'ın) en büyük zaafı örtük/dağınık durumdur. Biz **her platform için bir `PlatformSession`** ve
üstte **bir `Orchestrator`** koyarız:

```
        ┌──────────── Orchestrator (tek döngü, iptal edilebilir) ────────────┐
Start → │ LoadInventory → SelectTarget → EnsureWatcher → Monitor → Claim → Next │ → Idle(bekle)
        └───────▲──────────────────────────────────────────────┬─────────────┘
                │ watchdog tetiklerse: kanalı kara listeye al ──┘ (yeniden seç)
```
- **LoadInventory:** kampanyalar + envanter (sunucu-gerçeği). Rust'a süz. Uygunluk: `kalanDk ≤ bitişe kalan süre`, hesap bağlı mı.
- **SelectTarget (planlayıcı):** 1) bir yayıncı drop'u için o yayıncı **canlıysa** → hedef (süresi kısa/biten önce);
  2) yoksa genel drop için Drops-Enabled en iyi canlı Rust kanalı. U4 cevabına göre ayarlanır (tek kanal birden çok kampanyayı ilerletiyorsa
  yayıncı kanalı hem genel hem özel ilerletir → önceliklidir).
- **EnsureWatcher:** seçili kanal için `IWatcher` başlat/değiştir.
- **Monitor:** olay (PubSub drop-progress/drop-claim, stream-down) + 60 sn'de bir sunucu-gerçeği uzlaştırması.
- **Watchdog:** `M` dk içinde sunucu ilerlemesi artmadıysa → kanalı kara listeye al (süreli), yeni hedef seç; 3 ardışık başarısızlıkta
  platformu "degrade" işaretle ve UI'da nedeni göster. **Sessiz takılma yok.**
- **Claim:** claim edilebilir drop'u hemen claim et, sonucu doğrula (envanterde `claimed`), başarısızsa tekrar dene (backoff).
- **Idle:** aktif kampanya yok / hepsi tamam → bir sonraki kampanya başlangıcına kadar uyu (açılışta `starts_at`'a göre otomatik uyanma —
  "Start verildiğinde otomatik başla" senaryosu).

### 4.2 Dayanıklılık kuralları
- Her HTTP çağrısı: timeout, `CancellationToken`, üstel geri çekilme (tavan 60–180 sn), jitter yok değil ama amaç hız sınırına saygı; GQL için ≤ 5 istek/sn.
- Hata sınıfları: *Geçici* (ağ, 5xx → retry), *Kimlik* (401 → token yenile/yeniden giriş UI'ı), *Şema/Hash* (degrade mod + uyarı),
  *Kalıcı* (claim 400 → drop'u not et, devam). Hiçbiri uygulamayı **çökertmez**.
- Uyku/uyanma ve ağ değişimi (M5.2, `DropperService` + `ISystemEvents`; App'te `WindowsSystemEvents` = `SystemEvents.PowerModeChanged` + `NetworkChange.NetworkAvailabilityChanged`):
  - **Uykuya geçerken** motor arka planda temiz durdurulur (HLS/PubSub kapanır; bildirim bloklanmaz); kullanıcının "çalışsın" isteği (`_wantRunning`: Başlat verildi, Duraklat verilmedi) korunur.
  - **Uyanınca** 8 sn ağı bekler → çalışan her şey durdurulur (soketler ölüdür) → yalnız oturum varsa (`LoggedIn`/`Offline`) `TwitchSessionManager.RestoreAsync` ile doğrulanır → `LoggedIn` ise ve kullanıcı çalıştırıyorduysa motor yeniden başlar; ağ yoksa `Offline` + 60 sn'de bir yeniden deneme (cihaz-kodu girişi bozulmaz).
  - **Ağ gidip gelince** → `ConnectivityMonitor` (aşağıdaki §4.6): hesap `Offline` ise hemen doğrula; motor çalışıyor ve `Degraded` ise ya da kesinti ≥20 sn ise motoru yeniden başlat (izleyici ~30 sn kesintide pes edip kanalı 20 dk engeller; yeni motor engelleri sıfırlar); kısa çırpmaya dokunma; yalnız envanter bayatsa yenile. Uyku/uyanış sırasında izleyici durur, uyanışta `ResumeDelay` kadar sessiz kalır (uyanış toparlaması yapar).
  - **Askıda kalma yok:** her `HttpClient` 30 sn zaman aşımlı; PubSub PONG 10 sn; motor/servis iptal belirteci tüm bekleyen çağrılara akar (testle: keşif çağrısı sonsuz asılsa bile uyku bildirimi motoru durdurur).
  - Motor: ilerlemeyi **gözleyemediğimiz** yoklamalar (ağ yok) takılma sayacına işlemez; böylece kesinti kanalı/kampanyayı haksız yere engellemez/60 dk bekletmeye almaz.
- Global istisna yakalayıcı → `data\logs\crash-*.log` + tepside "yeniden başlatıldı" bildirimi.
- Tüm kalıcı yazmalar **atomik** (temp → `File.Replace`), `.bak` yedeği, `schemaVersion`.

### 4.3 Başsız motor — gerçekleşen tasarım (M3.3, `Orchestration/`)
- **Parçalar:** `IDropPlatform` (keşif, izleyici, sunucu oturumu, arka plan işleri, olaylar — platformdan bağımsız sözleşme) · `DropPlanner` (saf) ·
  `DropEngine` (tek döngü, tek platform) · `TwitchDropPlatform` (bağdaştırıcı). Kick, aynı arayüzü uygulayarak eklenecek.
- **Hata sözleşmesi:** `PlatformAuthException` → `AuthRequired` (2 dk'da bir tekrar); geçici ağ/sunucu (`Retry.IsTransient`) ve şema/hash (`PlatformDataException`) →
  `Degraded` + üstel bekleme (30 sn → en çok 15 dk). Hiçbiri motoru çökertmez; arka plan işi (PubSub/claim) çökerse olay günlüğüne yazılır.
- **Planlayıcı kuralları:** yalnız şu an çalışan, tamamlanmamış, bekletmede olmayan kampanyalar; tüm drop'ları claim edilmiş olanlar atlanır;
  kalan en uzun eşik bitişe yetişmiyorsa girişilmez; kanal seçimi = engellenmemiş + en çok izleyici. Sıra: **yayıncıya özel önce**, sonra bitişi yakın, sonra kalan süresi kısa.
- **İzleme döngüsü:** her `PollInterval=60 sn`'de `dropCurrentSession` okunur (sunucu-gerçeği). İlerleme (dakika ya da drop kimliği değişimi) = canlılık.
- **Watchdog:** ilerleme yoksa `Grace(4 dk)+Stall(5 dk)`; ilerleme görüldükten sonra `Stall(5 dk)`; drop tamamlandıysa `+ClaimWait(3 dk)`. Takılan kanal 20 dk engellenir;
  aynı kampanyada 2 kanal takılırsa kampanya 60 dk bekletilir ve "hesap bağlı olmayabilir" uyarısı verilir. Yoklama art arda 5 kez başarısızsa keşfe dönülür.
- **Tamamlanma:** ilerleme görüldükten sonra art arda 3 yoklamada oturum yoksa kampanya tamamlandı sayılır (sıradaki drop claim sonrası ~4–20 sn'de başlar).
- **Geçişler:** izleyici `Offline` → kanal 3 dk engelli; PubSub `stream-down` → anında geçiş; her 10 dk'da yeniden keşif; genel kampanyadayken (ve drop %90'dan azsa) yayıncı kampanyası belirirse öncelikli olana geçilir.
- **Gözlenebilirlik:** `EngineSnapshot` (durum, mesaj, hedef, oturum, son ilerleme, claim sayısı, son 30 olay) + `Changed` olayı → CLI/UI/tepsi aynı kaynağı gösterir.
- **Twitch claim yolu (U8 sonrası, canlı doğrulanmış; `docs/PLATFORM-NOTES.md`):** `TwitchAutoClaimer` kuyruğu iki kaynaktan beslenir — (1) PubSub `drop-claim` olayı, (2) `TwitchClaimFallback`: `drop-progress current==required` geldikten `30 sn` sonra aynı drop için `drop-claim` görülmediyse `dropInstanceID`'yi `{userId}#{campaignId}#{dropId}` olarak kurar (kampanya kimliği, `TwitchDropPlatform.DiscoverAsync`'in drop→kampanya eşlemesinden). Aynı kimlik iki kez işlenmez; claim idempotenttir (`ELIGIBLE_FOR_ALL` tekrar döner, ödül çoğalmaz). `claimDropRewards: null` (geçersiz/hazır olmayan kimlik) yeniden denenebilir `Rejected("NULL_PAYLOAD")`dır; kalıcı hata yalnız hash/yetki/şema istisnalarıdır. Her sonuç `history.jsonl`'a yazılır (`dropInstanceId` biçimi olay mı yedek mi olduğunu gösterir). Kapsam dışı/bilinen sınır: *reward* (yayıncı rozeti) kampanyalarında `dropCurrentSession` eşiği geçince de sürer; motor bu sınıfta "oturum yok" tamamlanmasını göremez — planlayıcı yalnız Rust oyun kampanyalarını seçtiği için etkisiz.
- **HLS kalite koruması:** "en düşük video" varyantı 2 Mbps'i aşıyorsa (küçük kanallarda alt kalite yok = kaynak, ≈41 MB/dk) ve `audio_only` varsa yalnız-ses seçilir (`HlsWatcherBase.Choose`); sunucu sayacı yalnız-sesle aynı hızda ilerler (U1/C8).

### 4.3b Kick bağdaştırıcısı (M4.2–M4.3a) — `Core\Platforms\Kick`, `Core\Watching`
- **İstemciler:** `KickPublicClient` (kampanya listesi) · `KickApiClient` (kimlikli `/drops/progress`, `/progress/summary?campaign_id=`, `POST /drops/claim`; kamusal canlı yayın listesi, kampanya yayınları, kanal+`playback_url`; hız sınırı 2/sn, GET'lerde 3 denemeli `Retry`, **claim yeniden denenmez**) · `KickHttp` (sınıflama: `PlatformAuthException` / `PlatformDataException` / `PlatformHttpException`) · `KickProgressParser`/`KickLiveParser` (toleranslı; zarf/tip varyantları; bozuk öğe atlanır).
- **`KickDropPlatform : IDropPlatform`:** keşif = kamusal kampanyalar (Rust, bitmemiş) + canlı kanallar (yayıncı kampanyası: kampanya kanalları **canlı ve kampanya oyununda**; genel: kampanya yayın listesi `lang_code=en`, boşsa Rust kategorisi en kalabalıkları); `GetSessionAsync` = kimlikli `summary.progress_units` (kümülatif dk; `required_units` dakikadır — doğrulandı) → eşiği henüz geçilmemiş en küçük ödül (`DropSession(rewardId, dk, eşik)`); tüm eşikler geçildiyse: otomatik claim açıksa "tamamlandı, claim bekliyor", kapalıysa `null` (kampanya tamam, elle claim); özet 404 = ilerleme yok (hata değil). `IDropPlatform` sözleşmesi aynen: motor değişmedi (`DropEngine` platformdan bağımsız).
- **İzleme:** `HlsWatcherBase` (ortak: en düşük kalite, medya listesi yoklama, yeni parça indirme, token yenileme) → `TwitchHlsWatcher` (GQL+usher) ve `KickHlsWatcher` (kanal API'si `playback_url`). Sunucu krediyi veriyor mu (U3) canlı ölçülecek; kanıtlanmadan Tier B/C'ye geçilmez.
- **Otomatik claim (`KickAutoClaimer`):** varsayılan **kapalı**; açıksa 45 sn'de bir `progress` okur, **yalnızca bilinen Rust kampanyalarındaki** tamamlanmış + claim edilmemiş ödülleri claim eder; ödül başına ≤3 deneme (10 dk aralık), sonuçlar `history.jsonl` (Platform="Kick", DropId=ödül, DropInstanceId=kampanya). Canlı hesapta hiç çalıştırılmadı; yalnızca sahte sunucu testlerinde.
- **Tek-aktif-kanal kuralı:** Kick'te aynı anda tek kanal için ilerleme olur; motor zaten tek kanal izler. İki platform = iki bağımsız `DropperService`/`DropEngine` örneği (Twitch'ten bağımsız, paralel).
- **Platform başına servis (M4.3b):** `DropperService(…, platform: PlatformProfile)` — profil ad/elle-claim sayfası/bildirim anahtarı ön ekini verir; hesap arayüzü `IPlatformAccount` (Twitch: `TwitchSessionManager`, Kick: `KickAccount`). Taşıyıcı tipler (`TwitchSession`, `TwitchRestoreResult`, `TwitchDeviceCode`, `TwitchInventorySnapshot`) platformdan bağımsız şekil olarak kullanılır; Kick bunlara çevirir (Kick QR bağlantısı → `TwitchDeviceCode.VerificationUri`). `KickConnectionFactory` bağlantı kurar (paylaşımlı hız sınırı, otomatik claim yalnız açıkken), `KickInventoryBuilder` hesap ilerlemesini kamusal Rust kampanyalarıyla birleştirip envanter şekline çevirir. Kick durumu ayrı dosyada (`data\state-kick.json`), claim geçmişi ortak `history.jsonl` (Platform="Kick").

### 4.3c Kick Tier B — gizli gerçek oynatıcı (M4-B, canlı doğrulandı)
- **Neden:** Kick'te yalnız HLS sunucu ilerlemesi vermiyor (docs/PLATFORM-NOTES.md U3); izleyici kimliği Kick'in kendi oynatıcısının viewer-websocket'inden geliyor. Kullanıcı kararı: gerçek oynatıcı (Tier B); **presence/telemetri taklidi (Tier C) yapılmaz** (kod tabanında yok, denenmedi).
- **Core (UI'sız, test edilebilir):** `IKickPlayerHost` (`OpenAsync(slug)`, `RestartAsync`, `ReleaseAsync`, `Status` + `StatusChanged`; durumlar Idle/Starting/Playing/Stalled/Offline/LoginRequired/Failed/Unavailable; istisna fırlatmaz) · `IKickPlayerSession` (profil oturumu: Unknown/Checking/LoggedIn/LoggedOut/Unavailable; `RefreshAsync`, `LoginAsync`) · `KickWebViewWatcher : IWatcher` (5 sn'de bir durum; Playing = iyi; Starting >90 sn / Stalled >45 sn / Failed → `RestartAsync` [10 dk'da ≤ 4, sonra `WatchEnd.Failed` → motor kanalı değiştirir]; Offline → `WatchEnd.Offline`; LoginRequired/Unavailable → hemen `Failed` + eyleme dönük neden; her çıkışta `ReleaseAsync`). `KickDropPlatform`/`KickConnectionFactory` isteğe bağlı `playerHost` alır; verilmezse eski HLS davranışı (yalnız CLI/tanılama).
- **App `Player\` (WebView2):** `WebViewKickPlayerHost` (UI iş parçacığına serileştirilmiş, tek örnek) — pencere **ekran dışı** (-20000,-20000; görev çubuğunda yok; `WS_EX_NOACTIVATE|TOOLWINDOW`; odak almaz), **1280×720** (Kick dar pencerede mobil yerleşime geçer), tarayıcı bayrakları `--autoplay-policy=no-user-gesture-required --disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows --disable-gpu`; ses KAPALI iki katman: `--mute-audio` (ses çıkışı hiç açılmaz) + `CoreWebView2.IsMuted` (her gezintide yeniden uygulanır) — ölçüm: gizli oynatıcı oynarken Windows ses oturumlarında WebView2 süreci için oturum yok (`build/audio-probe.ps1`); kick.com dışı üst düzey gezinti, popup, indirme ve izin istekleri engelli; en düşük kalite DevTools gerçek fare olaylarıyla (ayar menüsü → en küçük çözünürlük, her açılışta); `PlayerMonitor` (saf mantık): "Oynuyor" yalnız video zamanı **iki yoklama arasında ilerlediyse**; yayın kapalı = kanal API'si art arda 2 kez "yayın yok"; oturum yok = "Giriş Yap" düğmesi 2 yoklamada görünür; sayfa betiği yanıt vermezse Failed. Bırakınca `about:blank` (izleme anında kesilir) + 20 sn sonra pencere yok edilir (kanal geçişinde aynı pencere yeniden kullanılır). WebView2 Runtime yoksa `Unavailable` (açıklayıcı mesaj, çökme yok).
- **Oturum/gizlilik:** profil `data\webview2\`; giriş **yalnızca kullanıcı**, görünür `KickLoginWindow`'da (e-posta/parola ya da Kick'in sunduğu yöntem); uygulama hiçbir alana yazmaz, çerez/belirteç/localStorage okumaz, günlüğe yazmaz. Sayfa ajanı (`KickPlayerScripts`) yalnız video durumunu, kanalın canlı olup olmadığını, "Giriş Yap" düğmesini okur; çerez bandını X ile kapatır ("Tümünü kabul"e basmaz).
- **Arayüz:** Kick kartında "Oynatıcı oturumu: açık/yok/doğrulanmadı" + "Oynatıcı girişi" + ↻; **Başlat'ta oturum yoksa Kick başlatılmaz** (uyarı şeridi neden + ne yapılacağı; Twitch etkilenmez); oturum bilinmiyorsa önce kısa bir gizli doğrulama.
- **Sunucu-gerçeği değişmedi:** ilerleme yine API token'ıyla `/drops/progress/summary` (`KickDropPlatform.GetSessionAsync`); API token'ı (QR) ve oynatıcı profili (WebView2) iki ayrı oturumdur.
- **Risk notu (docs/04):** gizli pencere + 7/24 kesintisiz izleme bu yolun en büyük iz bıraktığı yerdir; varsayılan davranış değişmedi (tek hesap, tek kanal). Öneri (uygulanmadı): Kick için "kesintisiz izleme süresi uyarısı/dinlenme" ayarı, varsayılan KAPALI.

### 4.4 Uygulama katmanı ve arayüz — gerçekleşen tasarım (M2)
- **Katmanlar:** `DropEngine` (başsız) → `DropperService` (Core; hesap + motor yaşam döngüsü, tek `DropperSnapshot` + `Changed`) → `MainViewModel` (App; görüntüyü UI iş parçacığına taşır, komutlar) → `DashboardViewModel` (salt okunur, `DashboardInput`'tan türetir) → XAML. UI'da iş mantığı yoktur.
- **Pencere ömrü:** kapatılınca pencere yok edilir (`WindowController`), görünüm modeli yaşar; pencere kapalıyken pano listeleri kurulmaz, yalnız tepsi ipucu güncellenir. Tepsi: kendi Win32 sarmalayıcımız (`TrayIcon`).
- **Veri kaynakları:** "şu an" = `EngineSnapshot`; kampanya listesi = motorun son keşfi (`ObservingPlatform` sarmalayıcısı yakalar); drop ilerlemesi/claim = `Inventory` (5 dk'da bir + claim sonrası) ve canlı oturumla bindirilir; geçmiş = `history.jsonl`.
- **Hata yolu:** `AuthRequired` → bir kez sessiz yenileme → olmazsa hesap kartı "Yeniden giriş yap". Platform/ağ hataları motor rozetinde ve olay günlüğünde; yenileme hataları uyarı şeridinde.
- **İki platform (M4.3b):** `MainViewModel` opsiyonel Kick servisiyle iki `DropperService`'i yönetir: Başlat/Duraklat ikisini birden (çalışan varsa hepsi durur, yoksa girişli her platformda başlar), Yenile ikisini, tepsi ipucu "Twitch: … · Kick: …" (Kick kullanımdaysa; ≤127 karakter), uyarı şeridi Kick hatasını "Kick:" önekiyle gösterir. Pano tek `DashboardViewModel`'dir ve platform seçici (Twitch | Kick) seçilen servisin görüntüsünü gösterir. **Kick girişi QR'dır:** giriş sürerken kartta yerel üretilmiş QR + "Bağlantıyı kopyala" görünür (tarayıcı açılmaz: bağlantı masaüstünde 404 verir, telefondaki Kick uygulamasıyla açılmalı — docs/PLATFORM-NOTES.md K6). Kick yokken (servis verilmezse) kart eskisi gibi "Yakında".

### 4.6 İnternet bağlantısı izleme (M5.5)
- **Sorun (kullanıcı geri bildirimi, M5.4 sonrası):** ağ kopunca arayüz "bağlı" kalıyordu (kopma yalnız günlüğe yazılıyordu, motor ~dakikalar sonra hata durumuna düşüyordu); internetsiz açılınca bağlantı gelince bağlanmak 60 sn'lik yeniden deneme zamanlayıcısını bekliyordu; bildirim yoktu.
- **`ConnectivityMonitor` (Core):** karar **aktif yoklamadan** gelir (`HttpConnectivityProbe`: Windows'un `msftconnecttest.com` ve Twitch `id.twitch.tv` uçlarına eşzamanlı hafif GET; biri cevap verirse "internet var", ikisi de yoksa "yok"; captive-portal yönlendirmesi sayılmaz). Çevrimiçiyken 3 sn'de bir, şüphede/çevrimdışıyken 1 sn'de bir yoklar; art arda **2** başarısız yoklama = koptu, **1** başarılı = geldi. **Windows ağ olayı ipucudur:** "bağdaştırıcı yok" derse tek başarısız yoklama yeter (anında), "var" derse hemen yoklanır; başka bir bağdaştırıcıyla internet sürüyorsa yoklama başarılı olur → yanlış alarm yok. Motor hata durumuna düşünce (`Degraded`) ve yenileme hatasında hemen yoklanır; gerçek bir istek başarılı olursa (`ReportReachable`) yanlış "yok" düzelir. Uyku: izleyici durur, uyanışta `ResumeDelay` sessiz (uykuya girerken/uyanırken ağ gitmesi "kopma" sayılmaz).
- **Kopunca (`DropperService`):** `DropperSnapshot.NetworkOnline=false` → pencerede turuncu şerit "İnternet bağlantısı yok — bağlantı gelince otomatik devam edilecek", tepsi ipucu "internet bağlantısı yok", Twitch kartı "Çevrimdışı (hesap)"; hesap `LoggedIn → Offline`; devam eden oturum doğrulaması 30 sn zaman aşımı beklenmeden iptal edilir; balon "İnternet bağlantısı koptu". Motora dokunulmaz (kendi hata durumunu yönetir).
- **Gelince:** balon "İnternet bağlantısı geri geldi — N sn/dk süren kesinti bitti" (yalnız "koptu" bildirimi verildiyse), oturum **hemen** doğrulanır (yoklama zaten interneti doğruladığı için ilk deneme başarılı olur; 15 sn'lik yeniden deneme zamanlayıcısı yalnız Twitch'e ulaşılamıyorsa yedek), kullanıcı çalıştırıyorduysa motor başlar/yeniden başlar.
- **Ölçüm (gerçek uygulama + yerel sahte "internet" sunucusu açıp kapatılarak, `RUSTDROPER_PROBE_URLS` sınama kancası):** kopma ≈ **6,7 sn**'de, gelme ≈ **0,3–0,5 sn**'de görüldü. Kopma süresi bu sınamada yapay olarak uzun: Windows kapalı yerel porta bağlanmayı 2 sn'de reddediyor (2 yoklama × 2 sn + aralıklar); gerçek internet kesilince DNS/bağlantı hatası genelde hemen döner (≈2–4 sn), yukarı hat kopup bağdaştırıcı açık kalırsa en kötü ≈ 2 yoklama × 3 sn zaman aşımı + aralıklar (≈8–10 sn). **Bağdaştırıcı düzeyinde kopma (Wi-Fi kapatma, kablo çekme) Windows olayıyla ≈ tek yoklama süresinde (çoğu kez <1–2 sn) algılanır.**

### 4.5 Bildirimler (M5.3)
- **Kaynak (Core):** `DropperService.Notification` → `UserNotification` (tür, tekrar anahtarı, başlık/metin, isteğe bağlı bağlantı, bastırma süresi). Üretilenler (M5.5'te **internet koptu/geri geldi** de eklendi; kısa tekrar penceresi 20 sn): **drop claim edildi** (ödül adı kampanya/envanterden çözülür), **claim başarısız** ("Twitch envanterinden elle claim edin" + envanter bağlantısı), **claim'e hazır** (yalnız otomatik claim KAPALIYSA: drop tamamlandı, elle claim gerek), **yeniden giriş gerekli** (bir kez; sonraki başarılı girişe dek tekrarlamaz), **kampanya başladı** (yalnız motor *Boşta*yken *İzliyor*a geçince; kampanya ortasında açılışta bildirilmez).
- **Süzgeç (App):** `NotificationCoordinator` — "Bildirimler" ayarı (varsayılan AÇIK) kapalıysa göstermez (açık isteği olan "Bildirimi dene" hariç), `NotificationGate` aynı anahtarı süre dolana dek (claim: 7 gün, kampanya: 6 sa, giriş: 3 sa) tekrarlamaz, kapalıyken bastırılan olay süresini tüketmez; göstermeyi arayüz iş parçacığına taşır.
- **Gösterim:** `INotifier` (gerçek = `AppShell` → `TrayIcon.ShowBalloon`, `Shell_NotifyIcon NIF_INFO`; Windows 10/11'de bildirim olarak çıkar; pencere kapalıyken de çalışır). Balona tıklama: bağlantı varsa tarayıcıda açılır (envanter), yoksa pencere açılır. Balon gösterilemese/dinleyici hata verse motor etkilenmez.

## 5. İzleme (kredilendirme) soyutlaması

```csharp
interface IWatcher {            // bir kanalı "izleyen" bileşen
  Task StartAsync(Channel ch, CancellationToken ct);
  Task StopAsync();
  WatcherHealth Health { get; }  // Healthy / Degraded / Failed + neden
}
```
| Katman | Açıklama | Ağırlık | Durum |
|---|---|---|---|
| **Tier A — HLS** | Oynatıcı gibi playlist/segment trafiği (en düşük kalite/yalnız ses), tarayıcısız | düşük (CPU≈0, ~2 MB/dk) | **KANITLANDI (M3.1):** sunucu dakika sayacı ilerliyor (docs/PLATFORM-NOTES.md). `TwitchHlsWatcher` |
| **Tier B — Gizli oynatıcı** | WebView2 içinde gerçek oynatıcı, sessiz, 160p, pencere gizli/odak çalmaz | **yüksek (ölçüldü: ~600–830 MB RAM, %20–50 çekirdek; §2)** | **KICK'TE KULLANILAN YOL (M4-B, canlı doğrulandı: sunucu sayacı ~1 birim/dk).** Twitch'te gerekmedi. `KickWebViewWatcher` + `WebViewKickPlayerHost` (App\Player); bkz. §4.3c |
| Tier C — Yalnız telemetri | Referansların bir kısmının kullandığı hafif yöntem | en düşük | **Gerek kalmadı** (A çalıştığı için). Tier B yalnızca A ileride bozulursa yedek olarak yazılacak |

Seçim otomatik: A dene → sunucu ilerlemesi artmazsa (watchdog) B'ye düş → UI'da hangi yöntemin kullanıldığı görünür.

## 6. Kimlik doğrulama ve sırlar
- **Twitch:** Device-code akışı, **SMARTBOX_TV** istemci profiliyle (kod gösterilir, tarayıcı `twitch.tv/activate` açılır). Parola uygulamaya **hiç girmez**.
  `ANDROID_APP`/`WEB` Twitch tarafından kapatıldı, `MOBILE_WEB` GQL'de 401 veriyor (bkz. docs/PLATFORM-NOTES.md). Token'lar istemci kimliğine bağlıdır; profil değişirse oturum geçersiz sayılır.
- **Twitch kampanya keşfi (kanal üzerinden):** `ViewerDropsDashboard` bu istemcide boş döndüğü için: oyun dizini (DROPS_ENABLED) → ilk N kanalın `AvailableDrops`'ı → kimliğe göre birleştir (`TwitchCampaignDiscovery`).
  Aynı yolla "bu kampanyayı hangi kanallarda izleyebilirim" bilgisi (`ChannelsByCampaign`) gelir; planlayıcı buna dayanır.
- **Kick (M4.1, kodlandı):** TV-login — uygulama `uuid` + 6 haneli kod üretir, `kick.com/tv/login?uuid=…&code=…` tarayıcıda açılır, **kullanıcı kendi hesabıyla onaylar**; uygulama `POST kick.com/api/tv/link/authenticate/{uuid} {"key":kod}` yoklar (4 sn, 5 dk), onayda `token` döner → `kick.accessToken/userId/login` DPAPI'de (`KickSessionManager`, Twitch kalıbı: `Restored`/`NeedsLogin`/`NotLoggedIn`/`Offline` — ağ veya Cloudflare hatasında token korunur). Token doğrulama = `GET web.kick.com/api/v1/drops/progress` (kimlik hatası 401 veya Kick-JSON'lu 403). WebView2 yedeği şimdilik gerekmedi; TV-login gerçek hesapla çalışmazsa önce kullanıcıya danışılır.
- Token'lar `data\secrets.dat` içinde **DPAPI (CurrentUser)** ile şifreli; log'lara asla yazılmaz (maskeleme).
- Uygulama hesap parolası veya Steam bilgisi istemez. Steam bağlantısı kullanıcı tarafından Facepunch `/connect` sayfasında yapılır;
  uygulama bağlantı durumunu API'den okur ve "Bağlan" butonuyla sayfayı açar.

## 7. Veri / depolama (`data\` klasörü)
| Dosya | İçerik |
|---|---|
| `settings.json` | Ayarlar (dil, başlangıçta çalış, küçültülmüş başlat, otomatik claim [varsayılan kapalı], bildirimler [varsayılan açık], oyun slug'ı) |
| `secrets.dat` | DPAPI ile şifreli token'lar |
| `state.json` | Hesaba bağlı (`userId`) motor hafızası (tamamlanan kampanyalar + bekletmeler, süreli) ve son envanter önbelleği (açılışta hızlı UI); `schemaVersion` 1, atomik+`.bak` (M5.1) |
| `history.jsonl` | Claim geçmişi (tarih, drop, yayıncı, platform) |
| `hashes.json` | GQL hash kayıt defteri (kullanıcı/otomatik güncellenebilir) |
| `logs\` | Dönen log dosyaları (Serilog benzeri, 7 gün) |
| `webview2\` | Kick oynatıcısının kalıcı tarayıcı profili (çerezler; **kimlik bilgisi uygulamaya girmez/okunmaz**, yalnızca bu klasörde durur, git dışı); `player-session.json`: son bilinen oynatıcı oturumu (yalnız "açık/yok" + zaman) |

SQLite kullanmıyoruz (ek yerel kütüphane, boyut). Veri küçük; JSON + JSONL yeter.

## 8. Arayüz taslağı

```
┌ Rust Droper ───────────────────────────────────────────────  ─  □  ✕ ┐
│ [Twitch ● Bağlı: testuser]  [Kick ○ Giriş yap]        [▶ Başlat] [⏸]   │
├───────────────────────────────────────────────────────────────────────┤
│ ŞU AN: Twitch · izleniyor: TwitchRivals (https://twitch.tv/…)         │
│ Yöntem: HLS · Son ilerleme: 12 sn önce · Kampanya: Base Invaders      │
├ Genel dropler (herhangi bir Rust yayını) ─────────────────────────────┤
│ Rug          ████████████ %100  1sa/1sa   ✔ Claim edildi              │
│ Bed          ████████░░░░  %72  1sa26/2sa  kalan 34 dk                 │
│ Garage Door  ░░░░░░░░░░░░   %0  0/3sa      sırada                     │
├ Yayıncı dropları ─────────────────────────────────────────────────────┤
│ Backpack · TwitchRivals ↗  ██████░░░░ %60  18/30 dk  kalan 12 dk 🔴CANLI│
├ Toplam: 2/6 claim edildi · Tahmini bitiş: bugün 23:40 ────────────────┤
│ Günlük (son olaylar)  ·  Ayarlar  ·  Tanılama                          │
└───────────────────────────────────────────────────────────────────────┘
Tepsi: sol tık = pencereyi aç/gizle · sağ tık = Başlat/Duraklat/Çıkış · balon: claim bildirimi
```
Gerekli alanlar (talep): yayıncı adı, yayıncı linki, % izlenme, izlenen süre, kalan süre → hepsi kartta.

## 9. Test stratejisi (kararlılığın temeli)
1. **Birim testleri:** uygunluk hesabı, planlayıcı, watchdog, atomik depolama, DTO toleransı.
2. **Sözleşme/fixture testleri:** gerçek Kick JSON (bugün kaydedilen) + Twitch kayıtlı cevaplar; alan eksik/ekstra/null varyantları.
3. **FakePlatform:** sahte Twitch+Kick sunucusuyla uçtan uca (drop ilerler, takılır, kanal kapanır, 401, hash bozulur) — sunucu olmadan 24 saatlik senaryolar saniyelerde.
4. **Canlı smoke (kullanıcı hesabıyla, kullanıcı giriş yapar):** herhangi aktif kampanyada ilerleme + claim; Rust kampanyasında saha testi.
5. **Soak:** 24 saat sürekli çalışma, bellek/handle sızıntısı kontrolü (M5).

## 10. Yayın
Depo MIT lisanslıdır; `.github/workflows/ci.yml` her push'ta derler + test eder, `release.yml` `v*` etiketinde portable ZIP + SHA256 üretir. Ayrıntı: `CONTRIBUTING.md`.

## 4.7 Dayanıklılık: çökme koruması, yabancı oturum, sağlık kontrolü (2026-10-19)
- **Çökme koruması (App):** `data\run-state.json` = `{running, cleanExit}`. Motor çalışırken `running=true,cleanExit=false`; Duraklat → `false,true`; tepsi Çıkış → `false,true`; **Windows kapanması** (`SessionEnding`) durumu DONDURUR (açılışta devam). Motor çalışırken `schtasks` ile kullanıcıya özel, yükseltmesiz **`RustDroperKeepAlive`** görevi (5 dk, `RustDroper.exe --keepalive`) kurulur; Duraklat/Çıkış'ta silinir. `--keepalive` ile açılan örnek: çalışan örnek varsa sessizce çıkar (pencereyi öne getirmez); önceki çalışma beklenmedik bittiyse tepsiye küçülü açıp motoru kendiliğinden başlatır (`MainViewModel.ResumeAsync`: giriş görünene dek ≤5 dk bekler, Başlat uyarısı göstermez); aksi halde hemen çıkar. Ölümcül yönetilen istisnada ek olarak 6 sn gecikmeli yeniden açma. Ayar `AutoRestart` (varsayılan AÇIK). `RUSTDROPER_DATA` verilmişse (geliştirme/sınama) görev KURULMAZ. Görev Yöneticisi'nden sonlandırma da "beklenmedik" sayılır (en geç 5 dk'da geri gelir); istenmiyorsa önce Duraklat/Çıkış.
- **Yabancı oturum (Core `DropEngine`):** ilerleme AKARKEN durursa (aynı hesapla başka yerde izleme olasılığı) kanal hemen engellenmez; aynı kanalda izleme BİR kez yeniden başlatılır (`_reclaimed`: kanal→dakika), dakika gerçekten artınca hak yenilenir; yeniden başlatmadan sonra da ilerlemezse normal engelleme. İlerleme hiç gelmemiş kanalda eski davranış (engelle). docs/06 §1.
- **Sağlık kontrolü (App):** `HealthChecker` (saf; İnternet, giriş, Facepunch/Steam [Twitch] / Oynatıcı oturumu [Kick], kampanya keşfi, motor, otomatik claim, uyku engelleme, çökme koruması, Windows ile başlat) → başlıktaki nabız düğmesi; her platform ekranında yalnız kendi maddeleri.

## 4.8 Çok dilli arayüz: Türkçe + İngilizce (2026-10-20)
**Karar: kod içi tablo + `Loc` (ek paket yok, satellite assembly yok).** Kullanıcıya görünen metinler `RustDroper.Core.Localization.Loc`'tan gelir:
`Loc.T("anahtar", args…)` → geçerli dilde `string.Format` (kültür: tr-TR / en-US); çoğul gerekiyorsa `Loc.Plural("anahtar", n, args…)` (`.one`/`.other`).
Tablolar `CoreStrings.*.cs` (Core: motor notları, bildirimler, hata iletileri, izleyici nedenleri) ve `AppStrings.*.cs` (App: ayarlar, pano, kartlar, Sağlık, Hakkında) içindedir; her girdi `(anahtar, Türkçe, İngilizce)`.
- **Neden böyle (ölçülü gerekçe):** (1) *Tek dosya yayın:* `.resx` + uydu derlemeler (`tr/`, `en/` klasörleri) tek dosya yayında ek dosya/çıkarma sorunu çıkarır; `SatelliteResourceLanguages=en` zaten ayarlı. Kod içi tablo ana derlemede, ek dosya yok. (2) *Bellek/başlangıç:* ResourceManager/JSON çözümleme yok; tablo ~410 girdilik statik dizi (Core+App, ≈60 KB kaynak metin). Tepsi ölçümü: yeni derleme **61,0 MB** (+10 sn) / **65,4 MB** (8 aç/kapa sonrası) — dil öncesi yayın **67,0 / 64,3 MB** (aynı makine/komut): fark ölçüm gürültüsü içinde (hedef ≤68 MB sağlandı). Pencere kapalıyken ek zamanlayıcı/çizim yok. (3) *Türkçe değişmeden:* kaynak dil Türkçe; metinler eskisiyle birebir, mevcut Türkçe-assert testleri bozulmadı. (4) *Derleme güvencesi:* test, tablodaki her anahtarın iki dilde dolu ve yer tutucu sayılarının eşit olduğunu, İngilizce metinde Türkçe harf kalmadığını ve kaynaktaki her `Loc.T("…")` / `{loc:Loc …}` anahtarının tabloda bulunduğunu doğrular (`LocalizationTests`).
- **Dil seçimi:** `AppSettings.Language` = `"auto"` | `"tr"` | `"en"`. Yeni kurulum varsayılanı `auto`: Windows görüntü dili (`CultureInfo.CurrentUICulture` süreç başında) Türkçe ise Türkçe, değilse İngilizce. Eski ayar dosyaları: `"tr"` korunur; `language` alanı hiç yoksa (çok eski dosya) Türkçe; tanınmayan değer `auto`. Ayarlar → **Dil / Language**: *Otomatik (Windows)* · *Türkçe* · *English* (radyo düğmeleri).
- **Anında geçiş (yeniden başlatma gerekmez):** `Loc.Configure(...)` dili değiştirir ve `Loc.LanguageChanged` olayını tetikler. XAML metinleri `{loc:Loc anahtar}` işaretleme uzantısıdır (`LocExtension`): `LocSource` tekil nesnesinin indeksleyicisine tek yönlü bağlama kurar; dil değişince `Item[]` bildirimi tüm metinleri yeniden okutur (zayıf bağlama dinleyicileri; pencere kapalıyken maliyet yok). Görünüm modelleri (`ViewModelBase.ObserveLanguage`, zayıf abonelik) `OnLanguageChanged`'de yeniden üretir: `MainViewModel` → `Apply()` (pano, kartlar, tepsi ipucu, uyarı şeridi, Sağlık), `PlatformCardViewModel` son hesap/oynatıcı verisiyle, `AboutViewModel`/`SettingsViewModel` tüm özellikleri yeniler. Tepsi menüsü her açılışta güncel dille kurulur.
- **Sınırlar (bilinen):** (a) Motorun *geçmiş* "Son olaylar" notları ve bir kez üretilmiş `Engine.Message`/hata iletileri üretildikleri dilde kalır (yeni olaylar yeni dilde gelir); günlük (log) satırları ve CLI çıktısı Türkçe kalır (kapsam dışı). (b) Günlük/CLI dışında kalan tek Türkçe: dosya adları/yollar, `docs/`.
- **Test yalıtımı:** geçerli dil süreç geneli bir değerdir; testler `Loc.Use("en")` ile YALNIZ kendi eşzamansız akışları için dili ezer (`AsyncLocal`, paralel testleri etkilemez). Küresel dili gerçekten değiştiren testler `[Collection("Loc global")]` (paralelleştirme kapalı) içindedir ve sonunda Türkçeye döner. Varsayılan test dili Türkçedir (küresel durum açılışta `tr`).
- **Biçimler:** tarih/saat `fmt.date.*` anahtarlarıyla (`23 Eki 21:00` ↔ `Oct 23, 21:00`), süre `fmt.*` (`3 gün 9 sa` ↔ `3 days 9 h`), yüzde `%37` ↔ `37%`. Tepsi ipucu ≤127 karakter sınırı iki dilde korunur (test).
- **Twitch/Kick ayrımı:** her platformun ekranı yalnız kendi platformundan söz eder (Sağlık, kart metinleri, bildirimler) — İngilizce metinlerde de; test (`Health_InEnglish_KeepsPlatformSeparation`).
