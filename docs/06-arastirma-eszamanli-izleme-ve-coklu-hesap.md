# 06 — Araştırma: eşzamanlı izleme ve çoklu hesap (2026-10-19)

> Yalnız araştırma/analiz; kod değişmedi. Güven düzeyleri açıkça işaretli. Kaynaklar sonda.

## 1. Kullanıcı aynı hesapla tarayıcıdan başka yayın izlerse ne olur? (istek #16)

### Bilinenler
- **[F] Resmî (Facepunch SSS):** "Watching more than one channel simultaneously will not result in more drops" — drop ilerlemesi hesap başına **aynı anda tek kanal** için işler; yayıncı drop'ları ilgili yayıncıyı izlemeyi ister.
- **[F] Referans proje uyarısı (TwitchDropsMiner, Docker sürümü belgesi):** madenci çalışırken *aynı hesapla* tarayıcıda (ya da başka yolla) yayın izlemek "genellikle madencinin sahte ilerleme bildirmesine ve mevcut drop'ta takılmasına" yol açar; o hesapla başka yayın izlenmemesi önerilir.
- **[F] Bizim canlı bulgumuz (docs/PLATFORM-NOTES.md, U1):** HLS izleme sunucu sayacını hesaba bağlı `PlaybackAccessToken` üzerinden ilerletiyor; sunucu tarafında hesap başına **tek** `dropCurrentSession` var (`channel` alanını da döndürüyor).

### Çıkarım (kanıtlanmadı — canlı sınanabilir)
1. Kullanıcı tarayıcıda başka bir kanal açınca Twitch'in web oynatıcısı o kanal için "izleme dakikası" sinyali gönderir; sunucudaki **geçerli oturum** son sinyale göre o kanala geçebilir. Sonuç iki olası durum:
   - Tarayıcıdaki kanal **drop'lu değil / başka oyun** → oturum bizim kanalımızdan kopar, ilerleme durur → bizim `DropEngine` bunu "takıldı" sayar, 5 dk sonra **kanalı 20 dk engeller** ve başka kanala geçer (gereksiz kanal oynaması, ilerleme kaybı değil ama zaman kaybı).
   - Tarayıcıdaki kanal **aynı kampanyada drop'lu** → ilerleme o kanala kredi olur; kullanıcı izlemeyi bırakınca bizimkine döner.
2. İlerleme **geri gitmez** (sunucu sayacı kümülatif); asıl risk zaman kaybı ve motorun yanlış teşhisle kanal değiştirmesi.
3. Kick için aynı mantık: gizli WebView2 oynatıcı Kick'in "izleyici oturumu"nu kullanıyor; aynı hesapla tarayıcıdan başka kanal izlemek oturumu bozabilir (doğrulanmadı).

### Önerilen önlemler (öncelik sırasıyla)
1. **Başlat'ta bilgi penceresi** (istendi; tasarım oturumunda uygulanıyor): "Çalışırken aynı hesapla başka yayın izlemeyin." + "Bir daha gösterme".
2. **Yabancı oturum tespiti (öneri, uygulanmadı):** `dropCurrentSession.channel` bizim kanalımızdan farklıysa (ya da oturum boşalıp tarayıcıda izleme sürüyorsa) motor **kanal engellemesin**; pano "Başka bir yayın izleniyor — uygulama bekliyor" desin, 2–3 yoklama sonra oturumu geri almak için kendi kanalına yeniden bağlansın. Kanal engelleme sayacı yalnız *gerçek* takılmada (oturum bizim kanalımızda ama dakika artmıyor) işlesin. Küçük Core değişikliği (`DropEngine` + test).
3. **Kullanıcıya pratik öneri:** manuel izleme için ayrı tarayıcı profili ya da *başka bir Twitch hesabı* kullanın (aynı hesapta oturum paylaşımı sorunu).
4. **Canlı doğrulama (isteğe bağlı, kullanıcıyla):** aktif bir drop'lu kampanyada uygulama izlerken tarayıcıda başka kanal aç → pano ve günlükte `dropCurrentSession.channel` değişimini gözle; sonucu bu dosyaya işle.

## 2. İki farklı hesap (ör. 2 Twitch hesabı) aynı anda olur mu? (istek #17)

