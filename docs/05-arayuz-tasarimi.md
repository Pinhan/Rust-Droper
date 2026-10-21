# 05 — Arayüz tasarımı (iki ekranlı pano)

> Kapsam: yalnız `src\RustDroper.App` (XAML, Themes, görünüm ViewModel'leri) + `AppSettings.LastPlatform`. İş mantığı (Core, motor, servisler, oynatıcı akışı) değişmedi.

## 1. Bilgi mimarisi

Twitch ve Kick drop etkinlikleri aynı anda olmaz → pencere **iki ayrı ekrana** bölündü. Üstte iOS tarzı kayan göstergeli **Twitch ⇄ Kick geçişi** var; seçili ekranda yalnız o platformun içeriği görünür.

```
┌──────────────────────────────────────────────────────────────┐
│ ◆ Rust Droper                       [▶ Başlat] [⟳] [≡ Ayarlar] │  başlık (ortak, platformdan bağımsız)
│              ╔═════════════╤═════════════╗                    │
│              ║ ⬛ Twitch    │   ⬛ Kick   ║  ← kayan gösterge   │  geçiş (segmented toggle)
│              ╚═════════════╧═════════════╝                    │
├──────────────────────────────────────────────────────────────┤
│  PLATFORM EKRANI (seçiliye ait; geçişte kayma + solma)         │
│   · Platform kartı (durum, giriş/çıkış, kod/QR, oynatıcı oturumu)│
│   · Uyarı şeridi (YALNIZ bu platformun uyarısı)                  │
│   · "Otomatik claim KAPALI" (bu platform izliyorsa)              │
│   · ŞU AN · Genel drop'lar · Yayıncı drop'ları                   │
│   · Claim edilenler + Facepunch/Steam · Son olaylar              │
└──────────────────────────────────────────────────────────────┘
```

Kurallar:
- **Yalıtım:** Twitch ekranında Kick'e, Kick ekranında Twitch'e ait kart/rozet/uyarı/liste/olay/şerit/ayrıntı YOK. Ekran verisi `MainViewModel.Apply` içinde **yalnız seçili servisin** görüntüsünden türer; `Notice` (uyarı şeridi), komut hataları ve "Otomatik claim KAPALI" uyarısı platform başına tutulur.
- **Tek istisna:** diğer platformda dikkat gerektiren durum (hata/çevrimdışı/oturum reddi/oynatıcı oturumu yok/giriş gerekli) varsa geçişteki diğer segmentin köşesinde **metinsiz nötr nokta** görünür. Ayrıntı yalnız o platformun ekranında.
- **Ortak:** Başlat/Duraklat iki platformu birlikte yönetir (Kick'te yalnız Kick girişliyse Kick); Yenile ikisini yeniler; Ayarlar bir sayfadır (geçiş görünür kalır; platform seçmek Ayarlar'ı kapatır). Tepsi ipucu değişmedi (iki platformlu).
- **Kalıcılık:** seçili sekme `data\settings.json` → `lastPlatform` ("Twitch"/"Kick"; alan yoksa/bozuksa Twitch → eski ayar dosyalarıyla uyumlu). Kick servisi yoksa geçiş gizlenir.

## 2. Renkler

| Anlam | Renk |
|---|---|
| Zemin / panel / panel-alt / kenar | `#14161A` / `#1D2025` / `#252930` / `#2F343C` (değişmedi) |
| Rust (Başlat düğmesi, uygulama imi) | `#CD412B` (hover `#E2573F`) |
| **Twitch** (kendi çizimimiz, resmî logo yok) | mor `#9146FF` |
| **Kick** (kendi çizimimiz, resmî logo yok) | yeşil `#53FC18` (metin üstünde koyu `#0B0E0A`) |
| Durum | Ok `#4CC38A`, Uyarı `#E5A93D`, Hata `#E5604D`, Bilgi `#5AA9E6` |

Platform vurgusu tek bir `PlatformAccentBrush` kaynağıdır (pencere kaynağında). İlerleme çubukları, geçiş göstergesinin kenarı, bölüm başlığı ikonları ve kart şeridi buna `DynamicResource` ile bağlıdır. Platform değişince renk 260 ms'de mordan yeşile akar: şablonlar paylaşılan fırçayı **dondurduğu** için fırça üzerinde animasyon çalışmaz (denendi, `InvalidOperationException`); bunun yerine geçiş süresince her karede yeni donmuş fırça konur (`CompositionTarget.Rendering`; geçiş bitince abonelik kalkar → boştayken hiçbir şey çalışmaz).

Geçişteki segmentler: Twitch/Kick işaretleri her zaman kendi marka renginde (seçili değilken %60 soluk); seçili segmentin etiketi beyaz, diğeri soluk gri. Gösterge nötr koyu zemin + platform renginde 1,5 px kenar (Kick'in parlak yeşili üzerinde okunaklı metin sorunu olmasın diye).

## 3. İkon seti

Ek paket / resmî logo dosyası yok. `Themes\Icons.xaml` içinde 24×24 ızgarada çizilmiş `Geometry` kaynakları (çizgi tabanlı, Feather benzeri, kendi çizimimiz) + `Views\Icon` denetimi (`Data`, `Size`, `Foreground`; çizgi ya da dolgu). Twitch ve Kick simgeleri **kendi çizdiğimiz basit işaretler** (konuşma balonu + iki çubuk; blok "K"). İkonlar: oynat, duraklat, yenile, ayarlar (sliders), geri, tamam, giriş, çıkış, dış bağlantı, kopyala, uyarı, hediye, saat/olay listesi, aktivite (ŞU AN), ödül, kullanıcı, iptal, damla (uygulama imi).

## 4. Animasyonlar

Hepsi **yalnız pencere açıkken ve etkileşimde** çalışır; pencere kapanınca yok edilir (tepside çizim/zamanlayıcı yok). Sürekli çalışan tek animasyon, izleme sürerken küçük bir canlılık noktasının nabzıdır (10 kare/sn, tek 9 px elips).

| Animasyon | Süre | Tetik |
|---|---|---|
| Geçiş göstergesinin kayması + renk (mor⇄yeşil) | 260 ms, CubicEase | Platform değişince |
| Ekran kayması (±24 px) + solma | 220 ms | Platform / Ayarlar değişince |
| İlerleme çubuğu dolması (ŞU AN) | 600 ms, CubicEase | Değer değişince (ilk yüklemede animasyonsuz) |
| Düğme hover (üst katman solma), basılı (%97 ölçek + koyulaşma) | 120 / 80 ms | Fare |
| Durum noktası nabzı | 1,2 sn, 10 kare/sn | Yalnız izlenirken / giriş beklenirken |
| Vurgu renginin mor⇄yeşil akması | 260 ms | Platform değişince |
| Ayarlar anahtarının (iOS tarzı) topuzu kayması | 160 ms | Anahtar tıklanınca |

## 5. Bellek / performans bütçesi

- Yeni kaynak: ~25 statik `Geometry`, birkaç stil; resim/yazı tipi/paket yok. Hedef ek özel bellek **< 1 MB**; tepsi hedefi 52–68 MB korunur (yazılım çizimi `SoftwareOnly` AÇIK kalır).
- Pencere kapalıyken: hiçbir animasyon/zamanlayıcı yok; `Dashboard` zaten kurulmuyor.
- Animasyonlar `BeginAnimation` ile kısa ömürlüdür (tamamlanınca kaldırılır); `DispatcherTimer` eklenmedi.
- Yazılım çizimi nedeniyle büyük alanda sürekli animasyon yok: ekran geçişi 220 ms, sonra durağan.

## 6. Doğrulama
- **ViewModel testleri** (`tests\RustDroper.App.Tests\PlatformScreenTests.cs`): her ekranın metin dökümünde diğer platform adı yok (iki platform çalışırken, olaylarla); Kick hatası Twitch ekranında şerit/metin üretmez, yalnız `KickAttentionDot`; tersi; seçim kalıcı/geri yüklenir (Kick servisi yoksa yok sayılır); Ayarlar'dayken platform seçmek Ayarlar'ı kapatır; `IsRunning`; Kick'te yöntem "Gizli oynatıcı". Çekirdek: `SettingsTests` (`LastPlatform` normalleştirme, eski dosya uyumu, kalıcılık).
- **Görsel önizleme** (isteğe bağlı, ağ/hesap yok): `RUSTDROPER_PREVIEW_DIR=<klasör>` verip `dotnet test tests/RustDroper.App.Tests --filter Category=VisualPreview` — gerçek `MainWindow`'u ekran dışında sahte servislerle açıp PNG yazar (iki ekran, geçiş kareleri, ayarlar, giriş panelleri, dikkat noktası). Çıktılar: `docs\ekran-goruntuleri\01–08`.
- **Gerçek uygulama** (geçici veri klasörü): `powershell -File build\ui-screens.ps1 -Exe <RustDroper.exe> -OutDir <klasör>` — PrintWindow + UI Automation; çıktılar `10–13`.

## 7. Drop kart ızgarası (görsel kartlar)

**Amaç:** "Genel droplar" ve "Yayıncı droplarını" satır listesi yerine 5 sütunluk görsel kartlar; her kart drop'un resmini gösterir. Etkinlik başlamasa da kartlar görünür.

**Veri akışı (Core `Media`, iş mantığına dokunmadan):**
1. `FacepunchPageParser`: `twitch.facepunch.com` HTML'inden her `drop-box` için ad (`drop-type`), süre ("1 Hour" → 60 dk), JPG/MP4 adresi (`<video><source>/<img>`), yayıncı bölümünde yayıncı adı + bağlantı. Toleranslı (bozuk/boş sayfa → boş liste, istisna yok). Gerçek sayfadan alınmış fixture: `tests\RustDroper.Core.Tests\Fixtures\facepunch-twitch-drops.html` (kişisel veri yok; 5 genel + 1 yayıncı [Backpack/TwitchRivals]). `kick.facepunch.com` şu an görsel içermiyor → Kick için katalog yok.
2. `DropMediaCache`: `%LOCALAPPDATA%\RustDroper\media` (`AppPaths.MediaDirectory`, ~20 MB sınır, en eski dosyalar silinir). **Yalnız HTTPS** + izinli alan adları (`files.facepunch.com`, `ext.cdn.kick.com`, `files.kick.com`; yönlendirme sonrası da denetlenir), dosya başına ≤4 MB, içerik türü + dosya imzası (JPEG/PNG/WEBP/MP4) denetimi, `.part` + atomik taşıma. Ağ yoksa/başarısızsa `null` → yer tutucu ikon.
3. `FacepunchMediaService : IDropMediaProvider`: açılışta + 6 saatte bir (hata olursa 15 dk) sayfayı çeker, kataloğu yayımlar (kartlar önce yer tutucuyla), görselleri 3 paralel indirir, `Changed` olayı → pano yeniden kurulur. Eski katalog hata olunca korunur. Sayfa 2 MB üstü/başka alan adına yönlenirse reddedilir.
4. `DashboardViewModel(media)`: ham veriden bağımsız. **Kampanya varsa** ödül adı `DropNames.Key` (küçük harf, aksansız, yalnız harf/rakam: "Large Wood Box" = "large-wood-box") ile katalogdan eşleşir → görsel; eşleşmezse yer tutucu. **Kampanya yoksa** (genel/yayıncı ayrı ayrı) katalogdan "Etkinlik bekleniyor" kartları (ilerleme 0). **Kick:** Twitch kataloğu asla gösterilmez; görsel `reward.image_url` (önbelleğe indirilir, 10 dk'da bir yeniden denenir), yoksa yer tutucu.
5. Kartlar **yerinde güncellenir** (`DropRow`/`CampaignItem` kalıcı, `Reconcile`): öğeler yeniden oluşmaz → ilerleme çubuğu akar, görsel yeniden yüklenmez.

**Kart (`Views\DropCard`):** kare görsel (ImageBrush; 240 px'e küçültülerek çözülür), üstte durum ikonu (sol) + yüzde (sağ), alt kenarda degrade üzerinde animasyonlu ilerleme çubuğu; altta "Ad · süre", durum metni, kalan süre; yayıncı drop'unda ek satır (yayıncı adı ↗ + CANLI noktası). Durumlar: **Sırada** (kilit, soluk) · **Etkinlik bekleniyor** (saat, soluk) · **Devam ediyor** (saat) · **İzleniyor** (nabızlı nokta, platform renginde kenar) · **Claim'e hazır** (hediye ikonu, amber kenar + yarı saydam dış halka = hafif parıltı, amber çubuk) · **Claim edildi** (tik, yeşil çubuk/kenar). Aynı kart genel ve yayıncı drop'larında kullanılır; 5 sütun `UniformGrid`.

**Oynatma kararı: MP4 oynatılmıyor (yalnız JPG + hover yakınlaştırma).** `MediaElement` ile ölçüm (`build\media-probe.ps1`, gerçek uygulama, Debug, anonim): bir kartta hover → özel bellek **76,6 → 176,6 MB** (+100 MB), iki kartta **275 MB**, CPU **%40 bir çekirdek**; video bırakıldıktan sonra bile 224 MB kaldı (pencere kapanınca 72 MB). Hedef olan 52–68 MB'ı bozduğu için kart başına video kaldırıldı. Yerine: hover'da JPG %9 yakınlaşır (350 ms, tek seferlik; `RelativeTransform`), izlenen kartta nabızlı nokta. Çözümü yeniden denemek isteyen: `DropMedia.VideoUrl` hâlâ ayrıştırılıyor ve `EnsureAsync` MP4'ü indirebilir; yalnız oynatıcı (gizli WebView2 değil, ölçülerek seçilecek hafif bir yol) eksik.

**Bellek/CPU (Release portable, oturumsuz, geçici veri; `build\measure-memory.ps1 -Fast -Cycles 8`):** tepside **61,0 → 64,8 MB** (önceki tasarım turu: 65,2 → 62,6), pencere açık **73,8 MB** (önceki 67,7: ≈ +6 MB, 6 kartın çözülmüş görselleri; pencere kapanınca geri verilir). Kartlar üzerinde gezinme (`media-probe`): özel bellek **78 → 81,5 → 77 MB**, CPU %2–3, pencere kapalıyken **61,4 MB**. Tepsideki kalıcı ek yük: yalnız 6 saatlik zamanlayıcı + küçük HttpClient (görseller diske yazılır, bellekte tutulmaz).

**Ekran görüntüleri:** `20` (sahte veriyle tüm durumlar + yayıncı kartı), `21` (Kick, görselsiz yer tutucu), `22` (gerçek uygulama, geçici veri klasörü, **gerçek Facepunch sayfasından canlı çekim**), `23` (gerçek uygulamada kart üstüne gelme).

## 8. Claim edilen ödül kartları

"CLAIM EDİLEN ÖDÜLLER": üstte Facepunch/Steam bağlantı durumu satırı, altında **son 5** claim edilen ödül görsel kart olarak (`Views\ClaimedCard`, drop ızgarasıyla aynı 5 sütunluk `UniformGrid`; hizalı). Devamı "… ve N ödül daha" metni. Kart: görsel (5:4; yoksa ödül ikonu), sol üstte yeşil tik, ad, **kaynak**, "×N · 22 Eki 2026" (sayı yalnız >1; tarih biliniyorsa — Kick'te tarih yok → satır gizli). Hover'da görsel kısa yakınlaşır (drop kartlarıyla aynı).

- **Sıralama:** yeni → eski (`LastAwardedAt`; tarihsizler sonda), `Id`'ye göre tekil. Kartlar kalıcı (`ClaimedItem` + `Reconcile`): güncellemede yeniden oluşmaz.
- **Kaynak satırı** (yayıncı adı Twitch envanterinde YOK): `ClaimedReward.GameName` (Twitch `gameEventDrops[].game.displayName`; Rust ödüllerinde null) → yoksa `AccountLinkUrl` host'u `*facepunch.com` ise "Rust · Facepunch" → yoksa platform adı ("Twitch"/"Kick").
- **Görsel:** Twitch `gameEventDrops[].imageURL` (`static-cdn.jtvnw.net`), Kick ödül `image_url` (`ext.cdn.kick.com`). Facepunch medya önbelleğiyle aynı disk önbelleği (`%LOCALAPPDATA%\RustDroper\media`, ≤20 MB); **yalnız gösterilen 5 kartın** görseli indirilir; izin listesine yalnız `static-cdn.jtvnw.net` eklendi (HTTPS; Kick ödül görselleri zaten listedeydi: gerçek kamusal kampanya fixture'ında `ext.cdn.kick.com/drops/reward-image/…png`). Başarısız/ağ yok → yer tutucu ikon; 10 dk'da bir yeniden denenir.
- **Yalıtım:** her ekran yalnız kendi servisinin envanterini gösterir (Twitch ekranında Kick ödülü, Kick ekranında Twitch ödülü yok; testli).
- **Bellek:** Release portable, oturumsuz (kart yok): tepside 65,8 → 64,1 MB, pencere açık 76,6 MB. 5 çözülmüş (240 px) görsel ≈ 1–1,5 MB ek; gerçek hesapla ölçülmedi.
- **Açık / sonraki iş:** uygulamanın kendi claim ettiği ödüllerde yayıncı/kanal adı kaydedilmiyor (`history.jsonl`'da yalnız channelId var) → "claim kaydına kanal adı ekle", sonra kaynak satırı yayıncı adını gösterebilir.
- Ekran görüntüleri (sahte ad/tarih; görseller yalnız önizleme varlıkları): `24-claim-kartlari-twitch.png`, `25-claim-kartlari-kick.png`.

## 9. Küçük düzeltmeler (dilim A)

- **Başlık çubuğu:** Twitch ⇄ Kick geçişi logo ile Başlat arasındaki ortada (dikey alan kazanıldı); "Rust drop toplayıcı" alt yazısı kaldırıldı.
- **Boş-durum:** belirsiz "Henüz kampanya bilgisi yok" kaldırıldı (toplam satırı yalnız gerçek kampanya varken). Her bölümün tek metni: "Aktif genel kampanya yok." / "Aktif yayıncı kampanyası yok." / "Henüz claim edilmiş ödül yok." / "Henüz olay yok.". Katalog (Facepunch) kartlarının üstündeki "Rust drop'ları" ara başlığı silindi (yalnız "Etkinlik bekleniyor" durumu kaldı).
- **Facepunch/Steam bağlantısı** platform kartına taşındı: "Bağlı: kullanıcı" satırının altında renkli daire + "Facepunch/Steam: bağlı" (yeşil) / "bağlı değil" (kırmızı + "Hesabı bağla ↗") / "bilinmiyor" (gri). Kick'te yalnız Kick'in kendi bilgisi (`user_app_connected`).
- **Claim edilen ödüller:** "… ve N ödül daha" başlığın yanında; bölüm sönük (opaklık %62 + gri kaplama), hover'da canlanır.
- **Başlık yanı sayaçları:** "GENEL DROPLAR · 5 Drop", "YAYINCI DROPLARI · 1 Drop".
- **Site düğmesi:** her platform kartında küçük ikonlu düğme → Twitch: https://twitch.facepunch.com/ , Kick: https://kick.facepunch.com/ (yalnız bu iki sabit adres, kendi ekranında).
- **Bilgi satırı** (ŞU AN kartı, sönük): "Son kontrol HH:mm · motor ~60 sn'de bir ilerlemeyi, ~10 dk'da bir kampanyaları, katalog 6 saatte bir yeniler"; ipucu tüm aralıkları verir (PollInterval 60 sn, RediscoverInterval 10 dk, IdleRecheck 5 dk, envanter 5 dk, Facepunch 6 sa). "Son kontrol" = motorun son ilerleme/olay zamanı (çalışmıyorsa saat gösterilmez).
- Ekran görüntüleri: `30-baslik-ve-duzeltmeler-twitch.png`, `31-baglanti-yok-katalog-kartlari.png` (sahte veri).

## 10. Etkinlik bilgisi, bağlantı satırı, bitmiş etkinlik (dilim B)

**Platform kartının ortası = etkinlik bilgisi** (ayrı satır açılmadı): durum rozeti (Yakında / Aktif / Bitti) + etkinlik adı + "↗ Siteyi aç"; altında ayrıntı ("Twitch Drops · 54 · Rustoria sunar"), yerel saatte tarihler ("23 Eki 21:00 → 2 Kas 02:59") ve "Başlamasına 4 gün 3 sa" / "Bitmesine 3 gün 5 sa" (son gün: "7 sa 5 dk"; saniye yok). Kalan süre **dakikada bir** hesaplanır (pencere açıkken mevcut 1 sn'lik zamanlayıcı yalnız dakika değişince iş yapar; ek zamanlayıcı yok). Kartın sağında yalnız "Çıkış yap"/"İptal"/… kaldı; site düğmesi (Twitch → twitch.facepunch.com, Kick → kick.facepunch.com; yalnız bu iki sabit adres) etkinlik adının yanında.

**Veri (öncelik):** gerçek kampanya (ad, `startAt`/`endAt`; genel > yayıncı, aktif > yaklaşan > son biten) → Facepunch sayfası. Ayrıntı satırı yalnız Facepunch bloğundan gelir ve kampanya adı eşleşiyorsa (ya da kampanya yoksa) gösterilir. Durum **tarihlerden hesaplanır**; sayfadaki rozet sınıfı (`is-pending`...) yalnız tarih yoksa yedektir. Hiçbir veri yoksa alan boş (hata yok).

**`FacepunchPageParser.ParseEvents`:** `campaign-N` bloklarından `event-title`, `event-round` (birden çok), `event-subtitle` ("Hosted by X" → "X sunar"), rozet sınıfı, tarihler: `data-date-id="<hash>-<UNIX_SN>"` (öncelik) → İngilizce tarih metni (UTC) → `setupCountdown('.campaign-N', a, b)` ms (iki sıfırdan farklı değer: küçük = başlangıç, büyük = bitiş; tek değer = başlangıç). Birden çok blok: aktif > yaklaşan (en yakın) > en son biten. Gerçek sayfa fixture'ları: `facepunch-twitch-event.html`, `facepunch-kick-page.html`.

**Kick analizi (kick.facepunch.com, 2026-10-19):** sayfada `drop-box` yok, etkinlik adı/rozeti yok; yalnız `setupCountdown('.campaign-0', 1781208000000, 1782082800000)` (11–21 Haziran 2026, bitmiş) → Kick için ayrıştırıcı aynı, ama kart/başlık verisi **Kick API kampanyalarından** (ad, `starts_at`/`ends_at`, `image_url`) gelir; API kampanyası yoksa sayfanın geri sayım tarihleriyle "Kick drop etkinliği — Bitti" gösterilir. Kick'te Facepunch sayfasında `drop-box` çıkarsa katalog kartları da otomatik gelir (servis Kick sayfasını da 6 saatte bir okur; Twitch verisi Kick ekranında asla görünmez).

**Etkinlik bitince (madde 12):** Facepunch sayfası eski drop'ları göstermeye devam ederse katalog kartları **gizlenmez**; "Etkinlik bitti" durumunda soluk gösterilir (`DropState.Ended`: saat ikonu + loş kaplama; başlıkta "Etkinlik bitti"). Seçim gerekçesi: kullanıcı hangi drop'ların olduğunu yine görebilir, ama alınabilir sanılmaz. Claim edilen ödüller her durumda kalıcıdır (envanterden).

**Bağlantı satırı (E1):** "Bağlı: kullanıcı" ile aynı biçim — yeşil daire + "Bağlı: Facepunch/Steam Hesabı"; kırmızı daire + "Bağlı değil: Facepunch/Steam Hesabı" (+ "Hesabı bağla ↗"); gri "Facepunch/Steam: bilinmiyor". Kick kartında Kick'in kendi bağlantısı.

## 11. Yayıncı drop'ları öncelik sırası (dilim C)

**Motor (Core):** `DropPlanner` (saf) — sıra: yayıncı kampanyaları (kullanıcının öncelik listesi `PlannerInput.StreamerPriority`; listede olmayanlar sona, kendi içinde bitiş/kalan süre sırasıyla) → genel kampanyalar (sıralanmaz). Çevrimdışı (canlı kanalı olmayan/engelli), tamamlanmış, claim edilmiş ya da bitişe yetişemeyen kampanya **atlanır** → sıradaki ilk uygun seçilir; hiçbir yayıncı yoksa genel drop (Rust kategorisinin en kalabalık canlı yayını) izlenir.
- **Çok yayıncılı drop (C1):** bir kampanyaya atanan 2–3 yayıncıdan herhangi biri geçerlidir; planlayıcı çevrimiçi/engelsiz olanlardan en kalabalığı seçer. **Mevcut davranış zaten buydu; testle kanıtlandı:** izlenen kanal çevrimiçi kaldıkça değişmez (aynı kampanyada daha kalabalık kanal çıksa da geçilmez), kapanınca aynı drop'un diğer yayıncısına geçilir (hata/uyarı yok), hepsi kapalıysa kampanya atlanır, biri geri gelince dönülür.
- **İzleme sürerken sıra değişirse:** motor her yoklamada (≤60 sn) öncelik listesinin imzasına bakar; değiştiyse hemen yeniden değerlendirir. Geçiş yalnız (a) genel kampanyadayken bir yayıncı kampanyası için ya da (b) yayıncı kampanyasındayken sırada **daha öncelikli** bir yayıncı kampanyası için, ve mevcut drop %90'a varmadıysa yapılır; aksi halde kesintisiz izleme sürer (Grace/stall kuralları korunur; gereksiz kanal oynaması yok). Daha öncelikli olan çevrimdışıysa mevcut izleme bozulmaz.
- **Ayar:** `AppSettings.TwitchStreamerPriority` / `KickStreamerPriority` (kampanya kimlikleri; eski ayar dosyalarında yoksa boş; yinelenen/boş kimlikler temizlenir). `EngineOptions.StreamerPriority` her planlamada ayarlardan canlı okunur (`DropperService` varsayılanı).

**Arayüz:** yayıncı drop'ları artık tek 5 sütunluk ızgarada (`DashboardViewModel.StreamerCards`; kartlar kampanya sırasıyla). Kartın üstündeki **tutamaç** (⠿) tutulup sürüklenir: kart imleci izler, diğer kartlar yeni yerlerine 150 ms'de kayar, bırakınca `MoveStreamerCard` yeni sırayı kaydeder (aynı kampanyanın kartları birlikte gider; şu an görünmeyen kampanyaların kayıtlı sırası silinmez). Sürükleme yalnız tutamaçtan başlar (yayıncı bağlantısı vb. tıklamalar etkilenmez); Escape/yakalama kaybı iptal eder. `CardDrag` (kendi fare yakalama, ek paket yok), geometri `GridMath` (saf, testli). Sıra platform başına ayrı (Twitch/Kick ekranları kendi listesini gösterir/kaydeder).

Ekran görüntüleri: `26-yayinci-karti-siralama-oncesi.png`, `26-yayinci-karti-surukleme.png` (sürükleme ortası), `27-yayinci-karti-siralama-sonrasi.png`.

## 12. Başlat uyarısı (dilim D)

Kullanıcı **Başlat**'a bastığında (Duraklat'ta değil), **yalnız ana pencere açıkken** kısa, modal, karanlık temalı bir bilgi penceresi çıkar: "Rust Droper çalışırken aynı Twitch/Kick hesabıyla tarayıcıdan ya da uygulamadan başka bir canlı yayın izlemeyin; drop ilerlemesi bozulabilir." + [Tamam] + "Bir daha gösterme" anahtarı. Pencere kapatılırsa/Esc de Tamam sayılır (motor yine başlar; yalnız bilgilendirme). Tepsi menüsünden Başlat'ta (pencere görünmüyorsa) gösterilmez. Açılışta 200 ms solma + büyüme animasyonu; animasyon/zamanlayıcı kalmaz.
- **Ayar:** `AppSettings.HideStartNotice` ("Bir daha gösterme" kalıcı; eski dosyalarda yok → gösterilir). Ayarlar sayfasında "Başlat uyarısını göster" anahtarı ile geri açılabilir.
- **Soyutlama:** `IStartNoticePrompt` (`MainViewModel(..., startNotice:)`); uygulamada `WpfStartNoticePrompt` (`StartNoticeWindow`, ana pencereye sahiplenmiş modal), testte sahte. İçerik `StartNoticeView` (pencere ve önizleme aynı içeriği kullanır).
- Ekran görüntüsü: `28-baslat-uyarisi.png`.

## 13. Notlar (tur 4)

- `DashboardViewModel.Update` artık kilitli: sahte (anında) dispatcher'larla farklı iş parçacıklarından gelen eşzamanlı güncellemelerde koleksiyon yarışı (`ArgumentOutOfRange`) görülmüştü (üretimde arayüz iş parçacığı seri çağırır).
