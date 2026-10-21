# Rust Droper — Kullanım Kılavuzu

[English](USAGE.md)

Rust oyunu için Twitch (ve Kick) **drop**'larını arka planda, sistem tepsisinden otomatik toplar.
Yalnızca **Rust** kapsamındadır. Kullanım riski kullanıcıya aittir (bkz. en alttaki uyarı).

## 1. Başlatma
- [Releases](../../../releases/latest) sayfasından `RustDroper-<sürüm>-win-x64.zip` dosyasını indirip çıkarın ve **`RustDroper.exe`**'yi çift tıklayın. Kurulum yok; .NET gerekmez (tek dosya, ~125 MB). İndirmeyi `SHA256SUMS.txt` ile doğrulayabilirsiniz (README'deki SmartScreen notuna bakın).
- Veriler **`%LOCALAPPDATA%\RustDroper`** (gizli klasör) içinde tutulur: giriş (DPAPI ile şifreli, Windows kullanıcınıza bağlı), ayarlar, günlükler, Kick oynatıcı oturumu. exe'nin yanında hiçbir dosya oluşmaz; exe'yi başka klasöre kopyalasanız da giriş korunur. Kayıt defterine yazılmaz (yalnızca "Windows ile başlat"ı açarsanız `HKCU\...\Run`). Geliştirme/sınama için `RUSTDROPER_DATA` ortam değişkeni başka bir veri klasörü seçer.
- Kaynaktan üretmek için: `powershell -File build\publish-portable.ps1`.

## 2. İlk kurulum (bir kez)
1. **Steam ↔ Twitch bağlantısı:** <https://twitch.facepunch.com/connect> sayfasında hesaplarını bağla (drop'ların Steam'e düşmesi için şart). Uygulamada Twitch kartında **"Facepunch/Steam: bağlı"** görünmeli.
2. **Twitch girişi:** Twitch kartında *Giriş* → ekranda büyük, ortalı kod çıkar (yanındaki **Kopyala** düğmesi kodu panoya alır, ~2 sn "Kopyalandı ✓" gösterir; panoya yalnız kod yazılır), tarayıcı `twitch.tv/activate` sayfasını açar, kodu orada onayla. Parolan uygulamaya **hiç** girmez.
3. **Kick (isteğe bağlı):** Kick kartında *Giriş* → ekrandaki **QR'ı telefondaki Kick uygulamasıyla** okut (kod ~5 dk geçerli; QR'ın altındaki **Bağlantıyı kopyala** düğmesi aynı şekilde geri bildirim verir). Sonra Kick kartında **Oynatıcı girişi**: açılan pencerede kick.com'a kendin giriş yap, pencereyi kapat ("Oynatıcı oturumu: açık" görünmeli; parolayı uygulama görmez/saklamaz). **Kick'te izleme gizli bir tarayıcı penceresiyle (WebView2) yapılır**: ekranda görünmez, sessizdir, ~0,6–0,8 GB RAM ve bir çekirdeğin ~%20–50'sini kullanır (yalnız Kick çalışırken). *Canlı sınandı: sunucu sayacı ~1 dk/dk artıyor; Kick claim'i henüz sınanmadı.*

