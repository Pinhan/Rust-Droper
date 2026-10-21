# Platform notları (Twitch, Kick, Facepunch)

[English](PLATFORM-NOTES.md) · Projenin araştırma ve canlı sınamalarından derlenmiş özet (Ekim 2026). Bunlar, haber verilmeden değişebilen *üçüncü taraf hizmetlere dair gözlemlerdir*; kod her alana savunmacı yaklaşır (tipli, toleranslı DTO'lar; eksik alan = uyarı + degrade mod). Burada hesap tanımlayıcıları veya oturum verisi yoktur.

## 1. Rust drop'ları nasıl işler

**Twitch** ([twitch.facepunch.com](https://twitch.facepunch.com))
- Rust, Twitch Drops etkinlikleri yapar. *Genel* drop'lar, Drops-Enabled **herhangi** bir Rust yayınında kümülatif izleme süresidir (ör. 1 sa, 2 sa, 3 sa, 4 sa, 5 sa'lik eşyalar); *yayıncı* drop'ları belirli bir yayıncının kanalını daha kısa süre izlemeyi ister (ör. 30 dk).
- **Steam hesabınız Twitch hesabınıza bağlı olmalı** (`/connect`). Eşyalar claim'den yaklaşık 10 dk sonra Steam'e düşer; claim edilen eşyalar 180 gün içinde Steam'e aktarılabilir.
- Aynı anda birden çok kanal izlemek hızlandırmaz: Twitch **hesap başına aynı anda tek kanala** kredi verir.

**Kick** ([kick.facepunch.com](https://kick.facepunch.com))
- Kick'in **herkese açık, kimliksiz** bir kampanya API'si vardır (`web.kick.com/api/v1/drops/campaigns`); Rust kampanyaları `category.slug == "rust"` olanlardır. Kampanyanın ödülleri `required_units` (dakika) taşır. Genel kampanyalarda `channels: []` (kategorideki herhangi bir canlı yayın), yayıncı kampanyalarında kanallar listelenir.
- Kimlikli uçlar (`/api/v1/drops/progress`, `/progress/summary`, `POST /api/v1/drops/claim`) geçerli token yoksa **403 `{"data":{"type":"Forbidden"}}`** döner (401 değil); Kick-JSON'u olmayan 403 Cloudflare engelidir (token korunur).
- Aynı anda yalnızca bir aktif kanal ilerler (Facepunch SSS).

**Facepunch sayfaları** yalnızca ikincil kaynaktır (etkinlik tarihleri, görseller, hesap bağlama yönlendirmesi). Uygulama onların HTML'ine bağımlı değildir: sayfa değişirse uygulama görselsiz/tarihsiz çalışmaya devam eder.

## 2. Twitch teknik notları

- **Giriş:** OAuth cihaz-kodu akışı (`id.twitch.tv/oauth2/device` → kullanıcı kodu → `/oauth2/token` yoklama → `/oauth2/validate`). İstemci **profili** yapılandırılabilir: sınamada cihaz uç noktası `ANDROID_APP` ve `WEB` istemci kimliklerini reddetti (HTTP 400 "invalid client"), `MOBILE_WEB` GraphQL'i çağıramadı (401), `SMARTBOX_TV` profili cihaz akışı, GraphQL ve PubSub'da Client-Integrity belirteci olmadan çalıştı. Rust Droper bu profili kullanır. Twitch bunu her an değiştirebilir.
- **GraphQL kalıcı sorgular** (`Inventory`, `DirectoryPage_Game`, `DropsHighlightService_AvailableDrops`, `DropCurrentSessionContext`, `DropsPage_ClaimDropRewards`, `PlaybackAccessToken`, …) Twitch'in ara sıra değiştirdiği sha256 özetleriyle çağrılır → `PersistedQueryNotFound`. Uygulama net bir "şema değişmiş olabilir" durumu gösterir ve yeniden dener; özetlerin otomatik keşfi yol haritasında. Kullanılan hız sınırı: ≤ 5 istek/sn.
- **Kampanya keşfi:** eski kampanya listesi sorguları bu istemci için `null` döner. Keşif kanal üzerinden yapılır: `DirectoryPage_Game` (Rust, `DROPS_ENABLED` filtresi) → `AvailableDrops(kanal)`. Bazı "kampanyalar" abonelik tabanlıdır (`requiredSubs > 0`, izleme süresi yok) ve atlanır.
- **İlerleme:** `DropCurrentSessionContext` (`channel`, `currentMinutesWatched`, `requiredMinutesWatched`, `dropID`) birincil kaynaktır, dakikada bir yoklanır. PubSub `user-drop-events` `drop-progress` mesajları seyrektir, yalnızca ipucudur. Drop tamamlanınca oturum `null` değil, *boş nesne* olur.
- **İzleme:** yayının HLS ana listesi → en düşük varyant → medya listesi, sonra yeni parçaları çekmek sunucudaki izleme sayacını ilerletir (dakikada yaklaşık bir dakika; ilk dakika ~1–2 dk gecikmeli). 160p'de yaklaşık 2 MB/dk. Kanalda düşük varyant yoksa `audio_only` sayacı aynı hızda ilerletir. Tarayıcı ve telemetri olayı gerekmez.
- **Claim:** `DropsPage_ClaimDropRewards`, normalde PubSub `drop-claim` olayıyla gelen bir `dropInstanceID` ister. Sınamada bu olay çoğu zaman **hiç gelmedi**; kimlik `{kullanıcıNo}#{kampanyaId}#{dropId}` biçimindedir ve kendi kurduğumuz kimlik sunucuca kabul edildi. Bu yüzden Rust Droper, `drop-progress` `current == required` olduktan 30 sn sonra `drop-claim` görülmediyse yedek yolla claim eder. Mutasyon tamamlanmış drop için `ELIGIBLE_FOR_ALL` döner, idempotenttir, tanınmayan kimlik için hata değil `claimDropRewards: null` döner. Twitch-yerel ödüller (rozet/emote) tamamlanma anında sunucuca otomatik verildi. **Açık soru:** gerçek bir Rust kampanyasında oyuna teslim edilen (Steam/Facepunch) eşyaların davranışı.
- **Envanter:** `Inventory.gameEventDrops[]`, claim edilmiş ödülleri `isConnected`/`requiredAccountLink` ile listeler; Steam/Facepunch bağlantı durumu buradan okunur. `dropCampaignsInProgress` bazı kampanya sınıflarında `null`'dır, güvenilemez.

## 3. Kick teknik notları

- **Kamusal uçlar tarayıcısız çalışır** (Cloudflare bunlar için .NET `HttpClient`'ı engellemez): kampanyalar, `mobile.kick.com/api/v1/livestreams?category_id=…`, `kick.com/api/v2/channels/{slug}` (`playback_url`, `livestream.is_live`, kategoriler), `web.kick.com/api/v1/drops/campaigns/{id}/livestreams?lang_code=en` (`lang_code` zorunlu).
- **Giriş:** "TV login" akışı — uygulama `uuid` + 6 haneli kod üretir, kullanıcı bunu yerelde üretilen QR ile (hiçbir QR servisine gönderilmez) **telefondaki Kick uygulamasıyla** onaylar, uygulama `POST kick.com/api/tv/link/authenticate/{uuid}` uç noktasını yoklar. Bağlantı masaüstü tarayıcıda 404 verir; Kick mobil uygulamasıyla açılmalıdır.
- **İzleme gerçek oynatıcı ister.** Yalnız HLS çekmek (10 dk, 145 parça bile) sunucuda **hiç** ilerleme oluşturmadı; izleyici kimliği oynatıcının viewer-websocket'inden geliyor ve oturum açmış bir tarayıcı profili gerekiyor. Gizli bir WebView2 oynatıcı (ekran dışı pencere, sessiz, en düşük kalite) ilerlemeyi işler: yaklaşık **dakikada 1 birim, ~2 dk'da bir +2 adımla**. Geliştirirken öğrenilenler: sayfa ≥ ~1024 px geniş olmalı (altında Kick kalite menüsüz mobil yerleşime geçer); kalite menüsü yalnızca gerçek fare olaylarına tepki verir (uygulama DevTools `Input.dispatchMouseEvent` kullanır) ve seçilen kalite kalıcı değildir; çerez bandı (OneTrust) X ile kapatılır, asla "tümünü kabul"e basılmaz; oturum durumu "Giriş Yap" düğmesinin varlığından anlaşılır (çerez/belirteç okunmaz).
- **Claim:** claim uç noktası kodlanmıştır ve Kick otomatik claim'i varsayılan olarak **kapalıdır**; canlıda sınanmadı.

## 4. Risk

İki platformun koşulları da otomasyonu yasaklıyor olabilir; uyarı veya kısıtlama mümkündür. Riski azaltan tasarım tercihleri: platform başına tek hesap, aynı anda tek yayın, makul istek hızı, yapay telemetri yerine gerçek yayın trafiği, gereksiz GraphQL yükü yok. Ayrıca bkz. [Kick risk araştırması](04-kick-risk-arastirmasi.md) ve [eşzamanlı izleme / çoklu hesap araştırması](06-arastirma-eszamanli-izleme-ve-coklu-hesap.md).
