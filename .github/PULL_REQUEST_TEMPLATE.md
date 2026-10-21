## What / Ne

<!-- What does this change and why? Link the issue it belongs to (open an issue first). / Ne değişiyor ve neden? İlgili issue'yu bağlayın (önce issue açın). -->

Closes #

## Checklist / Kontrol listesi

- [ ] `dotnet build RustDroper.slnx -c Release` → 0 warnings / 0 uyarı
- [ ] `dotnet test RustDroper.slnx -c Release` → green / yeşil
- [ ] User-visible text added/changed in **Turkish and English** (`Loc` tables) / Kullanıcıya görünen metin **Türkçe + İngilizce** (Loc tabloları)
- [ ] `changelog.json` entry (TR + EN) + correct SemVer bump in `Directory.Build.props` + `build\changelog.ps1` run ([docs/08](../docs/08-surum-ve-degisiklik-kaydi.md)) — or: *no user-visible change* / veya: *kullanıcıya yansıyan değişiklik yok*
- [ ] Docs updated in both languages where relevant (`docs/USAGE*.md`, `README*.md`) / İlgili belgeler iki dilde güncellendi
- [ ] No secrets, tokens, real account names or personal data in code, tests, fixtures or logs / Kod, test, fixture veya günlükte sır/gerçek hesap adı yok
