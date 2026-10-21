# Contributing / Katkı

[English](#english) · [Türkçe](#türkçe)

## English

Thanks for your interest! Rust Droper is a small, best-effort hobby project, so please keep changes focused.

1. **Open an issue first** (bug report or feature request) and wait for a reply before spending time on a pull request. Bugs: use the *Bug report* template (version, Windows version, steps, log lines — **never paste passwords, tokens, cookies or your sign-in files**). Security problems: see [SECURITY.md](SECURITY.md), not a public issue.
2. **Scope:** Rust drops on Twitch and Kick, one Twitch + one Kick account, Windows. Other games / multi-account are out of scope by design.
3. **Build and test:** .NET 10 SDK, then `dotnet build RustDroper.slnx -c Release` (0 warnings; warnings are errors) and `dotnet test RustDroper.slnx -c Release`. Set `RUSTDROPER_SKIP_WEBVIEW2=1` if you have no WebView2/desktop. Tests must not use real accounts or secrets; live tests are opt-in (`RUSTDROPER_LIVE=1`).
4. **Two languages (required):** every user-visible string is added/changed/removed in **Turkish and English together** (`Loc` tables, see [docs/02-mimari.md](docs/02-mimari.md) §4.8). User docs: update `docs/USAGE.md` **and** `docs/USAGE.tr.md`; README: `README.md` **and** `README.tr.md`.
5. **Version + changelog (required for every change that reaches users):**
   - add an entry (Turkish + English) to `src/RustDroper.App/Resources/changelog.json`,
   - bump `<Version>` in `Directory.Build.props` with the right Semantic Versioning kind — **MAJOR** = architecture / big refactor / backwards-incompatible, **MINOR** = new feature, page or module, **PATCH** = bug fix, CSS, performance, security, text, small UX,
   - run `powershell -File build\changelog.ps1` to regenerate `CHANGELOG.md` / `CHANGELOG.tr.md`.

   The tests (`ChangelogRulesTests`) fail if an entry is missing or the bump kind is wrong. Details and an example: [docs/08-surum-ve-degisiklik-kaydi.md](docs/08-surum-ve-degisiklik-kaydi.md).
6. **Secrets:** never log or commit tokens, passwords or cookies. Keep test fixtures free of real identities.
7. Be kind: see the [Code of Conduct](CODE_OF_CONDUCT.md). Contributions are licensed under the [MIT License](LICENSE).

## Türkçe

İlgin için teşekkürler! Rust Droper küçük, "elinden geldiğince" ilerleyen bir hobi projesidir; lütfen değişiklikleri odaklı tut.

1. Pull request'e zaman harcamadan önce **önce bir issue aç** ve yanıt bekle. Hata bildirimi için *Bug report* şablonunu kullan (sürüm, Windows sürümü, adımlar, günlük satırları — **parola, token, çerez veya oturum dosyalarını asla paylaşma**). Güvenlik sorunları için herkese açık issue değil, [SECURITY.md](SECURITY.md).
2. **Kapsam:** Twitch ve Kick'te Rust drop'ları, bir Twitch + bir Kick hesabı, Windows. Başka oyun / çoklu hesap bilinçli olarak kapsam dışıdır.
3. **Derleme ve test:** .NET 10 SDK, sonra `dotnet build RustDroper.slnx -c Release` (0 uyarı; uyarılar hata sayılır) ve `dotnet test RustDroper.slnx -c Release`. WebView2/masaüstü yoksa `RUSTDROPER_SKIP_WEBVIEW2=1`. Testler gerçek hesap/sır kullanmamalı; canlı testler isteğe bağlıdır (`RUSTDROPER_LIVE=1`).
4. **İki dil (zorunlu):** kullanıcıya görünen her metin **Türkçe + İngilizce birlikte** eklenir/değiştirilir/silinir (`Loc` tabloları, bkz. [docs/02-mimari.md](docs/02-mimari.md) §4.8). Kullanıcı belgeleri: `docs/USAGE.md` **ve** `docs/USAGE.tr.md`; README: `README.md` **ve** `README.tr.md`.
5. **Sürüm + değişiklik kaydı (kullanıcıya yansıyan her değişiklik için zorunlu):**
   - `src/RustDroper.App/Resources/changelog.json`'a (Türkçe + İngilizce) giriş ekle,
   - `Directory.Build.props` içindeki `<Version>` değerini doğru Semantic Versioning türüyle artır — **MAJOR** = mimari / büyük refaktör / geriye uyumsuz, **MINOR** = yeni özellik, sayfa veya modül, **PATCH** = hata düzeltme, CSS, performans, güvenlik, metin, küçük UX,
   - `CHANGELOG.md` / `CHANGELOG.tr.md` dosyalarını üretmek için `powershell -File build\changelog.ps1` çalıştır.

   Giriş eksikse ya da artış türü yanlışsa testler (`ChangelogRulesTests`) başarısız olur. Ayrıntı ve örnek: [docs/08-surum-ve-degisiklik-kaydi.md](docs/08-surum-ve-degisiklik-kaydi.md).
6. **Sırlar:** token, parola, çerezi asla günlüğe yazma veya commit'leme. Test verilerinde gerçek kimlik olmasın.
7. Nazik ol: [Davranış Kuralları](CODE_OF_CONDUCT.md). Katkılar [MIT Lisansı](LICENSE) ile lisanslanır.
