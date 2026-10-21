# Rust Droper — Usage guide

[Türkçe](USAGE.tr.md) · [Back to README](../README.md)

Rust Droper collects the **Rust drops** of Twitch (and Kick) in the background from the system tray. Rust only. Use at your own risk (see the warning at the bottom and in the README).

## 1. Start

- Download `RustDroper-<version>-win-x64.zip` from the [Releases page](../../../releases/latest), unzip it and double-click **`RustDroper.exe`**. No installer; no .NET needed (single file, ~125 MB). You can verify the download with `SHA256SUMS.txt` (see the SmartScreen note in the README).
- Data lives in **`%LOCALAPPDATA%\RustDroper`** (hidden folder): sign-in (encrypted with DPAPI, tied to your Windows account), settings, logs, the Kick player session. Nothing is written next to the exe; you can copy the exe anywhere and keep your sign-in. The registry is not touched (only `HKCU\...\Run` if you turn on *Start with Windows*). For development/testing the `RUSTDROPER_DATA` environment variable selects another data folder.
- To rebuild from source: `powershell -File build\publish-portable.ps1`.

## 2. First-time setup (once)

1. **Steam ↔ Twitch link:** connect your accounts at <https://twitch.facepunch.com/connect> (required for the drops to reach Steam). The Twitch card should then show **Facepunch/Steam: linked**.
2. **Twitch sign-in:** Twitch card → *Sign in* → a large code is shown (the **Copy** button puts only the code on the clipboard); the browser opens `twitch.tv/activate` — confirm the code there. Your password never enters the app.
3. **Kick (optional):** Kick card → *Sign in* → scan the **QR with the Kick phone app** (the code is valid for ~5 minutes; **Copy link** copies the link instead). Then **Player sign-in** on the Kick card: sign in to kick.com in the window that opens and close it ("Player session: open" should appear; the app never sees or stores your password). **Kick is watched with a hidden browser window (WebView2)**: invisible and silent, but it uses ~0.6–0.8 GB RAM and 20–50 % of one core while Kick runs.

## 3. Daily use

- **Two screens:** the sliding **Twitch ⇄ Kick switch** at the top changes the platform screen; each screen shows only that platform's card, warnings, drop lists and events. If the other platform needs attention (error, offline, sign-in required) a **small neutral dot** appears next to it. The selected screen is remembered.
- **Start reminder:** pressing Start (window open) shows a reminder not to watch another live stream with the same Twitch/Kick account; "Don't show again" can be undone in Settings. Starting from the tray menu (window closed) shows no reminder.
- **Start / Pause:** in the window or from the tray icon's right-click menu; the button controls **both platforms** together (starts every signed-in platform, stops everything if anything is running).
- Closing the window **minimizes to the tray**. Left-click on the tray icon = show/hide, right-click = menu (**Exit** is there).
- **Event info:** the middle of the platform card shows the event name, status (Upcoming/Active/Ended), details, start–end in local time and the time left; **↗ Open site** opens the platform's official drop page.
- **Streamer-drop priority:** drag the streamer cards by their handle (⠿) — left to right, top to bottom = priority. The engine watches the first eligible (online, unfinished) streamer drop, moves on if it goes offline, and falls back to general drops. The order is saved, separately for Twitch and Kick; changes made while watching apply within about a minute unless the drop is already past 90 %.
- **Drop cards:** general and streamer drops are image cards with states: lock (queued), clock (in progress), pulsing dot (watching now), gift + amber glow (ready to claim), tick (claimed). Images are fetched from the Facepunch page (at start and every 6 hours) and cached under `%LOCALAPPDATA%\RustDroper\media` (≤ 20 MB). Without internet the cards still render with a placeholder.
- **Claimed rewards:** the last 5 rewards as cards; the rest as "… and N more".
- When no suitable Rust campaign exists the engine stays **Idle** and re-checks every ~5 minutes; it starts watching by itself once a campaign begins.

## 4. Settings and About

