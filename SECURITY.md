# Security Policy / Güvenlik Politikası

## English

If you find a security vulnerability in Rust Droper (for example: a way to leak or steal the stored sign-in data, a log line that exposes a token, code execution through crafted API responses or media files):

- **Please do not open a public issue.** Report it privately via GitHub's *Report a vulnerability* button on the repository's **Security** tab (private advisory), or e-mail **thepinhan@gmail.com** with the subject `Rust Droper security`.
- Include the version, steps to reproduce and the impact. **Never send real tokens, passwords or cookies.**
- This is a one-person, best-effort project: you will normally get a first reply within about a week, and a fix in a new release when the report is confirmed. Reporters are credited unless they prefer otherwise.

Supported versions: only the **latest release** receives fixes.

Scope notes: the app stores Twitch/Kick session keys encrypted with Windows DPAPI under `%LOCALAPPDATA%\RustDroper`; anyone who can run code as your Windows user can use them — that is inherent to DPAPI and not a vulnerability in Rust Droper. Platform-terms (ToS) risk is described in the README and is not a security vulnerability.

## Türkçe

Rust Droper'da bir güvenlik açığı bulursanız (ör. saklanan oturum verisini sızdırma/çalma yolu, token ifşa eden bir günlük satırı, özel hazırlanmış API yanıtı veya medya dosyasıyla kod çalıştırma):

- **Lütfen herkese açık issue açmayın.** Depodaki **Security** sekmesinde GitHub'ın *Report a vulnerability* düğmesiyle (özel danışma) ya da **thepinhan@gmail.com** adresine `Rust Droper security` konusuyla e-posta ile bildirin.
- Sürümü, yeniden üretme adımlarını ve etkiyi yazın. **Gerçek token, parola veya çerez göndermeyin.**
- Bu, tek kişilik ve "elinden geldiğince" bir projedir: genellikle yaklaşık bir hafta içinde ilk yanıt, doğrulanan bildirimler için yeni bir sürümde düzeltme verilir. İsterseniz adınız teşekkür olarak anılır.

Desteklenen sürümler: yalnızca **son sürüm** düzeltme alır.

Kapsam notları: uygulama Twitch/Kick oturum anahtarlarını `%LOCALAPPDATA%\RustDroper` altında Windows DPAPI ile şifreli saklar; Windows kullanıcınız olarak kod çalıştırabilen biri bunları kullanabilir — bu DPAPI'nin doğasıdır, Rust Droper açığı değildir. Platform koşulları (ToS) riski README'de anlatılmıştır ve güvenlik açığı sayılmaz.
