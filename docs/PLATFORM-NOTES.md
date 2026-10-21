# Platform notes (Twitch, Kick, Facepunch)

[Türkçe](PLATFORM-NOTES.tr.md) · Condensed from the project's research and live testing (October 2026). These are *observations about third-party services* that can change without notice; the code treats every field defensively (typed, tolerant DTOs; missing field = warning + degraded mode). No account identifiers or session data are included here.

## 1. How Rust drops work

**Twitch** ([twitch.facepunch.com](https://twitch.facepunch.com))
- Rust runs Twitch Drops events. *General* drops are cumulative watch time on **any** Drops-Enabled Rust stream (e.g. 1 h, 2 h, 3 h, 4 h, 5 h items); *streamer* drops need a given streamer's channel for a shorter time (e.g. 30 min).
- Your **Steam account must be linked to your Twitch account** at `/connect`. Items reach Steam about 10 minutes after claiming; claimed items can be transferred to Steam within 180 days.
- Watching several channels at once does not speed anything up: Twitch credits **one channel at a time per account**.

**Kick** ([kick.facepunch.com](https://kick.facepunch.com))
- Kick has a **public, unauthenticated** campaign API (`web.kick.com/api/v1/drops/campaigns`); Rust campaigns are those with `category.slug == "rust"`. A campaign has rewards with `required_units` (minutes). General campaigns have `channels: []` (any live stream in the category), streamer campaigns list their channels.
- Authenticated endpoints (`/api/v1/drops/progress`, `/progress/summary`, `POST /api/v1/drops/claim`) return **403 `{"data":{"type":"Forbidden"}}`** (not 401) without a valid token; a 403 that is *not* Kick-style JSON is a Cloudflare block (the token is kept).
- Only one active channel progresses at a time (Facepunch FAQ).

**Facepunch pages** are used only as a secondary source (event dates, images, account-link redirect). The app does not depend on their HTML: if a page changes the app keeps working without images/dates.

## 2. Twitch technical notes

- **Sign-in:** OAuth device-code flow (`id.twitch.tv/oauth2/device` → user code → poll `/oauth2/token` → `/oauth2/validate`). The client **profile** is configurable: in testing, the `ANDROID_APP` and `WEB` client IDs were rejected (HTTP 400 "invalid client") by the device endpoint, `MOBILE_WEB` could not call GraphQL (401), and the `SMARTBOX_TV` profile works for the device flow, GraphQL and PubSub without a Client-Integrity token. Rust Droper uses that profile. Twitch can change this at any time.
- **GraphQL persisted queries** (`Inventory`, `DirectoryPage_Game`, `DropsHighlightService_AvailableDrops`, `DropCurrentSessionContext`, `DropsPage_ClaimDropRewards`, `PlaybackAccessToken`, …) are addressed by sha256 hashes that Twitch rotates occasionally → `PersistedQueryNotFound`. The app surfaces a clear "schema may have changed" state and retries; automatic hash discovery is on the roadmap. Rate limit used: ≤ 5 requests/second.
- **Campaign discovery:** the old campaign-list queries return `null` for this client. Discovery goes through channels: `DirectoryPage_Game` (Rust, `DROPS_ENABLED` filter) → `AvailableDrops(channel)`. Some "campaigns" are subscription-based (`requiredSubs > 0`, no watch time) and are skipped.
- **Progress:** `DropCurrentSessionContext` (`channel`, `currentMinutesWatched`, `requiredMinutesWatched`, `dropID`) is the primary source, polled about once a minute. PubSub `user-drop-events` `drop-progress` messages are sparse and used only as a hint. When a drop completes, the session becomes an *empty object*, not `null`.
- **Watching:** requesting the stream's HLS master playlist → lowest variant → media playlist, then fetching new segments credits the server-side watch counter (about one minute per minute; the first minute is ~1–2 minutes late). Roughly 2 MB/min at 160p. Where a channel has no low variant, `audio_only` advances the counter at the same rate. No browser and no telemetry events are needed.
- **Claim:** `DropsPage_ClaimDropRewards` needs a `dropInstanceID`, normally delivered by the PubSub `drop-claim` event. In testing that event often **never arrives**; the instance ID has the form `{userId}#{campaignId}#{dropId}` and building it yourself is accepted by the server. Rust Droper therefore claims via a fallback 30 s after `drop-progress` reaches `current == required` when no `drop-claim` was seen. The mutation returns `ELIGIBLE_FOR_ALL` for completed drops, is idempotent, and returns `claimDropRewards: null` (not an error) for unknown IDs. Twitch-native rewards (badges/emotes) were granted by the server automatically at completion. **Open question:** the behaviour for game-delivered (Steam/Facepunch) Rust items on a real Rust campaign.
- **Inventory:** `Inventory.gameEventDrops[]` lists claimed rewards with `isConnected`/`requiredAccountLink`, which is how the Steam/Facepunch link state is read. `dropCampaignsInProgress` is `null` for some campaign classes and cannot be relied on.

## 3. Kick technical notes

- **Public endpoints work without a browser** (Cloudflare does not block .NET `HttpClient` for them): campaigns, `mobile.kick.com/api/v1/livestreams?category_id=…`, `kick.com/api/v2/channels/{slug}` (gives `playback_url`, `livestream.is_live`, categories), `web.kick.com/api/v1/drops/campaigns/{id}/livestreams?lang_code=en` (`lang_code` is mandatory).
- **Sign-in:** the "TV login" flow — the app creates a `uuid` + 6-digit code, the user approves it with the **Kick phone app** via a QR code generated locally (nothing goes to a QR service), the app polls `POST kick.com/api/tv/link/authenticate/{uuid}`. The link opened in a desktop browser returns 404; it must be opened with the Kick mobile app.
- **Watching needs the real player.** Pulling HLS alone (even 10 minutes, 145 segments) produced **no** server-side progress; the viewer identity is established by the player's viewer websocket and needs a signed-in browser profile. A hidden WebView2 player (off-screen window, muted, lowest quality) does credit progress: about **1 unit/min in +2 steps every ~2 minutes**. Notes from building it: the page must be ≥ ~1024 px wide (below that Kick switches to a mobile layout without the quality menu); the quality menu only reacts to real mouse events (so the app uses DevTools `Input.dispatchMouseEvent`) and the chosen quality is not persisted; the cookie banner (OneTrust) is closed via its X, never "accept all"; session state is detected from the presence of the "Log in" button (no cookies/tokens are read).
- **Claim:** the claim endpoint is implemented and defaults to **off** in the Kick claimer; it has not been tested live.

## 4. Risk

Both platforms' terms may forbid automation; a warning or restriction is possible. Design choices that reduce risk: one account per platform, one stream at a time, a modest request rate, real stream traffic instead of synthetic telemetry, no unnecessary GraphQL load. See also [Kick risk research](04-kick-risk-arastirmasi.md) and [simultaneous watching / multi-account research](06-arastirma-eszamanli-izleme-ve-coklu-hesap.md).