## 3. Günlük kullanım
- **İki ekran:** pencerenin üstündeki kayan **Twitch ⇄ Kick geçişi** (iOS tarzı) ile platform ekranı değişir; her ekranda yalnız o platformun kartı, uyarıları, drop listeleri ve olayları görünür (Twitch ekranında Kick'e dair hiçbir şey yok, tersi de). Diğer platformda dikkat gerektiren bir durum varsa (hata, çevrimdışı, giriş gerekli) geçişte o platformun yanında **küçük nötr bir nokta** çıkar — ayrıntı için o ekrana geç. Seçili ekran bir sonraki açılışta hatırlanır (`%LOCALAPPDATA%\RustDroper\settings.json` → `lastPlatform`). Geçiş Ayarlar sayfasındayken de görünür; bir platform seçmek Ayarlar'ı kapatır.
- **Başlat uyarısı:** Başlat'a basınca (pencere açıkken) "aynı Twitch/Kick hesabıyla başka bir canlı yayın izlemeyin; drop ilerlemesi bozulabilir" hatırlatması çıkar. "Bir daha gösterme"yi işaretlersen kalıcı kapanır; Ayarlar → "Başlat uyarısını göster" ile geri açılır. Tepsi menüsünden başlatınca (pencere kapalıyken) gösterilmez.
- **Başlat / Duraklat:** pencerede ya da tepsi simgesine sağ tık menüsünden. Düğme **iki platformu birlikte** yönetir (giriş yapılmış her platformda başlatır; çalışan varsa hepsini durdurur).
- Pencereyi kapatınca uygulama **tepsiye küçülür** (kapanmaz). Tepsi simgesine sol tık = aç/gizle, sağ tık = menü (**Çıkış** buradan).
- **Etkinlik bilgisi:** platform kartının ortasında etkinlik adı, durum (Yakında/Aktif/Bitti), ayrıntı, yerel saatte başlangıç–bitiş ve kalan süre (dakikada bir yenilenir); yanındaki **↗ Siteyi aç** o platformun resmî drop sayfasını açar. Etkinlik bittiyse Facepunch'tan gelen kartlar "Etkinlik bitti" olarak soluk kalır. Facepunch/Steam bağlantısı kartta "Bağlı: …" satırının altında görünür.
- **Yayıncı drop'ları öncelik sırası:** yayıncı kartlarını üstlerindeki tutamaçtan (⠿) sürükleyerek sırala (soldan sağa, yukarıdan aşağıya = öncelik). Motor sıradaki ilk uygun (çevrimiçi, tamamlanmamış) yayıncı drop'unu izler; çevrimdışıysa sonrakine geçer, hiçbiri yoksa genel drop'lara döner. Sıra kalıcıdır, Twitch ve Kick için ayrıdır; izleme sürerken değiştirirsen en geç ~1 dk içinde, drop %90'a varmadıysa yeni sıraya geçilir. Bir drop'a birden çok yayıncı atanmışsa hangisini izlesen de ilerler; Rust Droper çevrimiçi olanlardan birini seçer ve çevrimiçi kaldıkça değiştirmez.
- **Drop kartları:** genel ve yayıncı drop'ları 5 sütunluk görsel kartlar. Resimler Facepunch sayfasından (açılışta ve 6 saatte bir) çekilip `%LOCALAPPDATA%\RustDroper\media` altında önbelleklenir (≤20 MB; silinirse yeniden iner). Etkinlik başlamamışken de kartlar "Etkinlik bekleniyor" olarak görünür. Kart durumları: kilit (sırada), saat (devam), nabızlı nokta (şu an izleniyor), hediye + amber parıltı (claim'e hazır), tik (claim edildi). Kick ekranında kendi kampanyasının resimleri (yoksa yer tutucu) görünür. İnternet yoksa kartlar resimsiz yer tutucuyla yine çizilir.
- **Claim edilen ödüller:** son 5 ödül görsel kart (görsel, ad, kaynak, claim tarihi); gerisi "… ve N ödül daha". Kaynak satırı: oyun adı varsa o, Facepunch bağlantılı ödülde "Rust · Facepunch", yoksa platform adı (Twitch envanteri yayıncı adını vermez). Görseller Facepunch resimleriyle aynı önbelleğe (`%LOCALAPPDATA%\RustDroper\media`) iner; internet yoksa kart ikonla görünür.
- Pano (seçili platformun): *Şu an* kartı (yayıncı + bağlantı, kampanya, % ilerleme, izlenen/kalan dk, tahmini bitiş), genel ve yayıncı drop listeleri, claim edilen ödüller, son olaylar.
- Rust'ta uygun kampanya yokken motor **Boşta** bekler ve her ~5 dk'da yeniden bakar; kampanya başlayınca kendiliğinden izlemeye geçer.

