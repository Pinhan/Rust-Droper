# AGENTS.md — instructions for AI coding assistants (and humans)

Rust Droper: Windows tray app (C# / .NET 10 / WPF) that collects **Rust** drops on **Twitch and Kick**. Version 1.0.x is a finished, tested baseline; development is paused, so changes are bug fixes for platform changes.

**Read first:** [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) (English) or [docs/DEVELOPMENT.tr.md](docs/DEVELOPMENT.tr.md) (Türkçe) — code map, known traps, what is verified vs not, "if you change X check Y". The user speaks Turkish: reply in Turkish unless asked otherwise.

## Commands

```powershell
dotnet build RustDroper.slnx -c Release   # 0 warnings required (warnings are errors)
dotnet test  RustDroper.slnx -c Release   # set RUSTDROPER_SKIP_WEBVIEW2=1 if there is no desktop/WebView2
```

Close a running `RustDroper.exe` before building. Use a throw-away `RUSTDROPER_DATA=<temp folder>` for any experiment; never touch the real `%LOCALAPPDATA%\RustDroper` profile.

## Rules (tests enforce several)

1. **Every user-visible string in Turkish AND English together** (`Loc` tables; `docs/02-mimari.md` §4.8). Docs come in pairs: `README.md`/`README.tr.md`, `docs/USAGE.md`/`USAGE.tr.md`, `CHANGELOG.md`/`CHANGELOG.tr.md`.
2. **Every user-facing change = changelog + version bump:** TR+EN entry in `src/RustDroper.App/Resources/changelog.json`, bump `<Version>` in `Directory.Build.props` (MAJOR architecture/breaking · MINOR new feature/page/module · PATCH bug/CSS/perf/security/text/small UX), run `powershell -File build\changelog.ps1`. See `docs/08-surum-ve-degisiklik-kaydi.md`.
3. **Never log, print or commit** tokens, passwords, cookies or sign-in files. No real accounts in tests.
4. **Do not weaken the bar:** no suppressed warnings, no skipped/deleted/loosened tests to reach green.
5. Change one thing at a time in engine/watching code (time-based tests); run the whole suite.
6. Commit/push/tag only when the user asks. Releases: tag `vX.Y.Z` after CI is green; the workflow publishes the zip.

## Notes for AI assistants (tooling quirks learned the hard way)

- The user speaks **Turkish**; wants small, finished steps (build + tests green) and no half-done work. Ask before anything outward-facing (push, tag, release, publishing).
- A sandboxed shell may **not see the real user session**: files you write under `%LOCALAPPDATA%` are not visible to the real app or to Task Scheduler, and the reverse. Do not claim that crash-restart, the scheduled task, autostart or real signed-in data work because a sandbox test passed; give the user a short manual check instead.
- On Windows shells, long heredocs containing `'` or `\` get mangled. Create multi-line files with the editor/write tool (or a script file), not inline heredocs. A `cd` in a shell session persists: use absolute paths.
- The tray app locks its own exe/DLLs: close `RustDroper.exe` before building or publishing.

## Where bugs will most likely come from

Platform changes, not code rot: Twitch GraphQL persisted-query hashes (`TwitchGqlTypes.cs`), Twitch campaign discovery (channel-based, scans up to 100 channels), Kick endpoints / Cloudflare / player page, Facepunch page layout. Diagnose with the CLI first, e.g. `dotnet run --project src/RustDroper.Cli -- campaigns twitch rust --probe 100`, then fix and add a test.