| Setting | Default | What it does |
|---|---|---|
| **Auto-claim** | ON | Claims completed drops automatically (including irreversible in-game items). A change applies after **Pause → Start**. While it is off a red warning is shown during watching. |
| **Rust-reward campaigns only** | ON | Ignores streamer campaigns that appear in the Rust directory but don't give Rust items (e.g. chat badges). A campaign counts as Rust if the game name is "Rust", it has a Facepunch account link, or the reward name is in Facepunch's official list. Turn it off if you suspect a campaign is wrongly filtered (the Health check lists what was filtered). |
| **Notifications** | ON | Tray balloon: drop claimed/failed, sign-in required, campaign started. There is a *Test notification* button. |
| **Prevent sleep while running** | ON | The PC doesn't sleep while the engine is watching (the screen may turn off). A sleeping PC can't make progress. Not blocked while idle. |
| **Restart automatically after a crash** | ON | After an unexpected close the app reopens in the tray within ~5 minutes and resumes. Turned off by Pause or by Exit from the tray. |
| **Show the Start reminder** | ON | The reminder described above. |
| **Language** | Automatic | *Automatic (Windows)* · *Türkçe* · *English*. Automatic follows the Windows display language (Turkish → Turkish, anything else → English). Applies immediately. Log files and the developer CLI stay in Turkish; older "Recent events" notes stay in the language they were written in. |
| **Start minimized** | OFF | Goes straight to the tray. |
| **Start with Windows** | OFF | Starts when you sign in to Windows. |
| **Release notes** | — | The history icon next to About: the versioning rule ([Semantic Versioning](https://semver.org/)) and the notes of every version, newest first. Same content as [`CHANGELOG.md`](../CHANGELOG.md) / [`CHANGELOG.tr.md`](../CHANGELOG.tr.md). |
| **About** | — | Version, developer, contact, what it does, privacy, disclaimer, components, open the data/logs folders. |

## 5. Checklist for the start of a drop event

Twitch general drops are cumulative watch time (e.g. 1 h · 2 h · 3 h · 4 h · 5 h). Event times are on the Facepunch page and on the app's **event card** (local time).

1. Open the app **~15 minutes before** the start. The Twitch card should be *connected* with **Facepunch/Steam: linked**.
2. Open the **Health check** (pulse icon): everything green; **Auto-claim** ON (default) and notifications ON → press **Start**.
3. Within a few minutes of the start the status should go from *Idle* to *Watching* (a Drops-Enabled Rust stream is joined). Balloon: "campaign started".
4. Progress starts **1–2 minutes late**, then +1 per minute. The *Right now* card shows the channel and percentage.
5. When the first drop completes a "Drop claimed" balloon appears; the item shows up in Steam within a few minutes (Facepunch FAQ: up to ~10 minutes; after 30 minutes check the account link).
6. Later drops continue by themselves. Logs: `%LOCALAPPDATA%\RustDroper\logs\`; claim results in `history.jsonl`.
7. **If manual claiming is needed:** a "claim failed" balloon means you can claim at <https://www.twitch.tv/drops/inventory> (the drop is not lost).

## 6. Troubleshooting

| Symptom | What to do |
|---|---|
| Always **Idle** | The campaign may not have started, or there is no Drops-Enabled Rust stream. Look at the discovery lines in the `logs` folder. |
| **Degraded / "schema may have changed"** | Twitch's backend may have changed; the engine retries with exponential backoff. If it persists, open an issue with the log lines. |
| **"Sign-in required again"** | The Twitch session was rejected; sign in again from the Twitch card. |
| No progress for 10+ minutes | The watchdog blocks the channel for 20 minutes and switches; if two channels fail the campaign is held for 60 minutes with an "account may not be linked" warning → check the Steam link. |
| PC slept | After waking the engine re-validates the session and recovers; with *Prevent sleep* on it doesn't sleep while watching. |
| Internet lost | A warning strip appears; it resumes automatically when the connection returns. |

## 7. Known limitations

- Twitch watching uses **real stream traffic** (no browser, lowest quality or audio-only), about 2 MB/min (≈ 125 MB/h). Where a small channel has no low quality, audio-only is chosen.
- Claiming is verified live for Twitch-native rewards (badges/emotes). **Claiming game-delivered Rust rewards (Steam/Facepunch) on a real Rust campaign is only just being tested** (a fallback and a "claim manually" notification exist).
- Whether general-campaign drops progress in parallel or in sequence has not been conclusively observed yet.
- Kick: sign-in (QR) and watching with the hidden player are verified live (progress increased); Kick claiming has not been tested live; very long uninterrupted Kick watching is not recommended for risk/resource reasons.

## Warning

Twitch and Kick may forbid automation in their terms; the chance of a warning or restriction on your account is not zero. One account, one stream and a sensible request rate are used, but the risk is yours. The project is not affiliated with Twitch, Kick or Facepunch.