## 4. Ayarlar (Pano → Ayarlar) · Hakkında
| Ayar | Varsayılan | Ne yapar |
|---|---|---|
| **Otomatik claim** | AÇIK | Tamamlanan drop'ları kendiliğinden claim eder (geri alınamaz oyun eşyaları dahil; kullanıcı kararı). *Rust için açık kalsın.* Değişiklik **Duraklat → Başlat** ile geçerli olur. Kapalıyken motor çalışırken pencerede kırmızı uyarı çıkar. |
| **Yalnız Rust ödülü veren kampanyalar** | AÇIK | Rust yayınında bulunan ama Rust eşyası vermeyen yayıncı kampanyalarını (ör. sohbet rozeti) izlemez/göstermez. Bir kampanya Rust sayılır: oyun adı "Rust", Facepunch hesap bağlantısı ya da ödül adı resmî Facepunch listesinde. Yanlış eleme şüphesinde kapat (Sağlık kontrolü elenenleri gösterir). Duraklat → Başlat ile geçerli. |
| **Bildirimler** | AÇIK | Tepside balon: drop claim edildi / edilemedi, yeniden giriş gerekli, kampanya başladı. "Bildirimi dene" düğmesi var. |
| **Çalışırken uykuya geçmeyi engelle** | AÇIK | Motor izlerken bilgisayar uykuya dalmaz (ekran kapanabilir). Uyuyan bilgisayar drop ilerletemez. Boşta iken uyku engellenmez. |
| **Dil / Language** | Otomatik | *Otomatik (Windows)* · *Türkçe* · *English*. Otomatik: Windows görüntü dili Türkçe ise Türkçe, değilse İngilizce. Seçim **anında** uygulanır (yeniden başlatma gerekmez). Eski ayar dosyalarında Türkçe korunur. Günlük dosyaları ve geliştirici CLI çıktısı Türkçe kalır; geçmiş "Son olaylar" notları üretildikleri dilde görünür. İngilizce kılavuz: `USAGE.md`. |
| **Güncelleme notları** | — | Başlıkta Hakkında (ⓘ) düğmesinin solundaki geçmiş ikonu: sürüm kuralı (Semantic Versioning: MAJOR/MINOR/PATCH) ve yeni → eski tüm sürümlerin notları (Eklenenler / Değişenler / Düzeltilenler / Kaldırılanlar). Dile göre anında yenilenir. Aynı içerik depoda [`CHANGELOG.md`](../CHANGELOG.md) (İngilizce) ve [`CHANGELOG.tr.md`](../CHANGELOG.tr.md) (Türkçe) dosyalarındadır. Güncel sürüm: **1.0.0**. |
| Küçültülmüş başlat | KAPALI | Doğrudan tepsiye yerleşir. |
| Windows ile başlat | KAPALI | Oturum açılışında kendini başlatır. |
| **Hakkında** | — | Başlıktaki ⓘ düğmesi (Ayarlar'ın yanında; ayrıca pencerenin en altında uygulama adı + sürüm satırı): sürüm, geliştirici (Pinhan), iletişim (thepinhan@gmail.com; e-posta gönder/kopyala), ne yapar, gizlilik, sorumluluk reddi, bileşenler, veri/günlük klasörünü aç. |
| Oyun (`gameSlug`) | `rust` | Arayüzde yok; `%LOCALAPPDATA%\RustDroper\settings.json` içinde. Normalde dokunma. |

## 5. Yeni bir drop etkinliğinin başında kontrol listesi
Örnek: Twitch genel drop'ları kümülatif izleme süresiyle verilir (ör. 1 sa · 2 sa · 3 sa · 4 sa · 5 sa). Etkinlik saatleri Facepunch sayfasında ve uygulamadaki **etkinlik kartında** (yerel saatte) yazar.
1. Etkinlikten **~15 dk önce** uygulamayı açın. Twitch kartı *bağlı* ve **Facepunch/Steam: bağlı** olmalı.
2. **Sağlık kontrolü** (başlıktaki nabız simgesi) hepsi yeşil/uyarısız olmalı; **Otomatik claim** (varsayılan AÇIK) ve bildirimler açık → **Başlat**.
3. Etkinlik başlayınca (birkaç dk içinde) durum *Boşta*'dan *İzleniyor*'a geçmeli (Rust Drops-Enabled bir yayına bağlanır). Balon: *"kampanya başladı"*.
4. İlk dakikalar ilerleme **1–2 dk gecikmeli** başlar; sonra dakikada +1. *Şu an* kartında kanal ve % görünür.
5. İlk drop tamamlanınca *"Drop claim edildi"* balonu çıkar. Birkaç dk içinde Steam envanterinde görünür (Facepunch SSS: en geç ~10 dk; 30 dk'dan sonra yoksa hesap bağlantısını kontrol edin).
6. Sonraki drop'lar kendiliğinden sürer. Günlükler: `%LOCALAPPDATA%\RustDroper\logs\` — claim sonuçları `history.jsonl` içinde.
7. **Elle claim gerekirse:** balon *"claim edilemedi"* derse <https://www.twitch.tv/drops/inventory> sayfasından claim edin (drop kaybolmaz).

## 6. Sorun giderme
| Belirti | Ne yap |
|---|---|
| Hep **Boşta** | Kampanya henüz başlamamış olabilir. `logs` klasörü içinde keşif satırlarına bak. Rust'ta Drops-Enabled yayın yoksa Boşta kalır. |
| **Degraded / "şema değişmiş olabilir"** | Twitch arka ucu değişmiş olabilir; motor üstel beklemeyle kendini yeniden dener. Sürerse günlüğü paylaş. |
| **"Yeniden giriş gerekli"** | Twitch oturumu reddedildi; Twitch kartından yeniden giriş yap. |
| İlerleme yok (10+ dk) | Watchdog kanalı 20 dk engeller ve başka kanala geçer; iki kanalda olmazsa kampanya 60 dk bekletilir ve *"hesap bağlı olmayabilir"* uyarısı verir → Steam bağlantısını kontrol et. |
| Bilgisayar uyudu | Uyanınca motor oturumu doğrulayıp kendini toparlar; "uykuyu engelle" ayarı açıksa izlerken uyumaz. |
| İnternet gitti | Pencerede uyarı şeridi çıkar; bağlantı gelince otomatik devam edilir. |

## 7. Bilinenler
- Twitch izleme **gerçek yayın trafiğiyle** (tarayıcısız, en düşük kalite veya yalnız ses) yapılır; yaklaşık 2 MB/dk (≈125 MB/sa). Küçük kanallarda düşük kalite yoksa yalnız-ses seçilir.
- Claim, Twitch-yerel ödüllerde (rozet/emote) canlıda doğrulandı. **Oyuna teslim edilen Rust ödüllerinin (Steam/Facepunch) claim'i gerçek bir Rust kampanyasında henüz yeni sınanıyor** (yedek yol ve "elle claim" bildirimi hazır).
- Genel kampanyada drop'ların paralel mi sıralı ilerlediği henüz kesin gözlenmedi.
- Kick: giriş (QR) ve gizli oynatıcıyla izleme canlıda doğrulandı (ilerleme arttı); Kick claim'i canlıda sınanmadı; uzun süreli kesintisiz izleme risk/kaynak açısından önerilmez.

## Uyarı
Twitch ve Kick otomasyonu şartlarında yasaklıyor olabilir; hesap uyarısı/kısıtlaması ihtimali sıfır değildir. Tek hesap, tek yayın, makul istek hızı kullanılır, ama risk kullanıcıya aittir. Proje Twitch, Kick veya Facepunch ile bağlantılı değildir.
