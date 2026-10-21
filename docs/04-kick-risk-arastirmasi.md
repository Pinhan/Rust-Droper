# 04 — Kick Risk Araştırması (drop için otomasyon / "izliyorum" sinyali)

> Tarih: 2026-10-19 · Yöntem: yalnızca herkese açık sayfalar ve web aramaları (hiçbir hesaba giriş yok, Kick'e istek yok, kod çalıştırma yok).
> Soru: Kick'te drop toplamak için otomasyon kullanan ya da izleyici varlığı (presence) sinyali taklit eden hesaplar banlandı / kısıtlandı / diskalifiye edildi mi?
> Kanıt etiketleri: **[KESİN]** = resmi kaynak ya da birincil kayıt doğrudan okundu · **[İDDİA]** = tek/ikincil kaynak ya da üçüncü taraf tersine mühendislik · **[SÖYLENTİ]** = doğrulanmamış / satıcı pazarlaması · **[YORUM]** = benim çıkarımım (kanıt değil).
> Önemli sınırlama: sayfalar bir "özetleyici" araçla okundu (kelimesi kelimesine değil). Madde numaraları (ToS 1.4(b), Drops Şartları 7(b) vb.) karar vermeden önce kullanıcı tarafından resmi sayfadan bir kez göz atılarak doğrulanmalı.

---

## 1) SONUÇ

**Kick'te "drop kazanmak için otomasyon / sahte izleme kullandığı için banlanan, kısıtlanan ya da diskalifiye edilen" bir hesaba dair doğrulanmış hiçbir vaka bulunamadı — cevap: HAYIR (bulunamadı), güven düzeyi: ORTA-DÜŞÜK.** "Risk yok" anlamına gelmez, çünkü (a) Kick'in resmi kuralları (ToS + Drops Şartları) bu davranışı cezalandırmaya **açık kapı bırakıyor** (otomatik erişim yasağı; hile/atlatma/istismar nedeniyle diskalifiye ve ilerlemeyi geri alma; "herhangi bir sebeple" askıya alma hakkı), (b) Kick'in kanıtlanmış yaptırımları yalnızca **viewbot'a (yayıncı kanalları / sahte izleyici trafiği)** yönelik (Mayıs 2026'da 500 hesap), drop izleyicilerine uygulandığına dair kanıt yok ama dışlanabilmiş de değil, (c) Kick drop araçları ancak ~Kasım 2025'ten beri var ve kullanıcı tabanları küçük (en büyüğü ~80 yıldız), yani "ban raporu yok" sonucunun ağırlığı sınırlı, (d) Reddit/Discord (en olası yer) bu araçla taranamadı. **Viewer-websocket'i (Tier C) kullanan en az bir aktif açık kaynak araç var (Lurkloot) ve repo/changelog'unda hiç ban/askıya alma kaydı yok** — bu en yakın "canlı deney", ama kanıt gücü zayıf (küçük kitle, 4–5 ay).

---

## 2) Kick'in kuralları (özet)

Üç resmi belge okundu. Hiçbirinde "drop botu" ifadesi **geçmiyor**; ama hepsi dolaylı olarak kapsıyor.

| Belge | Son güncelleme | Konu ile ilgili hükümler (özet) | Kanıt |
|---|---|---|---|
| [Kullanım Şartları (ToS)](https://kick.com/terms-of-service) | 23 Mart 2026 | **Madde 1.4(a,b):** otomatik sistemlerle (robot, örümcek vb.) hizmete erişim ve scraping yasak. **1.4(d,e,j):** sistem bütünlüğünü bozma / aşırı yük / hizmetin işleyişine müdahale yasak. **1.4(l,n):** erişim kısıtlarını/filtreleri aşma yasak. **1.4(i):** dolandırıcılık/kimlik gizleme yasak. **1.5:** Kick, "herhangi bir sebeple", bildirimsiz askıya alabilir / sonlandırabilir. **4.8(g):** hesap kapanırsa kullanılmamış dijital içerik (KICKS, hediyeler) kaybolur. Metinde "viewbot", "drop" kelimeleri **yok**. | [KESİN] (özetleyici okuma) |
| [Topluluk Kuralları](https://kick.com/community-guidelines) | 19 Mart 2026 | **Kural 7 "Bütünlük ve Özgünlük":** izlenme/takipçi/etkileşimi yapay şişiren botlar, manipülasyon ve üçüncü taraf araçlar ve haksız avantaj sağlayan hack/hile/istismar yasak. **8:** izin dışı betik / parmak izi teknolojileri yasak. Kanal Puanı/KICKS gibi araçların bütünlüğünü bozan kullanım → özellik kaybı / kısıtlama. Yaptırım basamakları: uyarı → kısıtlama → geçici/süresiz askıya alma → kalıcı ban; tekrarlarda ağırlaşır; itiraz yolu var. "viewbotting" terimi ve **drops'a özgü madde yok.** | [KESİN] (özetleyici okuma) |
| [KICK Drops Şartları](https://kick.com/kick-drops-terms) | 10 Haziran 2026 | **3(c):** hesap askıda/banlı ya da ToS–Kurallar kapsamında cezalıysa drop'a uygun olmayabilir; Kick, drop süresince ilerlemenizi/katılımınızı **geri çekebilir**. **6(a):** dürüst davranma, aldatıcı/sahtekârca davranmama. **7(a):** dolandırıcılık, drop'u manipüle etme, ToS ihlali → Kick ödülü reddedebilir/geri alabilir, diskalifiye edebilir. **7(b):** haksız oyun, kuralları atlatma girişimi, **modifikasyon/glitch/exploit kullanımı** (drop duyurusu açıkça izin vermedikçe) → diskalifiye. **7(e):** Kick "adalet ve bütünlük için gerekli gördüğü herhangi bir sebeple" diskalifiye edebilir. **13:** Kick drop'u dilediği an değiştirebilir/iptal edebilir. Botlar, otomasyon, çoklu hesap kelimeleri **geçmiyor** (okunan özetleyiciye göre). | [KESİN] (özetleyici okuma) |
| [Facepunch — Drops on Kick](https://kick.facepunch.com/) | (tarihsiz) | Steam+Kick bağlantısı gerekli; "Drops Enabled" etiketli Rust yayınları sayar; **aynı anda tek aktif kanalda** ilerleme olur (birden fazla kanalı izlemek fazladan drop vermez); 180 gün içinde talep edilmeyen drop düşer. Hile/bot/diskalifiye maddesi **yok**. Facepunch'ın destek makalesi ([KICK Drops](https://support.facepunchstudios.com/hc/en-us/articles/31547758458013-KICK-Drops)) 403 verdi, **okunamadı**. | [KESİN] (ana sayfa) / okunamadı (destek) |
| Kick Yardım Merkezi | — | Drops ile ilgili resmi bir yardım makalesi **bulunamadı**. [kick.com/drops](https://kick.com/drops) sayfası JS ile yüklendiği için boş döndü. | doğrulanamadı |

**Düz Türkçe okuma [YORUM]:** Kick "botla drop topla" demiyor ama "otomatik sistemle erişim yok", "hile/atlatma/exploit yok", "bizim takdirimiz" diyor. Bir ihlal iddiasında **ilerlemenin silinmesi, drop'tan diskalifiye ve hesabın askıya alınması** için metinsel dayanak mevcut. Sadece "gerçek izleyici gibi görünen" bir davranışın cezalandırılıp cezalandırılmayacağı Kick'in tespit yeteneğine ve niyetine bağlı; yazıya dökülmüş bir garanti yok.

---

## 3) Bulunan vakalar

**Drop için bot/otomasyon/presence kullanımı nedeniyle Kick yaptırımı gören hesap: 0 vaka bulundu.** Aşağıdakiler yalnızca **komşu** vakalar (Kick'in bot/viewbot karşıtı davranışının kanıtı):

| Tarih | Kaynak | Ne oldu | Drop ile ilgili mi? | Kanıt gücü |
|---|---|---|---|---|
| 27 Ara 2023 | [Dexerto](https://www.dexerto.com/entertainment/kick-bans-waves-of-bots-as-viewers-accuse-streamers-of-viewbotting-2448889/) | Kick "çok sayıda bot hesap kaldırdık" dedi; takipçi sayılarında düşüş beklendi. Tespit yöntemi belirtilmedi. | Hayır | [KESİN] (haber) |
| Tarih doğrulanamadı (2024 civarı olduğu düşünülüyor) | [Sportskeeda](https://sportskeeda.com/esports/news-they-still-fixed-that-viewers-call-kick-streamer-gets-banned-showcasing-easy-to-use-view-botting-service) (sayfa doğrudan açılamadı, arama özetinden) | Yayıncı Nick White, yayında viewbot aracı geliştirip gösterdiği için banlandı; ban önce kalıcıydı, sonra 1 aylık askıya indirildi; topluluk "Kick yeterince yapmıyor" diye tepki gösterdi. | Hayır (yayıncı, viewbot) | [İDDİA] (tek kaynak, tarih yok) |
| 20–22 May 2026 | [Spilled](https://spilled.gg/kick-viewbotters-watch-hours/), [X: Nate Curtiss](https://x.com/natecurtiss_yt/status/2057242005244113307) | Kick ortak kurucusu Bijan Tehrani "en kötü 500 viewbot suistimalcisini banladık" paylaşımı yaptı; ~67 milyon "sahte" aylık izleme saati (~%13). **Kimin banlandığı (yayıncı kanalı mı, bot hesap mı) belirtilmedi**, resmî liste yok. | Hayır (viewbot) | [İDDİA] (Kick yetkilisinin paylaşımına dayalı, bağımsız doğrulama yok) |
| May 2026 | [Win.gg](https://win.gg/kick-allows-creators-to-hide-viewer-count/), [Dexerto](https://www.dexerto.com/twitch/kick-boss-blasts-twitchs-new-viewbot-protections-and-claims-they-wont-ban-big-streamers-3362613/) (yalnızca arama özetleri; sayfalar doğrudan okunmadı) | Viewbot yapan yayıncıların KPP gelirinde kesinti; keşfedilebilirlik düşürme; "aktif sohbet eden sayısı" göstergesi. | Hayır | [İDDİA] (doğrudan doğrulanmadı) |
| 25 May 2026 | [KickChatters](https://kickchatters.com/blog/does-kick-ban-view-bots/) | "Kick her viewbot kanalını banlamaz; veri merkezi IP'leri, sabit sayılar, toplu yeni hesaplar vb. aranır." | Hayır | [SÖYLENTİ] — **kendisi viewbot satıyor**, kaynak/veri yok |

**Aranıp bulunamayanlar:** "Kick drops ban", "Kick drop farming suspended/disqualified", "Kick account banned drops farming", Türkçe "Kick drop hesap ban / banlandı", X araması (Rust×Kick drop konulu) — hiçbirinde drop kaynaklı ban/diskalifiye çıkmadı. X'te Rust×Kick konusunda bulunanlar yalnızca (i) Facepunch'ın dolandırıcılık uyarısı ([X, Haz 2026](https://x.com/playrust/status/2065796313292194031)) ve (ii) Steam envanterine düşmeyen drop'lar ([X, Haz 2026](https://x.com/ErrnieGerrn/status/2065794031196455298)) — yani teslimat sorunları, ban değil.

---

## 4) Açık kaynak araç kayıtları

| Araç | Yöntem (kanıtlı) | "ban / suspended / detected / flagged" kaydı | Kayıtlar / uyarılar |
|---|---|---|---|
| [HyperBeats/KickDropsMiner](https://github.com/HyperBeats/KickDropsMiner) (82 yıldız; ~Kas 2025'te çıktı) | **Gerçek Chrome** (Selenium + undetected-chromedriver), kalıcı profil, isteğe bağlı "oynatıcıyı gizle" (video öğesi silinir, oynatma sürer) | **Yok.** README'de ban/disclaimer kelimeleri yok. 29 issue'dan 25'inin başlığı tarandı: ban/askı/tespit/uyarı geçen başlık **yok**. #41, #44 "drop ilerlemiyor" (oynatıcı gizliyken ilerleme durması, neden belirsiz) | [issues](https://github.com/HyperBeats/KickDropsMiner/issues), [#44](https://github.com/HyperBeats/KickDropsMiner/issues/44) [KESİN] |
| [tsgsOFFICIAL/StreamDropCollector](https://github.com/tsgsOFFICIAL/StreamDropCollector) | Gömülü **WebView2** oturumu + Kick REST API'leri | **Yok.** 15 issue başlığı tarandı; #17 "Collector ilerlemesi ≠ Kick ilerlemesi" (%98 vs %88, Haz 2026, çözüldü) dışında Kick'e özel kayıt yok. README: "kullanım riski size ait, otomatik izleme platform kurallarını ihlal edebilir" | [issues](https://github.com/tsgsOFFICIAL/StreamDropCollector/issues), [#17](https://github.com/tsgsOFFICIAL/StreamDropCollector/issues/17) [KESİN] |
| [Alorf/TwitchDropsBot](https://github.com/Alorf/TwitchDropsBot) | Kick TV-login + HTTP/3 (msquic) API istemcisi | **Yok.** [#25 "Kick Account: 403"](https://github.com/Alorf/TwitchDropsBot/issues/25) (Haz–Tem 2026, açık): bakımcı "hesabınıza bağlı olabilir" dedi; kullanıcı "bu kanalı izleme yetkiniz yok" mesajı aldı (bölge/yetki), ban kanıtı yok. [#24](https://github.com/Alorf/TwitchDropsBot/issues/24) HTTP/3 bağlantı hatası (altyapı). README: hesap askıya alma/ban sorumluluğu yok, otomatik farm ve bot çiftliği **önerilmez** | [KESİN] |
| **[jamezrin/lurkloot](https://github.com/jamezrin/lurkloot)** (31 yıldız; Chrome uzantısı + Docker CLI; v1.0.0 5 Haz 2026 → v1.18.0 18 Eki 2026) | **"Tabless" = viewer websocket ile izleme (Tier C'ye en yakın)**; yedek olarak sessiz sekme (gerçek oynatıcı). CLI (headless) Kick viewer soketini kullanır (v1.15.0, 3 Eki 2026) | **Yok.** Changelog ve issue başlıklarında hesap yaptırımı kaydı yok. **Ama:** (i) v1.17.1 (14 Eki): Kick sekmelerinde `Request blocked by security policy` (WAF engeli) → araç bekler, süren engelde durdurur ([PR #807](https://github.com/jamezrin/lurkloot/pull/807)); (ii) v1.16.0 (9 Eki): "Kick bazı kanalları izlenmiş gösterip saymıyordu" düzeltmesi; (iii) v1.17.0 (13 Eki): izleme süresini Kick web sitesiyle **aynı takvim ve ayrıntıyla** raporlar; "kanal sohbetine her zaman gir" ayarı izleyiciyi yayıncı/moderatörlere **izleyici listesinde görünür** kılar. README: "otomatik izleme bu şartlarca kısıtlanabilir, kendi takdirinizle kullanın" | [changelog](https://lurkloot.jamezrin.com/changelog/) [KESİN] |
| [DevilXD/TwitchDropsMiner #849](https://github.com/DevilXD/TwitchDropsMiner/issues/849) | Kick desteği talebi | Bakımcı Kick desteğini planlamadı (2 Kas 2025); yalnızca "kullanım riski size ait" diyerek KickDropsMiner'a yönlendirdi (13 Kas 2025). Thread'de ban/watch-time yöntemi tartışması **yok** | [KESİN] |
| [Dropilot](https://dropilot.app/) (ticari, bulut) | Sunucu tarafında izleme (yöntem belirtilmemiş); sitede "Zero risk" ifadesi | Ban kaydı/analizi yok; "risksiz" iddiası kanıtsız | [SÖYLENTİ] |

**Hangi yöntem sorun yaşadı?** Hiçbiri hesap yaptırımı bildirmedi. Teknik sorunlar: gerçek tarayıcı araçlarında oynatıcı gizlenince ilerleme durması (KickDropsMiner #41/#44), WebView ilerleme sapması (StreamDropCollector #17), websocket aracında WAF engeli ve izleme-sayılmama (Lurkloot). **Kanıt gücü: düşük** — yıldız/issue sayıları küçük, "rapor yok" ≠ "risk yok".

---

## 5) Twitch karşılaştırması

| Konu | Bulgu | Kanıt |
|---|---|---|
| TwitchDropsMiner ban vakaları | Üç issue: [#647](https://github.com/DevilXD/TwitchDropsMiner/issues/647) (Ara 2024, "1 hafta sonra banlandım", içerik silindi), [#730](https://github.com/DevilXD/TwitchDropsMiner/issues/730) (Tem–Eki 2025, "Automated or Botted Account" sohbet-spam gerekçesi), [#870](https://github.com/DevilXD/TwitchDropsMiner/issues/870) (Kas 2025, gerekçe "fraudulent behavior"). Üçünde de **madencinin sebep olduğuna dair kanıt yok**; bakımcı aracın sohbete yazmadığını, hesabın çalınmış/ele geçirilmiş olabileceğini söylüyor; "ban ihtimali sıfıra yakın" tahmini veriyor, 7/24 çalıştırmama önerisi. | [İDDİA] (anekdotsal) |
| Anti-bot kilitlenmeleri | TwitchDropsMiner v13 notları: eski sürümler Twitch'in anti-bot değişiklikleri sonrası CAPTCHA/~24 saat kilitlenme yaşatabiliyor ([sürümler](https://github.com/DevilXD/TwitchDropsMiner/releases), arama özeti). | [İDDİA] |
| Twitch'in resmi politikası | Ocak 2024: "Drops farming" (VOD tekrarı/alakasız içerikle drop kategorisi) yayıncılar için yasak; **izleyiciler cezalandırılmaz** ([Tubefilter](https://www.tubefilter.com/2024/01/19/twitch-drops-farming-crackdown/)). Botlar/otomatik izleme bu haberde geçmiyor. | [KESİN] (haber) |
| Benzerlik / fark | **Benzerlik:** iki platformda da ban vakası var ama "drop madenciliği" nedeni kanıtlanmış değil. **Fark:** Twitch araçları yıllardır yaygın ve bakımcı aktif; Kick araçları <1 yaşında ve Kick'in anti-bot altyapısı ile drop sistemi daha genç. Kick, Twitch'ten daha **sert viewbot söylemi** ve yeni kural/ban dalgaları yaşıyor (2026) — ama hedef yayıncı tarafı. | [YORUM] |

---

## 6) Teknik tespit ihtimali (kanıt ve spekülasyon ayrı)

**Kanıt (üçüncü taraf tersine mühendislik; Kick resmî belgesi yok) [İDDİA]:**
- Kick'in web oynatıcısı izleme sırasında viewer websocket'ine `tracking.user.watch.livestream` olayı yollar: bağlanınca bir kez, sonra her **120 sn**'de (aralık `kick.com/api/internal/settings/global` ayarından okunur); ayrıca `channel_handshake` her 15 sn, `ping` her 30 sn. Alanlar: `channel_id`, `livestream_id`, `vod_id` (`vod_id` `…/api/v2/channels/<slug>/info`'dan gelir; istemci onsuz izlemeyi izlemiyor) — kaynak: [Lurkloot #756](https://github.com/jamezrin/lurkloot/issues/756) (4–11 Eki 2026), [PR #776](https://github.com/jamezrin/lurkloot/pull/776).
- Bu yazarlar **sunucunun hangi sinyali saydığını kesin bilmediklerini** yazıyor: `vod_id` zorunlu mu, yarı sıklık ilerlemeyi etkiler mi — planlanan A/B testi yapılmadı, "Kick'in oynatıcısının yaptığı gibi yapıyoruz" varsayımı var. Sunucuda doğrulama/cezaya dair **kanıt yok**.
- Kick'in izleme verisi için resmî API yok ([KickDevDocs #332](https://github.com/KickEngineering/KickDevDocs/issues/332)).
- **Projemizin kendi ölçümü [KESİN]:** yalnızca HLS (kimliksiz video trafiği) 10 dk'da `progress_units` ilerletmedi (bkz. `docs/PLATFORM-NOTES.md` §5 U3). Lurkloot'un "bazı kanallar izlenmiş görünüp sayılmıyordu" düzeltmesi (v1.16.0) de sunucunun yalnızca belirli sinyalleri kredilediği izlenimini destekliyor [İDDİA/çıkarım].
- Kick'in web katmanı bot/WAF koruması kullanıyor: sekme isteklerine `Request blocked by security policy` dönmesi ([Lurkloot PR #807](https://github.com/jamezrin/lurkloot/pull/807), Eki 2026) ve kimlikli uçlarda 403 (projemizin gözlemi). Bu, istek düzeyinde bot süzgeci olduğunu gösterir; **hesap yaptırımına bağlandığına dair kanıt yok**.

**Spekülasyon (kanıtsız) [SÖYLENTİ/YORUM]:**
- "Sunucu, video trafiğiyle presence sinyalini eşleştiriyor" — **ne kanıt ne karşı kanıt bulundu.** Bu araştırmada böyle bir kontrolün var olduğunu ya da olmadığını gösteren hiçbir kaynağa rastlanmadı.
- "Videosuz ama presence'li (tabless) trafik viewbot yapısına benzer" — mantıksal çıkarım: viewbot'lar da "oynatıcı çalıştırmadan bağlantı" kuruyor; Kick bunu izleyici hesabı ile drop bağlamında da süzüyor olabilir. Kanıt yok.
- Tabless modun "videosuz/reklamsız" olması ([Lurkloot README](https://github.com/jamezrin/lurkloot): arka plan izleme video ve reklam izletmez) yayıncı ve Kick için reklam geliri etkisi demek → Kick'in bunu ayrıca hedefleme motivasyonu olabilir. Çıkarım.

---

## 7) Bulunamayan / doğrulanamayan şeyler

- Reddit (r/KickStreaming, r/Rust, r/dropsfarming…) arama aracı tarafından taranamadı (alan engelli); Reddit'te bir vaka olması **dışlanamaz**. Discord/forum içi yazılar indekslenmiyor. Türkçe aramalar boş döndü.
- Kick Yardım Merkezi'nde drops makalesi bulunamadı; Facepunch destek makalesi 403 verdi; `kick.com/drops` JS sayfası okunamadı.
- Resmi belgeler özetleyici araçla okundu; madde numaraları ve ifadeler elle teyit edilmeli.
- Nick White olayının tarihi ve Kick'in resmi açıklaması; Mayıs 2026'daki 500 banın kimleri kapsadığı (yayıncı mı bot hesap mı) doğrulanamadı.
- Win.gg ve Dexerto'nun Mayıs 2026 viewbot yazıları yalnızca arama özetinden alındı; Sportskeeda "yeni anti-botting kuralları" yazısı 405 verdi.
- Lurkloot/KickDropsMiner/StreamDropCollector kullanıcı sayıları bilinmiyor; "ban yok" sonucu **kullanıcı tabanı** ile kıyaslanamıyor. KickDropsMiner'ın 4 issue'su ve StreamDropCollector issue gövdeleri (yalnızca başlıklar) taranamadı.
- Kick'in bir drop izleyicisini hangi eşik/davranışla işaretlediği, video ve presence tutarlılık kontrolü yapıp yapmadığı: **bilinmiyor**.
- Steam/Rust hesabının (Facepunch tarafı) Kick drop'u hileli bulunursa cezalandırılıp cezalandırılmayacağı: doğrulanamadı.

---

## 8) Karar için sade özet (Tier B = gizli gerçek Kick oynatıcısı · Tier C = izleyici varlığı taklidi)

1. **Bilinen vaka yok:** Kick'te drop için bot/otomasyon kullanan bir hesabın banlandığına dair doğrulanmış tek bir kayıt bulamadım. Ama bu, güvenli olduğunun kanıtı değil: araçlar yeni, kullanıcı azı, Reddit/Discord taranamadı.
2. **Kurallar ceza kapısını açık bırakıyor:** Kick "otomatik sistemle erişim" ve "hile/atlatma/exploit" diyor; hesabı herhangi bir sebeple askıya alabileceğini, drop ilerlemesini silebileceğini ve diskalifiye edebileceğini yazıyor. Yani kural olarak Tier B de Tier C de bir gri alanda; sadece Tier C bu maddelere (otomatik erişim + hile/atlatma) daha net oturuyor [YORUM].
3. **Tier B daha az fark edilir:** Gerçek oynatıcı, gerçek video, gerçek reklam trafiği; Kick açısından normal izleyiciden ayırt etmek zor. En çok risk, gizli/başsız pencere izi ve 7/24 kesintisiz izleme.
4. **Tier C daha kırılgan ve daha riskli görünüyor:** Videosuz, yalnızca "izliyorum" sinyali; yapısı viewbot'a benziyor (Kick'in asıl savaştığı şey) ve Kick sinyalleri değiştirirse bozulur. Lurkloot, Kick izleme mantığını Ekim 2026'da en az 3 kez değiştirmek zorunda kaldı (3, 9 ve 13 Eki). Kick'in bunu ayrıca yakalayıp yakalamadığı **bilinmiyor**; ne kanıt ne karşı kanıt var.
5. **Kick'in kanıtlı yaptırımı viewbot:** Mayıs 2026'da 500 viewbot hesabı banlandı, yayıncı gelirleri kesiliyor. Bu izleyicilere/drop'a uygulandığına dair kanıt yok ama ruhu aynı: "yapay izleme".
6. **En kötü senaryo:** Kick hesabının askıya alınması/kapanması (KICKS ve dijital içerik kaybı dahil), drop ilerlemesinin silinmesi veya ödülün geri alınması. Steam/Rust hesabına yansıma ihtimali doğrulanamadı.
7. **Mevcut pratik:** Gerçek tarayıcı kullanan araçlar (KickDropsMiner, StreamDropCollector) ve Lurkloot'un tabless modu aylardır ban raporu almadan kullanılıyor; Twitch'te de benzer şekilde madenci-kaynaklı ban kanıtlanmış değil. Bunlar "düşük ama sıfır olmayan" risk için zayıf kanıt.
8. **Benim yorumum:** Tier B (gizli gerçek oynatıcı) risk/getiri olarak daha savunulabilir; Tier C'yi seçersen "belirsiz ve kırılgan" olduğunu bilerek, yalnızca tek ve kendi hesabınla, kısa sürelerle ve 7/24 olmadan dene. Karar kullanıcının. (Bu madde yorumdur; yukarıdaki kanıtlardan doğrudan çıkan bir sonuç değildir.)

---

## Arama günlüğü (tekrarlanmaması için)

Aranan: Kick drops banned/bot/farming; Kick ToS viewbotting; Kick Rust drops FAQ; Kick help center drops; "drop hesap ban" (TR); Alorf/TwitchDropsBot; Kick drops miner suspended; Kick viewer websocket presence; Twitch drops farming policy 2024; TwitchDropsMiner banned; Kick bot purge 2026; Lurkloot/Dropilot risk; Nick White; Twitch Drops ToS otomasyon. Okunan resmi sayfalar: kick.com/terms-of-service, /community-guidelines, /kick-drops-terms, kick.facepunch.com. Okunamayan: reddit.com (engelli), kick.com/drops (boş), Facepunch destek (403), sportskeeda iki makale (405).