### Teknik olabilirlik (bizim mimari)
- **Evet, mimari buna yakın:** her platform için ayrı `DropperService` örneği zaten var (Twitch + Kick). İkinci Twitch hesabı = ikinci `DropperService` örneği; gerekenler: **ayrı sır anahtar ön eki** (`secrets.dat` içinde `twitch2.*`), **ayrı `state.json`** (`state-twitch2.json`), ortak `history.jsonl` (alan: hesap kimliği), hesap başına ayrı `TwitchSessionManager`/PubSub bağlantısı, arayüzde hesap seçici/sekme.
- **Kaynak maliyeti:** Twitch HLS izleme hesap başına ~2 MB/dk + ~birkaç MB bellek → **ucuz** (iki hesap ≈ +%1 CPU). **Kick** izlemesi gizli WebView2 oynatıcı: ~0,7 GB RAM ve %20–50 çekirdek **hesap başına** (ayrı WebView2 profili şart) → ikinci Kick hesabı pratikte **pahalı** (≈1,5 GB).
- **Hız sınırı:** istekler IP başına; iki hesap aynı `SlidingWindowRateLimiter`'ı paylaşmalı (Twitch GQL zaten 2 hesapta da makul).
- **Kanal çakışması:** iki hesap **aynı kanalı** izleyebilir (farklı oturumlar), ama aynı IP'den aynı kanala çok sayıda "izleyici" olarak görünmek şüphe artırır; planlayıcı hesaplar arasında kanalları **dağıtmalı** (hesap başına tek aktif kanal kuralı hesap başına geçerli).

### Pratik değer (önemli)
- **Facepunch:** her **Steam hesabı yalnız bir Twitch hesabına** bağlı olabilir ve eşyalar **Rust'ın kitaplıkta olduğu Steam hesabına** düşer. İkinci Twitch hesabının faydası yalnız **ikinci bir Rust sahibi Steam hesabı** (aile/arkadaş/ikinci hesap) varsa vardır. Aksi halde ikinci hesabın drop'ları teslim edilemez (180 gün içinde Rust alınırsa edilebilir).
- Genel drop'lar hesap başına ayrı sayaç işletir → iki hesap = iki ayrı set ödül (yalnız farklı Steam hesaplarına).

### Risk
- **[Düşük güven, tek ticari blog]** aynı IP'den çok hesapla drop kazanmanın hesap kısıtlamasına yol açabileceği öne sürülüyor; Twitch'ten resmî eşik/politika açıklaması **bulunamadı**. Referans araçlardan **Alorf/TwitchDropsBot** çoklu hesabı destekliyor, **TwitchDropsMiner** tek hesap; bu iki yaklaşımın hiçbirinde doğrulanmış toplu ban raporu bulmadık (aynı sınırlı kanıt düzeyi — docs/04 gibi).
- Twitch/Kick koşulları otomasyonu zaten kısıtlıyor; çoklu hesap bu riski **artırır** (özellikle aynı IP'de). Kick tarafı için docs/04'teki belirsizlik geçerli.
- Tek hesap, tek IP, tek aktif kanal bu projenin bilinçli tasarım kararı (v1 = 1 Twitch + 1 Kick).

### Öneri
1. **Şimdi yapma** (v1 kararı korunsun).
2. İleride istenirse **Twitch-only, 2 hesap** (hesap başına ayrı sır/durum/geçmiş + hesap seçici); tahmini iş: orta (2–3 dilim; `DropperService` tekrar kullanımı sayesinde çekirdek değişmez). **Kick'te çoklu hesap önerilmez** (bellek/risk).
3. Önce kullanıcıya sor: ikinci hesabın bağlanacağı **ikinci bir Rust'lu Steam hesabı var mı?** Yoksa çoklu hesabın faydası yok.

## Kaynaklar
- Facepunch Drops SSS: <https://twitch.facepunch.com/> (aynı anda tek kanal; Steam↔Twitch bağlantı kuralı) ve <https://kick.facepunch.com/>
- TwitchDropsMiner (Docker sürümü) belgesi — aynı hesapla başka yayın izleme uyarısı: <https://hub.docker.com/r/journeyover/twitchdropsminer>
- TwitchDropsMiner issue #462 (drop sayılmama, anekdot): <https://git.tdem.in/DevilXD/TwitchDropsMiner/issues/462>
- Çoklu yayın/hesap ve IP ayrımı (ticari blog; düşük güven): <https://app.cyberyozh.com/pt/blog/can-you-watch-multiple-twitch-streams-for-drops/>
- Bu projede: `docs/PLATFORM-NOTES.md` (U1, U4, referans analizi), `docs/04-kick-risk-arastirmasi.md`.
