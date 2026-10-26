# Changelog

All notable changes to Rust Droper are listed here, newest first. Versions follow [Semantic Versioning](https://semver.org/): **MAJOR** = architecture changes, big refactors, backwards-incompatible changes; **MINOR** = new features, pages, modules, user functions; **PATCH** = bug fixes, CSS, performance, security, text and small UX fixes.

> This file is generated from `src/RustDroper.App/Resources/changelog.json` (`powershell -File build\changelog.ps1`). Do not edit it by hand. Turkish version: [CHANGELOG.tr.md](CHANGELOG.tr.md).

## [1.0.1] — 2026-10-26 · PATCH · Bug fix / Campaign discovery

Fixed a campaign discovery bug that left the app 'Idle' once the event had started: streams that carry drops are now searched deeper in the stream list.

### Changed

- Discovery no longer stops at the first few channels of the 'Drops Enabled' list; it keeps scanning (up to 100 channels) until enough channels with drops are found, and re-checks known ones first next time
- A campaign's scope (general/streamer) is now decided only among channels that actually carry drops

### Fixed

- The app stayed 'Idle' and watched nothing although the event had started, because the channels carrying the event campaign were not at the top of the stream list

## [1.0.0] — 2026-10-20 · BASELINE · Initial release

The first stable release of Rust Droper and the reference (baseline) for every release after it. It collects the Rust drops on Twitch and Kick in the background.

### Added

- Follows Rust's Twitch and Kick drop campaigns automatically in the background from the system tray
- Watching uses the real stream feed on Twitch (no browser) and a hidden browser player on Kick
- A separate screen for each platform, with a Twitch ⇄ Kick switch in the title bar
- Drop card grid with images, progress bars and states (queued, watching, ready to claim, claimed)
- Auto-claim (on by default) and a claimed rewards section
- Drag-and-drop priority order for streamer drops
- Event card with the Facepunch event's name, dates and countdown
- A filter that follows only campaigns that give Rust rewards (can be turned off in Settings)
- Crash protection: after an unexpected close the app reopens by itself and resumes where it left off
- Automatic recovery after internet outages and PC sleep, plus preventing sleep while running
- Health check page showing the state of sign-in, connection, campaign discovery and settings
- Tray icon, menu and notifications (drop claimed, sign-in required, campaign started, connection lost)
- Turkish and English interface, chosen automatically from the Windows language and switchable instantly
- About and Release notes pages
- A reminder when pressing Start not to watch another live stream with the same account
- Portable single file: no installation; sign-in data stays on this PC, encrypted (DPAPI) and tied to your Windows account
