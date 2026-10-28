# Changelog

## 2.8.4 — 2026-10-27
### Added
- Ball Pit "Space" background: pre-rendered deep-space sky (gradient, Milky Way band, nebulae, 2–4 spiral/elliptical galaxies, layered coloured starfield, vignette). Built once per size/seed off the render thread, blitted per frame.
- Quality: Low = static, Medium = slow drift, High = drift + twinkle; step-down level 1 disables sky animation.
- Setting Background = Space/Classic (Space default for new/untouched installs; customised classic colours/image keep Classic), "Randomize" sky seed; persisted, in diagnostics (background, skySeed, skyAnimation, skyBuilds), in both apps.
- Overlay (transparent) mode is unchanged: no sky.

## 2.8.3 — 2026-10-27
### Fixed — a burst of joins in the Ball Pit took the stream down
- **Root cause (thread dumps during the stall):** the renderer held the scene lock for the whole frame and shaded the balls with `Parallel.For` on the shared .NET thread pool. Every finished avatar download (also on the thread pool) then blocked on that lock to hand its texture over. A burst of ~50 joins started ~50 downloads at once, so ~30 pool threads sat waiting on the lock, and the `Parallel.For` the lock holder was waiting for could not get threads. Frames took seconds, the Ball Pit feed (the only picture input in Ball-Pit-only mode) stopped, ffmpeg produced no output for 13–21 s, and the ingest dropped the stream. The UI thread waited on the same lock, so the app looked frozen too. Two more paths could end the stream: the encoder-side auto-degrade restarted ffmpeg to change the render size, and any render exception silently ended the feed (then the 25 s watchdog killed the encoder).
- **Rendering no longer holds the lock or uses the thread pool.** The ball state is copied under the lock in microseconds and drawn without it, on the renderer's own shading threads. Avatar hand-over is lock-free and is applied on the next physics step.
- **The feed can't stall the encoder any more.** A paced writer sends a frame every 1/fps from the newest finished frame, repeating the last one when a render is late. Rendering runs on its own thread. A render exception is logged (throttled), the pit is reset (everyone goes back in the queue) and frames keep flowing. The reason the feed ended is logged.
- **No encoder restarts for quality.** When the encoder is slow, the Ball Pit lowers its own quality level instead.
### Added — Ball Pit limits and quality
- **Cap**: *Max balls* defaults to **60** (was 200), up to 200 (was 300). A profile still on the old default 200 is moved to 60 once; any other value you chose is kept. People over the cap wait in a queue: **recent chatters first, then LIVE viewers, then room members**. The pit shows "N in the ball pit · +M more". A waiting chatter or viewer swaps in for a plain member; members never swap with each other.
- **Spawn rate limit**: at most 4 new balls per second (burst 2). Leave + rejoin churn is coalesced: a ball that is popping out and comes back is kept, and someone who joins and leaves before their ball appears never spawns. "joined" announcements are skipped during a burst.
- **Avatars**: at most 3 load/decode at a time, decoded straight to the ball's 64 px texture (JPEG/WebP scale while decoding), kept in an 8 MB LRU, and failed URLs aren't retried for 10 minutes. Balls show initials until their avatar is ready; a frame never waits for one.
- **Lighter rendering**: uniform-grid collisions, no per-frame allocations (≈ 4 KB/frame, was 40–366 KB), cached name labels (only drawn while ≤ 40 balls on Medium), highlight without `Math.Pow`.
- **Quality presets** (Ball Pit tab → *Quality*): **Low** (50 % render size, no avatars/shadows/highlights/labels, every 2nd frame drawn), **Medium** (default, 75 %), **High** (100 %). Picking a preset also picks its render size.
- **Automatic degrade**: if the slowest 10 % of frames take > 80 % of the frame budget for 2 s, the pit goes up one level (1 no shadows/highlights → 2 no labels, fewer particles → 3 every 2nd frame drawn → 4 half the balls). It steps back down after 15 s with plenty of headroom. Each change is logged.
- **Stats / Log / diagnostics**: pit frame time (avg/p95), ball count, "+N more" and degrade level in the Stats item line and the Ball Pit status; a summary in the log every 5 minutes. *Export diagnostics* has a `ballPit` section (counts and timings only, no names).
### Numbers (1280×720 @ 30 fps, x264, pit Source + mock radio, 8 cores; 60 s encode, +50 joins at 15 s, 500-member room at 35 s)
- Before: lag (wall − output) rose from 0.30 s to **+21 s**, 535 frames skipped, "Encoder can't keep up (74 % of the Ball Pit frames dropped)".
- After: lag 0.03 → 0.04 s (peak +0.04 s), output 1.000 s per wall second, 0 skipped, no encoder restart, pit 3–4 ms avg per frame. Pinned to 2 cores: also flat (peak +0.04 s), p95 ≤ 25 ms.
### Tests
- `pitlimits` unit (defaults/migration, cap + priority, rate limit, churn coalescing, degrade levels, avatar LRU, decode concurrency, negative cache, render-exception recovery with frames still flowing, auto degrade and recovery), `pitbench` (renderer/physics table), `pitburst` (the 60 s encode above).

