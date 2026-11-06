# Development guide — read this before changing anything

> Turkish version: [DEVELOPMENT.tr.md](DEVELOPMENT.tr.md). Short rules for AI assistants: [../AGENTS.md](../AGENTS.md).

Rust Droper collects **Rust** drops on **Twitch and Kick** from a Windows tray app (C# / .NET 10 / WPF).
This file is the hand-over note of the original maintainer. It records what is verified, what is **not**, and the traps that are not obvious from the code.

## 1. State of the project

- **v1.0.1 is a finished, tested, working baseline** (build: 0 warnings; ~535 Core + ~320 App tests green; CI green). The maintainer has paused development. Treat the code as stable; make small, tested changes.
- Platforms change without notice (Twitch GraphQL hashes, Kick endpoints, Facepunch page layout). **Most future bugs will be "the platform changed", not "the code rotted".** See §5.
- Scope is deliberate: Rust only, one Twitch + one Kick account, Windows only. Multi-account and other games were rejected on purpose (see [06-arastirma-eszamanli-izleme-ve-coklu-hesap.md](06-arastirma-eszamanli-izleme-ve-coklu-hesap.md)).

## 2. Build, test, run

```powershell
dotnet build RustDroper.slnx -c Release      # must be 0 warnings (warnings are errors)
dotnet test  RustDroper.slnx -c Release      # set RUSTDROPER_SKIP_WEBVIEW2=1 on machines without a desktop/WebView2
powershell -File build\publish-portable.ps1  # self-contained single-file exe (~127 MB) under publish\
powershell -File build\changelog.ps1         # regenerate CHANGELOG.md / CHANGELOG.tr.md (see §4)
```

- SDK: .NET 10. Live tests are opt-in (`RUSTDROPER_LIVE=1`); normal tests never touch real accounts or the network.
- A running `RustDroper.exe` locks the build output: close it before `dotnet build`.
- Dev data folder: `%LOCALAPPDATA%\RustDroper` (hidden). Override with `RUSTDROPER_DATA=<folder>` — **always use a throw-away folder when experimenting** so you never touch a real signed-in profile.
- Other env vars: `RUSTDROPER_RENDER=hardware`, `RUSTDROPER_MEMORY_LOG_SECONDS`, `RUSTDROPER_PLAYER_MODE=visible` (show the hidden Kick player), `RUSTDROPER_PLAYER_ARGS`, `RUSTDROPER_PLAYER_GPU=1`, `RUSTDROPER_SOAK_SCALE=N`, `RUSTDROPER_SKIP_WEBVIEW2=1`, `RUSTDROPER_SKIP_STARTUP`.
- CLI for probing platforms without the UI: `dotnet run --project src/RustDroper.Cli -- <command> <twitch|kick> ...` with commands such as `login`, `whoami`, `logout`, `campaigns`, `scan`, `watch`, `run`, `claim`, `progress` (Kick), plus `changelog [--check]` (e.g. `campaigns twitch rust --probe 100`). Usage lines are in `src/RustDroper.Cli/Program.cs`; its console output is Turkish. Do not run the CLI and the app at the same time against the same data folder (token refresh races).

## 3. Code map

| Area | Where | Notes |
|---|---|---|
| Domain model | `src/RustDroper.Core/Domain` | platform-neutral campaigns/drops/inventory |
| Twitch | `Core/Platforms/Twitch` | GraphQL (hashed persisted queries), PubSub, device-code login, HLS watcher, claim fallback |
| Kick | `Core/Platforms/Kick`, `Core/Watching/KickWebViewWatcher.cs`, `App/Player` | login by phone QR, hidden WebView2 real player |
| Engine | `Core/Orchestration` | `DropEngine` (watchdog, switching, stall handling), `DropPlanner` (what to watch), `DropperService` (account + engine lifecycle), `EngineStateStore` |
| Rust filter | `Core/Platforms/RustCampaignFilter.cs` | what counts as a Rust campaign (setting `RustRewardsOnly`) |
| Drop art / event info | `Core/Media` | scrapes twitch.facepunch.com / kick.facepunch.com (secondary source, tolerant parsers) |
| Paths / migration | `Core/Infrastructure` | `AppPaths`, `LegacyDataMigrator`, atomic JSON stores |
| Localization | `Core/Localization/Loc.cs`, `App/Localization` | in-code tr/en tables, no satellite assemblies |
| UI | `src/RustDroper.App` | WPF, `ViewModels`, `Views`, `Themes`, tray icon is our own Win32 wrapper |
| Crash protection | `App/Infrastructure/KeepAlive.cs` | scheduled task `RustDroperKeepAlive`, `run-state.json` |
| Changelog | `App/Resources/changelog.json` (single source), `Core/Changelog` | |

Design decisions and measurements: [02-mimari.md](02-mimari.md) (Turkish). Platform behaviour: [PLATFORM-NOTES.md](PLATFORM-NOTES.md). Kick risk: [04-kick-risk-arastirmasi.md](04-kick-risk-arastirmasi.md). UI: [05-arayuz-tasarimi.md](05-arayuz-tasarimi.md).

## 4. Rules you must follow (tests enforce several)

1. **Two languages.** Every user-visible string is added/changed/removed in **Turkish and English together** (`Loc` tables). To add a string: `Loc.T("key", args)` in code or `{loc:Loc key}` in XAML, plus one `(key, Türkçe, English)` row in `CoreStrings.*.cs` (Core) or `AppStrings.*.cs` (App); never build keys by string concatenation. Details: [02-mimari.md](02-mimari.md) §4.8. Tests check that both exist, placeholder counts match and the English text contains no Turkish letters. Docs: `USAGE.md` + `USAGE.tr.md`, `README.md` + `README.tr.md`, `CHANGELOG.md` + `CHANGELOG.tr.md`.
2. **Version + changelog for every user-facing change** ([08-surum-ve-degisiklik-kaydi.md](08-surum-ve-degisiklik-kaydi.md)): add a TR+EN entry to `changelog.json`, bump `<Version>` in `Directory.Build.props` (MAJOR = architecture / breaking, MINOR = new feature/page/module, PATCH = bug, CSS, performance, security, text, small UX), run `build\changelog.ps1`. `ChangelogRulesTests` fails if you forget.
3. **Secrets:** never log, print or commit tokens/passwords/cookies. Tokens live DPAPI-encrypted in `secrets.dat`. The app never receives a password (device-code login for Twitch, phone QR for Kick, a visible WebView2 window for the Kick player login). Tests must not use real accounts.
4. **Warnings are errors.** Do not suppress warnings or skip/delete tests to get green.
5. **Do not change two things at once** in the engine: the engine/watching code has time-based tests (fake clocks, soak tests). Change one thing, run the whole suite.

## 5. Known traps (not obvious from the code)

**Twitch**
- Discovery is **channel-based**: `DirectoryPage_Game` (Drops-enabled Rust channels) → per-channel `AvailableDrops`. The dashboard/details campaign queries return null for this client profile (`SMARTBOX_TV`; other client profiles are blocked or 401).
- Most "Drops Enabled" channels carry **no** Rust campaign (a site-wide sub campaign tags many channels). The real event campaign appears only on participating channels (≈8 of 100 observed). Discovery therefore scans up to 100 channels and stops after 8 eligible ones (v1.0.1 fix; stopping at the first ~6 caused "Idle" at event start). Do not reduce the probe depth without re-measuring: `dotnet run --project src/RustDroper.Cli -- campaigns twitch rust --probe 100`.
- Sub-only campaigns (`requiredSubs > 0`) are never watched. "General" vs "streamer" scope is decided among channels that carry drops.
- GraphQL uses **persisted-query hashes** (`GqlOperations` in `TwitchGqlTypes.cs`, `DefaultHash` per operation). If Twitch rotates one you get `PersistedQueryNotFound`-style errors: fix by capturing the new hash. Automatic hash discovery was planned but **not implemented**.
- Claim: for Twitch-native rewards the reward is granted when the counter reaches the threshold even without a claim event. `TwitchClaimFallback` builds the id (`{userId}#{campaignId}#{dropId}`) 30 s after completion if no `drop-claim` event arrives.
- Reward-class (streamer reward) campaigns keep counting after the threshold; the engine's rule "last drop reached + claim wait elapsed ⇒ campaign done" exists because of that — do not remove it.
- Lowest HLS quality can equal the source on small channels (≈2.5 GB/h); the watcher falls back to audio-only when a video rendition is > 2 Mbps.
- Reward names differ between sources (Facepunch "Rug" vs Twitch "Twitch Rivals Spooky Rug"); `DropNames.Key` matches tolerantly. Cosmetic duplicates are possible.

**Kick**
- Plain HLS traffic gives **no** drop progress (token carries only a channel claim, not the user). Progress requires the real web player, hence the hidden WebView2 player (`KickWebViewWatcher`, off-screen window, muted, lowest quality, `--disable-gpu`). Memory ≈ 0.7–0.8 GB for WebView2 while watching.
- Kick login is via a link opened with the **phone's Kick app** (QR); the same link 404s in a desktop browser. The Kick player needs its own one-time sign-in (separate WebView2 profile `webview2\`).
- Server counter advances in 2-unit steps about every 2 min. Kick auto-claim has **never been verified live**.
- Cloudflare blocks unusual clients: keep the browser-like User-Agent / HTTP versions as in `KickConstants`; non-JSON 403 = block/transient (token is kept), JSON 403 = invalid token.

**Tests**
- Some tests use the **real clock** against fixture campaigns with fixed dates. They break when the calendar passes those dates even though the code is fine (happened on 2026-11-06: Kick fixtures ended 2026-10-30, fixed by moving them to 2036). If CI turns red with no code change, check fixture dates first and give fixtures far-future end dates or a `FakeTimeProvider`.

**App / Windows**
- Window close destroys the window and trims memory (tray memory ≈ 55–65 MB). WPF is forced to software rendering (`SoftwareOnly`) because hardware rendering leaked a few MB per open/close. Do not re-enable casually.
- Shared `SolidColorBrush` resources get frozen by templates → animating a brush colour throws or does nothing; replace the brush instead.
- WPF allows one `Application` per process: UI preview tests that render screens run in a separate process and are opt-in (`RUSTDROPER_PREVIEW_*`).
- Single-instance mutex is derived from the data folder. Tests must set `RUSTDROPER_DATA`/skip startup or they will touch real data.
- Lock screen does **not** pause watching; sleep does (the app resumes after wake). Windows shutdown freezes state and resumes at next boot.
- Crash protection: while running the app writes `run-state.json {Running, CleanExit}` and installs a scheduled task every 5 min (`RustDroper.exe --keepalive`). Pause/Exit removes it. A user-closed app is never restarted.

## 6. Verified vs NOT verified

Verified live (real accounts): Twitch login, inventory, discovery, HLS progress, PubSub, claim mutation and fallback (Twitch-native badges), Kick QR login, Kick progress through the hidden player, crash-restart chain, persistent login, 4-hour soak, tray memory targets.

**Not verified — treat with care:**
- Delivery of a Rust reward **into the game** after claim (Facepunch/Steam) and whether the `drop-claim` event arrives for it.
- Whether several drops of one campaign progress in parallel or sequentially (the UI assumes parallel for time estimates).
- Kick auto-claim, Kick on very long (≥ 2 h) watches, Kick mature-content gate.
- English-language Windows `auto` language path (unit-tested only), real lock-screen behaviour on Kick (Windows may throttle an occluded WebView2).
- The Facepunch page parsers depend on page structure; if it changes the cards fall back to placeholders built from campaign data (the app keeps working).

## 7. "If you change X, check Y"

| Change | Also check |
|---|---|
| Any UI text | both `Loc` tables, `USAGE*.md`, screenshots in `docs/screenshots` if layout changed |
| Discovery / planner | `TwitchCampaignDiscoveryTests`, `DropPlannerTests`, live `campaigns twitch rust --probe 100` |
| Engine / watching | soak + engine tests with fake clocks; never loosen their bounds |
| Settings (`AppSettings`) | back-compat with old `settings.json` (migration flags such as `AutoClaimDefaultMigrated`) |
| Paths / data | `AppPaths`, `LegacyDataMigrator`, tests must use a temp data dir |
| Kick player | `PlayerMonitor` tests, keep the page agent read-only (it must not read cookies/tokens/localStorage) |
| Anything user-visible | changelog entry + SemVer bump (rule 2) |

## 8. Releasing

1. Make the change, tests green, changelog + version bump, `build\changelog.ps1`.
2. Commit and push to `main`; wait for the `ci` workflow.
3. Tag `vX.Y.Z` (same as `<Version>`) and push the tag: the `release` workflow builds, tests, runs `build\package-release.ps1` and publishes `RustDroper-X.Y.Z-win-x64.zip` + `SHA256SUMS.txt` to GitHub Releases.
4. Users must download the zip from **Releases**, not "Code → Download ZIP" (that has no exe).

Screenshots in `docs/screenshots` come from `powershell -File build\ui-screens.ps1 -Exe <RustDroper.exe> -OutDir <folder> -Language tr|en` (uses a throw-away data folder, never real data). A few other helper scripts of the original maintainer (memory measurement, live-test launchers) are **not** in this repository; rewrite them if you need them — the measured targets are in [02-mimari.md](02-mimari.md) §2.
