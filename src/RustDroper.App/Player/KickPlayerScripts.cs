namespace RustDroper.App.Player;

/// <summary>
/// Kick sayfasına enjekte edilen küçük "ajan" betiği: YALNIZCA oynatıcının durumunu okur (video öğesi, kanalın canlı olup olmadığı,
/// üst çubukta "Giriş Yap" düğmesi var mı) ve ilk-açılış kapılarını (çerez bandı, olgun içerik onayı) kapatır.
/// Kimlik bilgisi, çerez, belirteç, localStorage okumaz/yazmaz; hiçbir şey Kick dışına gönderilmez.
/// </summary>
internal static class KickPlayerScripts
{
    /// <summary>Her belgede (yalnız üst çerçevede) tanımlanır; <c>window.__rdAgent</c> altında yöntemler sunar.</summary>
    public const string Agent = """
        (() => {
          if (window.top !== window || window.__rdAgent) return;
          const S = { live: null, liveSlug: null, liveAt: 0, offlineChecks: 0, checking: false };
          const pickVideo = () => {
            const vs = [...document.querySelectorAll('video')];
            return vs.find(v => v.classList.contains('player-no-controls'))
              || vs.filter(v => v.readyState > 0).sort((a, b) => (b.videoWidth * b.videoHeight) - (a.videoWidth * a.videoHeight))[0]
              || vs[0] || null;
          };
          const txt = e => (e.innerText || '').trim();
          const authState = () => {
            const loginRe = /^(giriş yap|giriş|log in|login|sign in)$/i;
            const signupRe = /^(kaydol|sign up|register)$/i;
            const items = [...document.querySelectorAll('button, a')];
            const out = items.some(e => loginRe.test(txt(e)) || signupRe.test(txt(e)));
            if (out) return 'out';
            const header = document.querySelector('input[type=search], input[type=text][placeholder]');
            return header ? 'in' : 'unknown';
          };
          const dismissGates = () => {
            const did = [];
            const cookie = document.querySelector('.onetrust-close-btn-handler');
            if (cookie && cookie.offsetParent !== null) { cookie.click(); did.push('cookie'); }
            for (const d of document.querySelectorAll('[role=dialog], [role=alertdialog], dialog[open]')) {
              if (!/mature|18\+|yetişkin|olgun|age restricted|yaş/i.test(d.innerText || '')) continue;
              const b = [...d.querySelectorAll('button')].find(b => /^(start watching|izlemeye başla|continue|devam|i am 18|18 yaşında|evet|yes)/i.test(txt(b)));
              if (b) { b.click(); did.push('mature'); }
            }
            return did;
          };
          const checkLive = async slug => {
            S.checking = true;
            try {
              const r = await fetch('/api/v2/channels/' + encodeURIComponent(slug), { credentials: 'include', headers: { accept: 'application/json' } });
              if (r.ok) {
                const j = await r.json();
                const live = !!(j && j.livestream && j.livestream.is_live !== false);
                S.live = live;
                S.offlineChecks = live ? 0 : S.offlineChecks + 1;
              }
            } catch (e) { /* ağ hatası: bilinmiyor, canlı/kapalı sayacına dokunma */ }
            S.liveAt = Date.now();
            S.checking = false;
          };
          const center = e => { if (!e) return null; const r = e.getBoundingClientRect(); return r.width > 0 ? { x: r.left + r.width / 2, y: r.top + r.height / 2 } : null; };
          window.__rdAgent = {
            tick(slug) {
              const v = pickVideo();
              const gates = dismissGates();
              if (S.liveSlug !== slug) { S.liveSlug = slug; S.live = null; S.liveAt = 0; S.offlineChecks = 0; }
              if (!S.checking && Date.now() - S.liveAt > 20000) { checkLive(slug); }
              return {
                path: location.pathname, ready: document.readyState, vis: document.visibilityState,
                auth: authState(), live: S.live, offlineChecks: S.offlineChecks, gates,
                v: v ? { paused: v.paused, ended: v.ended, rs: v.readyState, ct: v.currentTime, w: v.videoWidth, h: v.videoHeight } : null
              };
            },
            auth() { return authState(); },
            debugControls() {
              const v = pickVideo();
              return JSON.stringify({
                testids: [...document.querySelectorAll('[data-testid]')].map(e => e.getAttribute('data-testid')).filter(t => /player|video|control/i.test(t)).slice(0, 30),
                hovered: [...document.querySelectorAll(':hover')].map(e => e.tagName + '.' + String(e.className && e.className.baseVal === undefined ? e.className : '').slice(0, 30)).slice(-4),
                hasFocus: document.hasFocus(), videoRect: v ? JSON.stringify(v.getBoundingClientRect()) : null, win: [innerWidth, innerHeight]
              });
            },
            videoCenter() { return center(pickVideo()); },
            gear() {
              const fs = document.querySelector('[data-testid=video-player-fullscreen]');
              if (!fs || !fs.parentElement) return null;
              const btns = [...fs.parentElement.querySelectorAll('button')];
              return center(btns[btns.length - 1]);
            },
            qualityItems() {
              return [...document.querySelectorAll('[role=menuitemradio]')].map(i => ({ t: txt(i).replace(/\s+/g, ' '), checked: i.getAttribute('aria-checked') === 'true', c: center(i) }));
            }
          };
        })();
        """;
}
