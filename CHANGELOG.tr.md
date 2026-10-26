# Değişiklik Günlüğü

Rust Droper'daki tüm önemli değişiklikler burada, en yeniden eskiye listelenir. Sürümler [Anlamsal Sürümleme](https://semver.org/lang/tr/) kuralını izler: **MAJOR** = mimari değişiklik, büyük refaktör, geriye uyumsuz değişiklik; **MINOR** = yeni özellik, sayfa, modül, kullanıcı fonksiyonu; **PATCH** = hata düzeltme, CSS, performans, güvenlik, metin ve küçük UX düzeltmeleri.

> Bu dosya `src/RustDroper.App/Resources/changelog.json` içinden üretilir (`powershell -File build\changelog.ps1`). Elle düzenlemeyin. İngilizce sürüm: [CHANGELOG.md](CHANGELOG.md).

## [1.0.1] — 2026-10-26 · PATCH · Hata düzeltme / Kampanya keşfi

Etkinlik başladığında uygulamanın 'Boşta' kalmasına yol açan kampanya keşfi hatası düzeltildi: drop'u olan yayınlar artık yayın listesinde daha derin aranıyor.

### Değişenler

- Keşif, 'Drops Enabled' yayın listesinin ilk birkaç kanalıyla yetinmiyor; drop'u olan yeterli kanal bulunana dek (en çok 100 kanal) tarıyor ve bulunan kanalları sonraki taramada önce kontrol ediyor
- Kampanya kapsamı (genel/yayıncı) artık yalnızca drop'u olan kanallara göre belirleniyor

### Düzeltilenler

- Etkinlik başlamış olduğu halde uygulama 'Boşta' kalıp hiçbir yayını izlemiyordu; çünkü etkinlik kampanyasını taşıyan kanallar yayın listesinde ilk sıralarda değildi

## [1.0.0] — 2026-10-20 · BASELINE · İlk sürüm

Rust Droper'ın ilk kararlı sürümü ve bundan sonraki tüm sürümlerin referansı (baseline). Rust oyununun Twitch ve Kick drop'larını arka planda toplar.

### Eklenenler

- Rust'ın Twitch ve Kick drop kampanyalarını sistem tepsisinden arka planda kendiliğinden takip eder
- Twitch'te izleme gerçek yayın akışıyla (tarayıcısız), Kick'te gizli bir tarayıcı oynatıcısıyla yapılır
- Her platform için ayrı ekran ve başlıkta Twitch ⇄ Kick geçişi
- Drop kartı ızgarası: görsel, ilerleme çubuğu ve durumlar (sırada, izleniyor, claim'e hazır, claim edildi)
- Otomatik claim (varsayılan olarak açık) ve claim edilen ödüller bölümü
- Yayıncı drop'ları için sürükle-bırak öncelik sırası
- Etkinlik kartı: Facepunch etkinliğinin adı, tarihleri ve geri sayımı
- Yalnız Rust ödülü veren kampanyaları izleyen filtre (Ayarlar'dan kapatılabilir)
- Çökme koruması: beklenmedik kapanışta uygulama kendiliğinden yeniden açılıp kaldığı yerden devam eder
- İnternet kesintisi ve bilgisayar uykusu sonrası otomatik toparlanma; çalışırken uykuya geçmeyi engelleme
- Sağlık kontrolü sayfası: giriş, bağlantı, kampanya keşfi ve ayarların durumunu gösterir
- Tepsi simgesi, menü ve bildirimler (claim edildi, giriş gerekli, kampanya başladı, bağlantı kesildi)
- Türkçe ve İngilizce arayüz; Windows diline göre otomatik seçim ve anında dil değiştirme
- Hakkında ve Güncelleme notları sayfaları
- Başlat'a basınca, aynı hesapla başka bir canlı yayın izlememeyi hatırlatan uyarı
- Taşınabilir tek dosya: kurulum gerektirmez; oturum bilgileri yalnız bu bilgisayarda Windows hesabına bağlı şifreyle (DPAPI) saklanır