## 2.8.2 — 2026-10-27
### Added — stream with only the Ball Pit and the radio (no video files)
- **Source selector** at the top of the Source / Playlist tab (Windows and Linux): *Playlist (video files)* or *Ball Pit scene*, with a note saying where the audio comes from (the Radio tab, or silence when the radio is off). It's the same setting as the Ball Pit tab's mode: choosing *Ball Pit scene* sets Mode = Source, choosing *Playlist* turns a Source pit off (an Overlay pit is kept), and changing the Ball Pit tab updates the selector.
### Fixed — Start refused with an empty playlist even with the Ball Pit as the source
- Root cause: Start's file check was `missing.Count == Playlist.Count`, which is true for 0 == 0, so an empty playlist always got "None of the playlist files exist." whatever the Ball Pit mode. The engine also threw "Playlist is empty" if the pit scene wasn't ready. The check now looks at the source: an empty playlist is fine with the Ball Pit scene (the hint names that option otherwise); if the scene isn't available the error says so.
- With no playlist the engine plays an internal silent filler under the pit (live pacing unchanged from 2.6.1: no `-re`, wall-clock timestamps; the radio is mixed in when it's on, otherwise the track is silence, so EpikChat still gets audio). The filler is not a track: no now-playing, no history entry, no requests queue, and **Next** is disabled.
- `!request` in Ball-Pit-only mode: the radio's requests handle it when enabled; otherwise the viewer gets "Song requests need a playlist — this stream is the Ball Pit scene only" (once per 30 s per room).
- **Title bar showed "EpikStreamer 2.5.0"**: it's now read from the build at runtime. The Linux man page's version is filled in by the package build.
### Fixed — chat window in EpikChat's mobile layout; stale stream key retried 10×
- **The Linux chat window opened 440 px wide** (Windows: a fixed 400 px column). EpikChat's own CSS hides the room list (`.chat-workspace-rail`) below **968 px** and the member/viewer sidebar below **1280 px**, so the page showed only the message column. The chat window now opens at **1300×860**, can't be made narrower than 984×560, and remembers its size. On Windows the chat column restores its remembered width (default 1000, the window widens to fit when the screen allows) and the panel **zooms out** when it's narrower than 984 px so EpikChat still gets its desktop layout.
- **"Compact" (on by default) hid EpikChat's navigation**: its CSS hid every `header`, `nav` and `*sidebar*` element (the top bar with Go live, and parts of the room/member lists). It now only trims ads/cookie notices and tightens message spacing.
- If the page is still narrow, room switching (reply in the source room) and Go live open EpikChat's collapsed menu first.
- **`Operation not permitted` / `Server error: Publish failed` from the RTMP server now means "the key is dead"**: the app runs Go live for a fresh key before the next retry (after 2 s, not the usual back-off, and even within 90 s of the last create), instead of retrying the same key up to 10 times. If EpikChat still shows you live, the current key is read from "Stream credentials"; if that's the same refused key, you're asked to end the stale live stream in the chat panel (the app never clicks End).
### Tests
- `layout` unit (window/column/zoom vs the 968/1280 px breakpoints, Compact CSS, publish-refused detection); Linux host go-live mock: real chat window width ≥ 968 CSS px with the room rail shown, Go live behind a collapsed menu, fresh key after a refusal (offline → new session; live with a rotated key → Stream credentials; same key → clear message, nothing ended); room-switch replies with the rail collapsed (`--selftest-rooms … mobile`).
- `pitonly` unit (selector sync, start checks, encoder args with no playlist + pit Source, radio on/off), `pitonly-encode` (30 s, pacing/lag, audio + video), Linux host `--selftest-pitonly` (Start with an empty playlist).

## 2.8.1 — 2026-10-27
### Fixed — chat commands from background rooms
- **Commands typed in a joined room that isn't the active tab got no response** (e.g. Lobby active, `!help` in ImpulsiveThots). Root cause: the settings form was filled *before* the 2.7.1 chat-feed migration ran, so a 2.7.0-era blank "Command room" stayed in the box and was saved back on the next settings save (window close, alert test, hotkey apply) with the migration already marked done. Blank meant "auto = the room shown when going live", so messages from every other room were dropped as "other room". Now the migration runs before the form is filled, blank means **all joined rooms**, and settings already saved that way are repaired once (DefaultsVersion 281).
- **Replies always go to the room the command came from, whichever tab is active and whether or not the chat panel is on screen.** A single serialized reply queue switches to the room through the sidebar, verifies the room is shown, posts through the page's normal composer, confirms the post through our own message coming back on the feed (with that room), then returns to the tab you were on — or keeps the tab you clicked meanwhile. Nothing is ever sent over the socket by the app.
- Room names are compared with quotes/case/spacing normalized (`Drugs''R''Us` = `Drugs'R'Us` = `Drugs’R’Us`), in the app and in the page's room-switch helper.
- The same viewer typing the same command in two rooms within 15 s is no longer swallowed as a duplicate (dedupe is per room).
- Command cooldowns are per room.
- Unsolicited posts (trivia, races, polls, predictions) go to the stream's room instead of the literal "*" when listening in all rooms.
### Added
- Per-room chat diagnostics (Export diagnostics → `feed.rooms`): received, commands seen, delivered, duplicates, drops by reason (incl. command-handler reasons such as cooldown), replies queued / sent / confirmed / failed, last drop reason and last reply result. No message text.
### Known
- The tap still doesn't learn `chatCommandsBotId` (`botIds` 0 in diagnostics); bot messages are still flagged by sender type.

## 2.8.0 — 2026-10-27
### Added — Engage: viewer points, rewards, mini-games, polls & predictions
- **Points** (name configurable, default "Epik Points"; everything per profile on the new **Engage** tab):
  - earned per minute watching while you're live (from the live-viewers list), per chat message (cooldown, no repeated text, per-minute cap), a first-chatter bonus each stream and optional daily streaks (+25 × days, max 7);
  - stored per EpikChat **userId** (falls back to the name; a name entry is migrated to the id once it's seen) in `engage/points.json` next to the profiles, written atomically with daily backups (14 kept); a corrupt file is moved aside and the newest backup restored;
  - `!points [@user]`, `!top`, `!give @user N` (toggle), host/mod `!addpoints` / `!removepoints @user N`;
  - **Points** sub-tab: search, set balance, remove, reset all, CSV export (formula-safe) / import (replace or add).
- **Rewards** (`!rewards`, `!redeem <name> [text]`): each has a cost, cooldown, enabled flag and optional host approval (**Pending approvals** list; deny refunds). Kinds: **Song** (the viewer's text goes through your existing request queue / radio requests; refunded if requests are off), **BallPit** (`bigball`, `shake`; refunded if the pit is off), **Alert** (on-stream alert text with `{user}` `{text}`), **Sound** (plays a file on your PC). Defaults: song 500, bigball 100, shake 250, hype alert 300, airhorn (disabled).
- **Mini-games** (each toggleable): `!gamble <amount|all|50%|1k>`, `!slots <amount>`, `!duel @user <amount>` → `!accept` / `!decline` within 60 s, **trivia** (`!trivia` by host/mods or every N minutes; 24 built-in questions plus your JSON packs; first correct answer wins), `!heist <amount>` (join within 60 s; success chance grows with crew size, capped), **Ball Pit race** (`!race` by host/mods or the button; `!joinrace`, `!bet <racer> <amount>`; racers run in lanes with a chequered finish line; winners' backers share the pot). Anti-abuse: per-user cooldown, a global commands-per-minute limit, bets min/max, bots, you and an excluded list never play or earn.
- **Polls**: from the Polls sub-tab or `!poll "Question" a | b | c [seconds]` (host/mods). Vote with `!vote N`, or a bare number / option text if enabled; one vote per viewer, changeable. Live overlay card with bars and a countdown; results to chat and to the history list.
- **Predictions**: `!prediction "Question" a | b` (or the tab), viewers `!predict <outcome#> <points>`, host `!lock`, `!resolve N`, `!cancelprediction` (refund). Parimutuel payout (rounding dust goes to the biggest winner; refund if nobody picked the winner).
- **On-stream overlay**: poll, prediction, leaderboard (after `!top`), trivia, heist, race and toast cards, corner/width/colours configurable. With the Ball Pit live they're drawn into the pit frames; otherwise a text version is drawn by one more `drawtext` from `engage.txt` (no extra inputs either way).
- Replies follow the existing **↩ Reply in chat** opt-in and rate limit; several replies to the same room within a second are joined with " · " and chunked to 250 characters. With replies off they show on stream while live. Engage commands are listed by `!help`.
- The live feed now carries the sender's **userId** on every tapped message, the viewer ids from the live-viewers ACK and your own userId from the handshake (used for points and for recognising you as host). The tap is still passive.
### Notes / caveats
- Watch-time points need the live-viewers list (socket ACK while you're live). Viewers seen only by name are keyed by name until their id shows up.
- The host is you (signed-in userId/username) plus the **Moderators** list (names or ids); moderation actions are not automated.
- Ball Pit races need the Ball Pit in Source or Overlay mode while live; the camera keeps orbiting, so the track's direction on screen changes with the view.
- The Sound reward plays .wav on Windows; on Linux it uses the same player as alert sounds.
### Tests
- `engage` unit suite (points/accrual/anti-spam/streaks, ledger load/migrate/backup/CSV, games incl. seeded race and parimutuel, polls/predictions parsing and results, reply batching/chunking, overlay text).
- Engage mock end-to-end: the fake socket server's "engage" scenario sends commands from Lobby, Music, The_Bar_Alc_420 and Room B (host poll, votes incl. a bare number and a changed vote, `!addpoints`, games, `!top` after a reconnect). In headless Chrome (replayed through the bridge/router into the engine) and through the Linux WebKitGTK host, where every reply is typed into the composer of the room the command came from.
- `engage-encode`: 60 s encodes with the poll card, a race and the leaderboard drawn into Ball Pit frames, and the text fallback, on both ffmpeg builds (samples are not release assets).

## 2.7.1 — 2026-10-27
Built against the protocol and page structure Sam captured from the signed-in site (2.7.0's live feed was a guess and never matched).
### Fixed
- **Commands didn't answer in the other rooms you're in (e.g. `!help`).** EpikChat's socket (`wss://ws.epikchat.com/chat`, Engine.IO v4) uses socket.io's **msgpack parser**: every packet is one binary frame, which 2.7.0 never decoded. The tap now decodes msgpack (maps, arrays, str, bin, ints, floats, nil, bool, timestamp ext in the 4/8/12-byte forms; ArrayBuffer and Blob frames, kept in order) and reads:
  - `message` events (`content`, `senderName`, `targetId` room, `createdAt`); embeds, deleted and non-room messages are skipped; bots (`senderType: "bot"` / the chat-commands bot id) and your own messages (`senderId` = `connectionAccepted.userData.userId`) never trigger;
  - room id → name from `connectionAccepted` favorites and the room lists in ACKs (`getSidebarData`, `resumeOpenApp`); message ids found in ACKs are history and never fire;
  - `userUpdate` presence.
  The tap stays passive: it wraps the page's own socket, decodes copies of incoming frames and of outgoing frames (only to pair ACK ids with event names), never inspects the CONNECT packet (it holds the chat token), and never sends or modifies anything.
- **Command room defaults to all joined rooms (`*`)**; each reply goes to the command's own room. 2.7.0 settings with a blank command room are migrated once.
- **Viewers list stayed empty.** The live-viewers list arrives as a periodic ACK (`{status, viewers, broadcastId, revision, viewerIds}`); it's recognized by shape (whatever the request is called — diagnostics name it), entries are mapped generically (id / username / displayName / picture / country), and changes feed the Viewers tab, alerts, Ball Pit and history. DOM fallback: the `aside` "Your live stream viewers" → region "Viewers" (empty when "No viewers yet").
- **Go live didn't click the final "Done".** The real "Connect your encoder" dialog has no further confirm step: after reading the key the app clicks the dialog's own **Done** button exactly once and ffmpeg starts. Already-live detection uses the "Your live stream viewers" sidebar or the **End live stream** button (verified selectors, reported in diagnostics).
### Added
- **↩ Reply in chat** toggle on the chat panel's bar (Linux: **↩ Reply** next to 💬 Chat) and a one-time prompt "Reply to chat commands in chat? (posts as you)".
- Room switching to reply is **on by default but only used while the chat panel is off screen** (no visible disruption); otherwise replies queue until that room is showing or the panel is hidden (max 20, 5 min). The sidebar room is matched by its exact accessible name. Replies are always typed into the page's composer, never emitted over the socket, and keep the existing rate limits.
- Chat diagnostics: `verified: true`, protocol summary, decoded event names, ACK shapes (field names and value types only), the viewers request name, per-room decoded-message counters, decode errors. Still no message text, names, tokens or cookies.
### Notes / caveats
- The viewer row markup inside "Your live stream viewers" is unknown, so the DOM fallback reads rows generically; the socket ACK is the primary source.
- The name of the request whose ACK carries the viewers wasn't captured; it's detected by shape and reported in diagnostics.
- Linux: the chat panel is a separate window, so "on screen" means the 💬 Chat window is open; replies to other rooms then queue until it's closed or that room is showing (or turn on room switching while visible — `SwitchWhileVisible` in settings.json).
### Tests
- The fake server now speaks EIO4 + socket.io-msgpack binary with the captured shapes (timestamp ext, connectionAccepted favorites, sidebar ACK with other rooms, messages in Lobby / The_Bar_Alc_420 / Music while the UI shows Room B, viewers ACK poll with join/leave, server pings, a reconnect with history replay; ArrayBuffer on the first connection, Blob on the second).
- Chrome (CDP) + router replay and the Linux WebKitGTK host check: each command fires once with its room, no self/bot triggers, viewers populate; on the host, `!help` in a non-visible room is queued while the panel is on screen and answered in its own room by switching once it's hidden, then switched back.
- The go-live session mock now has the exact "Connect your encoder" dialog and live sidebar: Done is clicked exactly once (never the X / End live stream), and already-live detection is checked.

## 2.7.0 — 2026-10-27
### Added
- **Seamless chat (live feed).** Chat commands, `!request`, Ball Pit jumps, alerts and the viewer list no longer depend on which EpikChat tab or panel is visible.
  - A small script runs at document start in the 💬 Chat panel. It wraps the page's own `WebSocket` / `EventSource` (and peeks at viewer-list `fetch`/XHR responses) and **passively mirrors incoming events** to the app. It is read-only: it never sends, modifies or opens connections.
  - Events are recognized by shape (socket.io/engine.io, Phoenix, Centrifugo, `[event, payload]` and `{type, data}` envelopes). Message, history/backlog, viewer join/leave/list and member events are classified. History and backlog never fire commands.
  - A router merges the live feed with the existing on-screen (DOM) reader. Messages are deduplicated by id (and by user + text within 15 s across sources), so each fires exactly once, including when the socket reconnects and replays history.
- **Command room** setting: auto (the room shown when you go live, the default), all joined rooms (`*`), or a named room. Messages from other rooms are ignored, including the room you happen to be looking at.
- **Viewer list without "Viewing me"**: viewers come from live join/leave/list events. Optional fallback (on by default): when no live viewer events arrive for 60 s, the app re-requests the same-origin list the panel uses, read-only, at most every 20 s (minimum 15 s), only while the page is visible.
- **Reply routing** (replies are still off by default): a reply goes to the room the command came from. If that room isn't showing, it's queued (max 20, 5 min) and posted when you open it. Opt-in **Allow switching room to reply** clicks the room in EpikChat's room list, replies, and switches back.
- **Source indicator** in Settings → Chat: "Chat: live feed ✓ (room[, viewers live])" or "Chat: DOM fallback — only the visible room".
- **Diagnostics:** Chat diagnostics… now adds a `tap` section (event names, counts, field names and value types only; no message text, names, tokens or cookies) and the feed counters. Advanced **Protocol map** (JSON regex overrides for event names) lets the feed be tuned without a new release.
### Notes / caveats
- **The EpikChat realtime protocol is unverified.** The chat app's code is only served after sign-in, and per the instructions nothing was signed in to or connected to. The parser was built from general knowledge of common realtime formats, plus EpikChat's public developer policy (which confirms WebSocket real-time events) and its public Next.js bundle (auth only). If the live feed doesn't recognize the real events, the indicator shows "DOM fallback" and everything works as in 2.6.1.
- A live-feed message whose room is known only by id is matched to a room name once it's learned (from a frame carrying the name, or the same message on screen). Until then it waits for 2.5 s and is then dropped and counted as "unmatched" in diagnostics.
- Room switching for replies depends on EpikChat's room list markup and is unverified.
### Tests
- New: `feed` (router and reply-queue unit tests) and `tap-unit.js` (decoder and classifier in node).
- New: a fake local WebSocket server (`ws-server.js`, inferred socket.io-style protocol) with a mock page. The UI shows Room B, the command room is Lobby and the viewers panel is never opened. The scenario covers backlog, other-room traffic, viewer join/leave, a server-side disconnect with history replay plus a duplicate resend, then a room switch where DOM and feed both see a message.
  - It runs in headless Chrome over CDP (`run-tap-mock.sh`, replayed through the app's router) and through the Linux WebKitGTK host (`--selftest-tap`, the real ChatPanel). Both check that commands fire exactly once, viewers populate, there are no duplicates and the reconnect recovers. On Linux the test also checks that the reply is queued and posted after the switch.

## 2.6.1 — 2026-10-26
### Fixed
- **Ball Pit + Radio falling behind real time** (ffmpeg log flooded with `Resumed reading at pts … with rate 1.050 after a lag of N s`, stream lagging and stuttering). Root cause: with the Ball Pit on, the encoder mixed two `-re` (readrate) inputs, the playlist pipe and the now-playing card PNG, with live TCP inputs (Ball Pit frames, radio audio) that already run at wall-clock speed. On ffmpeg 8 / master (the bundled BtbN build), the scheduler lets the `-re` inputs fall further behind and the whole graph gets throttled to about 0.5–0.65x. Measured: 0.63x, with lag growing about 0.35 s every second. The Ball Pit feeder made it worse: when it was blocked writing to ffmpeg, its catch-up logic advanced the frame counter without sending frames, so the timeline was lost for good.
  - With a live Ball Pit input, `-re` is no longer used: the live input sets the pace.
  - Ball Pit frames are stamped with wall-clock timestamps (`-use_wallclock_as_timestamps 1`), and the feeder is paced to exactly 30 fps. Frames it has to skip become repeated frames instead of a slower timeline.
  - With the Ball Pit on, the now-playing card is drawn straight into the Ball Pit frames in C#, so there is no extra PNG input or overlay in ffmpeg.
  - The radio input gets `aresample=async` to absorb jitter.
### Added
- **Pace watchdog / automatic Ball Pit quality** (Ball Pit → Auto quality, on by default). If the encoder can't keep up, it degrades step by step and the log/state show "Encoder can't keep up — lowering Ball Pit quality…". The trigger is any of: recent speed below 0.97x for 15 s, the "Resumed reading" lag growing, or more than 10 % of Ball Pit frames dropped. The steps are: draw every 2nd frame (the stream stays at 30 fps), then lower the render size down to 50 % (short encoder restart). If even that isn't enough, a one-time hint suggests a lower resolution/fps or the hardware encoder.
  - The renderer also switches itself to every-2nd-frame when the PC can't draw 30 fps.
### Changed
- **AMD (h264_amf):** low-latency/speed preset with no B-frames, and the bitrate is capped at 2.5 Mbps (EpikChat's cap).
- Tests: new `pace` real-time soak test (Source/Overlay, card, any ffmpeg, CPU-throttle mode). Unit checks were added for the new arguments.

## 2.6.0 — 2026-10-26
### Added
- **Radio** (new tab): background music from an internet radio station, made for the Ball Pit but it works in every mode.
  - Paste a **.pls / .m3u / .m3u8** playlist or a direct stream URL. The MP3/AAC stream is preferred and HLS is the fallback (or the other way round with **Prefer HLS**). **Test** checks that each stream decodes and reads the now-playing info.
  - The radio goes through the existing audio pipeline: its own **volume** (changeable live) and **mute**, optional **loudnorm** levelling, and **ducking** under the mic or video audio. It mixes with your Audio-tab music.
  - **Resilient:** a separate relay feeds the encoder over local TCP. If the station drops, the stream keeps going with silence while the relay reconnects in the background (backoff up to 5 s, switching to the other stream URL after repeated failures). The encoder never restarts because of the radio. The log shows "📻 Radio stream dropped…" and "📻 Radio reconnected".
  - ffmpeg's HTTP reconnect options are used when the build supports them (detected from `-h protocol=http`).
- **AzuraCast now playing:** the API base and station shortcode are detected from the playlist URL (or set by hand), and the public now-playing API is polled about every 10 s. It feeds:
  - the **now-playing card** (title, artist, album art, progress, up next, listeners and station name);
  - an optional **radio card** in the Ball Pit;
  - a preview in the Radio tab.
- **API key** is optional and not needed for now playing or requests. If set, it's stored encrypted and sent as `X-API-Key`. It's never logged.
- **Chat:** while the radio is on, `!song` / `!np` answer with the radio track and `!lastsong` with the previous one (new placeholders `{radio}`, `{last}`, `{station}`). `!request <song>` can send song requests to the station using the existing per-user cooldown. This is **off by default**. Note that AzuraCast limits requests per IP address, so the station may refuse requests that come in quick succession.
- **Ball Pit beat pulse** (optional): the balls bounce to the music.
### Tests
- `radio`: playlist parsing (including the real station playlist), the now-playing parser, auto-detection, requests against a local mock AzuraCast, ffmpeg arguments, and the relay and silence fill.
- `radio-encode`: end-to-end encodes on both ffmpeg builds, including a simulated 8 s station drop. The video stayed continuous, the audio came back, and the encoder never restarted.
### Notes
- Now playing uses polling (not SSE), and only for AzuraCast stations. Other stations play, but without track info.
- Turning the radio on or off, or changing its URL, applies on the next Start. Volume and mute apply live.
- The beat pulse only runs while streaming.

## 2.5.0 — 2026-10-26
### Added
- **Ball Pit** (new tab): everyone in the room becomes a bouncy, avatar-textured 3D ball you can stream.
  - **Who's in it:** the room's **Room members** list. The list is virtualized, so a full scan scrolls through it and then puts your scroll position back; it never scrolls while you're using the list. Optionally, **LIVE viewers** and recent **chat authors** are included too. Each member's avatar and name color is used; people without an avatar get an initials ball.
  - **Avatars** are downloaded directly and cached on disk. If that's blocked, the chat page re-reads an image it already shows. No other site APIs are called.
  - **Scene:** rendered on the CPU with SkiaSharp. A lit room (floor and walls) with shaded, textured balls that roll, soft shadows and name labels. Camera tilt, zoom, angle and an optional slow orbit are adjustable. Up to 200 balls by default (hard cap 300); extra people wait for a free slot.
  - **Physics:** gravity, bounce (restitution), ball-to-ball collisions, rolling friction and walls.
  - **Events:** a joiner drops in from above, a leaver pops. A chat message makes that person's ball jump and shows a speech bubble.
  - **Chat commands** `!bounce`, `!bigball`, `!color <#hex>` and `!shake` go through the existing chat-command system, with a per-user cooldown equal to the command cooldown. `!shake` can be host-only or have its own cooldown. The Ball Pit never replies in chat.
  - **Two modes:** **Source** replaces the video with the pit (with no video in the playlist, a silent background runs and your Audio-tab music still plays). **Overlay** draws the pit with transparency on top of the playlist.
  - Both modes work with recording, multi-output (tee), the hardware encoders, auto Go live and auto-reconnect.
  - **Tab controls:** a live preview (the actual stream frames while you're live), the member count, settings, **Add 10 test users**, **Scan members now** and a manual **!shake** button.
  - **Performance:** about 144 fps rendered at 1280×720 with 200 balls on 8 cores (Release build), so 720p30 uses roughly 20 % of one core. A render scale (100/75/50 %) is available for slower machines.
- Chat diagnostics gain a `ballPit` section listing the member, avatar, name and total selectors, all flagged `verified: false`. It contains no names.
### Tests
- Physics and feature unit tests (`ballpit`): rebound, collisions, the cap and waiting list, commands and cooldowns, command routing, roster merging, textures, avatar cache and ffmpeg arguments. There's also a render benchmark (`ballpit-bench`).
- End-to-end 10 s encodes in both modes on the system ffmpeg and on the FFmpeg master build (`ballpit-encode`).
- New mock `epikchat-members.html`: a 150-member virtualized list with avatars and colors, plus a join and a leave, user-scroll protection and the avatar fallback. It runs in headless Chrome (`run-members-mock.sh`) and through the Linux WebKitGTK bridge (`--selftest-members`).
### Unverified assumptions
- The member avatar, name-color and member-total selectors are **untested against the real signed-in site**. If they're wrong, balls get initials and default colors; the names still come from the already-used "Room members" list.
- Emoji in names and bubbles have no color-emoji font fallback in the renderer and may show as boxes.

## 2.4.1 — 2026-10-26
### Fixed
- **Start now opens the EpikChat live session for you.** Before, you had to click the camera (Go live) icon in EpikChat before Start would work, because EpikChat refuses the RTMP ingest until a live session exists.
  - On **Start**, on **auto-reconnect** and on **scheduled starts**, the app checks the chat panel. If you're not live yet, it clicks the nav camera / **Go live** button and runs the dialog: **RTMP encoder** → Continue → your profile's **Public/Private** audience → **Create live stream** → **Connect your encoder**.
  - It reads the session's stream key with the same automation as the key fetch. It keeps the saved key if it matches; otherwise it switches the output to the new key and saves it (masked in logs).
  - Once ffmpeg is connected, it clicks a final **Go live / Start streaming** confirm button if EpikChat shows one. It only closes the encoder dialog with a Done-type button, never Cancel.
  - Works the same in the Windows (WebView2) and Linux (WebKitGTK) chat panels. The chat panel opens automatically if it was closed.
- **No duplicate sessions:** nothing is clicked when the page already shows you're live, the camera is clicked at most twice per attempt, and a reconnect within 90 s of creating a session doesn't create another.
### Added
- Setting **Output → "Open Go live on EpikChat automatically when starting"** (default **on**), with status messages and a timeout (60 s). If it can't finish, the encoder starts anyway (it keeps retrying) and you're told: *"Click the camera icon (Go live) in the EpikChat chat panel and finish the dialog."*
- With the setting on, an EpikChat output may start with an empty key; the key is taken from the new session.
- Chat diagnostics include a `goLive` section: live state, how the camera button was found, and all go-live selectors (flagged `verified: false`).
### Tests
- New mock `epikchat-session.html`: nav as in EpikChat (compass, green camera, gift, avatar), ingest refused until a session exists, already-live / icon-only / color-only variants. Run on WebKitGTK (`--selftest-golive`) and headless Chrome (`run-session-mock.sh`). Engine reconnect hook and key replacement are covered too.
### Unverified assumptions
- The camera/live selectors are **untested against the real signed-in site**: we assume the camera button's accessible name is "Go live", or else that it's an icon-only nav button with a video/camera icon or green color. "Already live" is detected from an "End/Stop stream", "You're live" or "Live now" control.
- Whether EpikChat shows a final confirm button after the encoder connects, and how the encoder dialog closes, is assumed. If neither exists, nothing is clicked.

## 2.4.0 — 2026-10-26
### Added
- **Linux / Debian version** (`EpikStreamer.Linux`, Avalonia 11) for **x64 and arm64**, with the same features as the Windows app. It shares one `EpikStreamer.Core` (net8.0) with the Windows app.
  - **Packages:** `epikstreamer_2.4.0_amd64.deb` / `_arm64.deb` and portable `epikstreamer-2.4.0-linux-<arch>.tar.gz`.
    - The .deb installs to `/opt/epikstreamer` with an `/usr/bin/epikstreamer` launcher, a menu entry, icons and a man page.
    - ffmpeg, WebKitGTK and libsecret are pulled in as dependencies.
  - **Sign-in, stream-key fetch and chat panel** use WebKitGTK (`libwebkit2gtk-4.1`). It drives the same normal EpikChat pages as the Windows WebView2 build.
    - If WebKitGTK is unavailable, the app opens EpikChat in your default browser and you paste the key manually.
  - **Remembered stream keys** are stored in the desktop keyring via libsecret (GNOME Keyring / KWallet).
    - Without a keyring they fall back to an AES-GCM-encrypted file whose key is kept in `~/.config/epikstreamer/secret.key` (mode 0600).
  - **Encoders:** NVENC, VA-API (Intel/AMD), QuickSync and x264, with auto-detect.
  - **Audio:** the microphone comes from PulseAudio/PipeWire (`-f pulse`), and sounds play through `paplay`/`pw-play`/`aplay`.
  - **Updates:** the app tells you a new version is available and downloads the matching `.deb` (SHA-256 verified). It then copies `sudo apt install <file>` to the clipboard / opens it in your package installer. It never installs anything as root by itself.
  - `update.json` gains an optional `linux.{amd64,arm64}.{url,sha256}` block. Older Windows clients ignore it.
### Changed
- Windows app version 2.4.0. No functional changes on Windows.
- Platform-specific choices in the shared core (mic input, encoder list, update package) go through a single `Os.Windows` switch, so the test suite covers both platforms.
### Known limitations (Linux)
- Global hotkeys work on X11 only. Wayland has no global hotkey API, so use the in-app buttons there.
- The EpikChat sign-in/chat browser opens as its own window instead of being docked in the main window.
- Chat messages are sent only through the page's own input box. As on Windows, chat replies are off by default.
- Library thumbnails don't refresh live while a scan is running; reopen the tab.
- Not yet tested against a real EpikChat live session on Linux. Tested against the mock pages plus the real sign-in page. The LIVE Viewers selectors are still unverified.

## 2.3.0 — 2026-10-25
### Added
- **Media library** (Library tab):
  - Watched folders, scanned with ffprobe (metadata, duration, resolution, codecs) and thumbnailed. Results are cached by size+mtime.
  - List/grid views with search, sort and filter.
  - Saved playlists (create/rename/delete/reorder, drag from the library) that can become the stream playlist or be queued.
  - The library is the source for song requests.
- **Song requests**:
  - Commands: `!request`/`!sr` (fuzzy match), `!queue`, `!skip` (vote, by count or % of viewers) and `!wrongsong`.
  - Limits: per-user limit, cooldown, max queue, max length, allow/block lists.
  - Placement: next, or end of the playlist pass.
  - Host controls: approval, approve/deny/reorder/clear.
  - A request is inserted at the next track boundary with the configured transition.
  - Replies go to the on-stream overlay. Chat replies are still opt-in.
- **Now-playing card**:
  - Shows the title, thumbnail, progress/time, up next and the requester.
  - Auto (N s on each track change) or Always mode.
  - Position, width, colors, opacity and label are configurable.
  - Rendered to a PNG that ffmpeg re-reads (`-re -f image2 -loop 1`), so it updates about once a second without restarting the stream.
- **Viewer list**:
  - Names come from the LIVE → Viewers panel and/or Room members, with join/leave times, total watch time, visits and a leave grace period.
  - Optional on-stream welcome alert (off by default).
  - History records unique viewers and the top 20 by watch time. The CSV gains a `unique_viewers` column.
  - "Open profile" clicks the name in EpikChat's own UI. There's no automated moderation.
### Known limitations
- The LIVE "Viewers" panel selectors are a best guess and **not verified** against the live site. Chat diagnostics now report what they match (`viewers`, `verified:false`).
- Member lists are virtualized, so only rendered rows are seen.
- The default now-playing position (bottom-left) can overlap a bottom-left text ticker. Move one of them.

## 2.2.2 — 2026-10-25
### Fixed
- **Chat commands did nothing.** Root cause: the old chat observer guessed generic markup (`[data-message-id]`, `.chat-message`, `.message`, `[class*='message']`, `[role='listitem']`, a `textarea`/placeholder input and a Send button), and none of it exists in EpikChat. EpikChat renders messages in a react-virtuoso list (`div[role="log"][aria-label="Chat messages"]`) whose rows are `[data-chat-message-row="<id>"]` and are constantly re-created while scrolling, so the observer never matched a single message. Its fallback text would also have included the toolbar and the reply quote. On top of that, commands were only handled while a stream was running.
- New observer, built to EpikChat's actual DOM:
  - It reads the author (`[data-chat-message-layout]` → first button), bot tag (`[data-chat-tag="bot"]`), time (`time[datetime]`) and message text (the last block). The reply quote, toolbar, buttons and emoji fallbacks are excluded, and emoji become their characters.
  - System banners are skipped.
  - Detection uses message IDs, so each message is handled exactly once even when Virtuoso re-renders rows.
  - When the observer attaches, or when you switch rooms, the existing backlog is marked as seen and never answered. Hidden room panes are ignored.
  - It never reacts to bots, to its own replies, or to your own username. The username is read from the page, or you can set it.
- Chat replies (still opt-in) type into the real composer (`input[data-chat-composer-input]`) with DevTools `Input.insertText` + Enter. If that fails, they fall back to setting the value and submitting the form. Replies are truncated to the 250-character limit and rate-limited (default: one every 5 s).
- Commands now work when you're not live (as an overlay only while live; as chat replies if enabled).
### Added
- Settings → Chat commands: **My EpikChat username**, **minimum seconds between replies**, **debug chat log** (logs every message the observer sees), observer status ("attached to Lobby, 44 rows"), and a **Chat diagnostics** button. The button saves a sanitized DOM skeleton plus the observer state (no message text or names) for bug reports.
- Tests: a headless-Chrome mock of EpikChat's chat DOM (`EpikStreamer.Tests/mock/run-chat-mock.sh`, covering virtualization churn, system banner, reply quote, emoji, bot rows, hidden toolbar, room switch and the composer form) plus a `chatfeed` mode that runs the observed messages through the command handler.

## 2.2.1 — 2026-10-25
### Fixed
- **The stream failed to start with current ffmpeg builds** (including the BtbN master build bundled with 2.2.0): `Option thread_queue_size … cannot be applied to input url pipe:0`, `Error parsing options for input file pipe:0`, exit code -22. ffmpeg master no longer accepts `-thread_queue_size` as an input option. EpikStreamer now omits it when the build rejects it (both for the main `pipe:0` input and the background-music input).
- When ffmpeg rejects the command line (an option, filter or parse error before any output), the reconnect retries are no longer used up. The stream stops with a clear error that shows the ffmpeg line responsible.
### Added
- **ffmpeg capability probe**, run once per ffmpeg binary and cached: version, input `-thread_queue_size`, `-reinit_filter`, `-stats_period`, image2 `atomic_writing`, `flvflags`, dshow `audio_buffer_size`, the filter list (azmq, loudnorm, dynaudnorm, sidechaincompress, agate, …), the encoder list and the encoder-private options for AMF/NVENC/QSV/x264. Version-sensitive options are only emitted when the build supports them. Missing features are logged; for example, without azmq the live mute/volume controls are disabled with a notice instead of breaking the stream.
- Encoder auto-detect skips encoders that aren't compiled into the build.
- Tests now run against both the system ffmpeg (7.1) and the BtbN `ffmpeg-master-latest` build (`FFMPEG=… dotnet EpikStreamer.Tests.dll <mode>`), plus a `caps` test mode and a `fatal` test mode.
### Notes
- The `[in#0] UDTA parsing failed retrying raw` and `stream 0, timescale not set` source messages are harmless metadata warnings from the input file.
- Overlay fonts and text files are passed as relative names on purpose. ffmpeg runs with its working directory set to the overlay folder, so Windows drive-letter colons never need filtergraph escaping.

## 2.2.0 — 2026-10-25
### Added
- **Scheduling** (Schedule tab): one-off, daily or weekly entries with a start time, an optional stop time (past midnight is fine), a profile and an optional `.m3u`/`.txt` playlist. Missed starts within 2 minutes still fire. Each occurrence fires only once. Optional **launch at Windows startup** (per-user `Run` key, optionally minimized to the tray).
- **Tray mode**: minimize and/or close to the tray. The tray menu has Show, Mini player, Start/Stop, Next, Mute music and Mute mic. Balloon notifications for start, end, connection lost, reconnected and errors (can be turned off).
- **Global hotkeys** (`RegisterHotKey`), configurable. Defaults: Ctrl+Alt+S start/stop, N next, M mute music, K mute mic, O hide overlays. Conflicts are reported.
- **Chat commands**: `!song`, `!uptime`, `!help` and custom `!trigger = response` lines, with `{song} {music} {uptime} {viewers} {user}` placeholders and a cooldown. Responses appear as an on-stream overlay. **Replying in chat is off by default** (opt-in, with a confirmation).
- **Viewer alerts**: new-viewer and milestone alerts (configurable text, duration and local sound), drawn on stream via `drawtext` (`textfile` + `reload`).
- **Audio**: loudness normalization (EBU R128 `loudnorm` with a target LUFS, or `dynaudnorm`) and a main volume. Background music playlist (shuffle, volume) with sidechain ducking under the video's audio. Microphone mix-in (DirectShow) with volume, mute and noise gate.
- **Live controls** without restarting the stream (`azmq`): mute music, mute mic, hide overlays, music and mic volume. Available in the top bar, tray, hotkeys and mini player.
- **Stream history** (History tab): every session is saved (duration, peak/avg viewers, bitrate, dropped frames, reconnects, files played), with a chart of the last 30 sessions and CSV export (formula-injection safe).
- **Themes**: Dark (fully dark controls, including combo boxes, menus, scrollbars and tooltips), Light, Midnight Blue and Epik Purple. They switch live.
- **Compact mini player**: an always-on-top window with a preview, status and controls.
- App icon.
### Notes
- The chat message/input selectors are untested against the live EpikChat site. Chat commands need the chat panel open and signed in.

## 2.1.1 — 2026-10-25
### Fixed
- "Sign in to EpikChat & get stream key" no longer gets stuck at "Continue". The automation now follows EpikChat's real Go-live dialog:
  - picks the open modal by `aria-modal`/`aria-label` and visibility (ignoring the hidden emoji-picker dialog);
  - selects **RTMP encoder** in the *Stream source* group and waits for `aria-pressed="true"`;
  - clicks **Continue**, then follows the same dialog as it becomes *Choose your audience* (there is no second Continue);
  - picks Public/Private, clicks **Create live stream** (once), then reads the server and key from the *Connect your encoder* dialog after **Show stream key**.
- Clicks use a full pointer/mouse event sequence. Each step polls until its result is visible.
- Every step's status, including the dialog currently seen, is shown in the window and written to `%LOCALAPPDATA%\EpikStreamer\signin.log`.
- Reading the key is more robust: inputs, `code`/`pre`/`kbd`, copy-button data attributes, labelled text, and full `rtmp://…/x/KEY` URLs.
### Added
- Mock EpikChat page mirroring the real DOM (`EpikStreamer.Tests/mock`) with a headless-Chrome check (`run-mock.sh`).

## 2.1.0 — 2026-10-25
### Added
- Crossfade transitions between playlist items (`xfade` + `acrossfade`), 0–5 s, 16 styles. Seamless across loop, shuffle, skip and reconnect.
- Chat side panel: EpikChat `/chat` in WebView2, sharing the sign-in session (never a second chat session), compact CSS mode. Viewer count and live status shown on the Stats tab and in the title bar (selectors untested against the live site).
- Multiple outputs per profile (EpikChat / Twitch / YouTube / Kick / Custom presets) via one encode + `tee` (`onfail=ignore`), with per-destination status.
- Local recording to MKV with optional lossless remux to MP4, plus folder and filename-pattern settings.
- Per-user Inno Setup installer (bundled ffmpeg, shortcuts, uninstaller, WebView2 check).
- Auto-update from GitHub Releases (`nano11b/EpikStreamer`): checks on startup and on demand, asks before downloading, verifies SHA-256, runs the installer silently. Optional GitHub token, for rate limits only.
- `installer/build-installer.ps1`, `installer/release.ps1` (optional env-var Authenticode signing, `update.json` + `.sha256`, `gh release create`), `.gitignore`.
### Changed
- New streaming engine: segments are pre-rendered with exact timestamps, and the encoder paces with `-re`. There are no gaps or timestamp jumps between items (verified with ffprobe).
- Stream keys are DPAPI-encrypted per output and masked in all logs (including tee-escaped forms).
- v2.0 profiles are migrated automatically (single server/key → one EpikChat output).

## 2.0.0
- Playlists, overlays (logo/text/clock), hardware encoders with auto-detect, auto-reconnect, stats/preview, profiles, EpikChat sign-in & stream-key helper.

## 1.0.0
- First release: stream a single file to an RTMP server with ffmpeg.
