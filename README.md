# EpikStreamer 2.8.1

Desktop app for Windows (WPF, .NET 8) and **Linux** (Avalonia, .NET 8; Debian/Ubuntu .deb or portable tar.gz) that streams local video files to RTMP ingests such as EpikChat's
**Go live → RTMP encoder** (server `rtmp://media.epikchat.com:1935/x` + your stream key) using ffmpeg.
Defaults match EpikChat's cap: 1280x720, 30 fps, 2500 kbps video, 128 kbps AAC, keyframe every 2 s.

## Downloads (GitHub Releases: https://github.com/nano11b/EpikStreamer/releases)
| Package | Contents |
|---|---|
| `EpikStreamer-2.8.1-setup.exe` | **Recommended.** Per-user installer (no admin rights): installs to `%LOCALAPPDATA%\Programs\EpikStreamer` with the self-contained app and ffmpeg, Start-menu shortcut, optional desktop shortcut, uninstaller (Settings → Apps). Checks for the WebView2 Runtime. |
| `EpikStreamer-v2.8.1-full-win-x64.zip` | Portable: self-contained `EpikStreamer.exe` (no .NET needed) + `ffmpeg\ffmpeg.exe`/`ffprobe.exe` (BtbN win64 GPL build: libx264, NVENC, QuickSync, AMF). Unzip and run. |
| `EpikStreamer-v2.8.1-app-win-x64.zip` | Small framework-dependent `EpikStreamer.exe`. Needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). No ffmpeg: click **Download ffmpeg** in the app. |
| `epikstreamer_2.8.1_amd64.deb` / `epikstreamer_2.8.1_arm64.deb` | **Linux (Debian 12+, Ubuntu 22.04+, Mint, Raspberry Pi OS 64-bit).** Self-contained app (no .NET needed); ffmpeg, WebKitGTK and libsecret come from your distro. |
| `epikstreamer-2.8.1-linux-amd64.tar.gz` / `-linux-arm64.tar.gz` | Portable Linux build for other distros. See `INSTALL-LINUX.txt` inside. |

## Install on Linux
Debian / Ubuntu / Mint / Raspberry Pi OS (pick `amd64` or `arm64`, see `dpkg --print-architecture`):
```
wget https://github.com/nano11b/EpikStreamer/releases/download/v2.8.1/epikstreamer_2.8.1_amd64.deb
sha256sum -c --ignore-missing SHA256SUMS-linux.txt     # optional, after downloading SHA256SUMS-linux.txt too
sudo apt install ./epikstreamer_2.8.1_amd64.deb
epikstreamer        # or "EpikStreamer" in your applications menu
```
Remove it with `sudo apt remove epikstreamer`. Your settings stay in `~/.config/EpikStreamer`.

Other distros: install `ffmpeg`, WebKitGTK 4.1 (`webkit2gtk4.1` / `webkit2gtk-4.1`), `libsecret` and GTK 3, then
`tar xzf epikstreamer-2.8.1-linux-amd64.tar.gz && ./epikstreamer-2.8.1/epikstreamer`.

Linux notes:
- Hardware encoding: NVIDIA → NVENC (proprietary driver). Intel/AMD → VA-API: install `intel-media-va-driver` or `mesa-va-drivers`, and make sure your user is in the `render` group. **Auto-detect** on the Output / Encoder tab picks whichever works, falling back to x264.
- Microphone and sounds go through PulseAudio/PipeWire (`pulseaudio-utils` is recommended).
- Stream keys are saved in your keyring (GNOME Keyring / KWallet) when **Remember keys** is ticked. Without a keyring they're saved AES-encrypted under `~/.config/epikstreamer/` (0600).
- Updates: **Check for updates** downloads the new `.deb` and shows the `sudo apt install …` command (also copied to the clipboard). Nothing is installed as root automatically.
- Global hotkeys work under X11. Wayland sessions don't allow them, so use the buttons there.
- If WebKitGTK is missing, **Sign in / Fetch key** opens EpikChat in your normal browser and you paste the key yourself.

## Features
- **Playlist** (Source / Playlist tab): add/drag-drop MP4/MKV/MOV/AVI/WebM…, remove, reorder, loop the whole playlist, shuffle, **⏭ Next** to skip.
  Files play back to back over **one continuous RTMP connection**, and each is normalized to the output resolution (letterboxed), fps and 44.1 kHz stereo (silent files get a silent track).
- **Overlays**: PNG logo (corner, scale, opacity), text (size, color, position, background box, scrolling ticker, custom font), clock/timestamp (strftime format, e.g. `%Y-%m-%d %X`).
- **Encoders**: x264 (CPU), NVIDIA NVENC, Intel QuickSync, AMD AMF. **Auto-detect** test-encodes a tiny clip with each one and selects the best working encoder.
- **Auto-reconnect**: if ffmpeg exits unexpectedly, the connection drops or output stalls for 25 s, the app retries with backoff (2, 4, 8, 16, 32, 60 s, up to *Max reconnect attempts*). It resumes the current playlist item a few seconds before where it stopped. The counter resets after 60 s of healthy streaming.
- **Stats / Log**: viewers and live status (from the chat panel), per-destination status, fps, bitrate, speed, dropped/duplicated frames, uptime, reconnect count, current item, a 2-minute bitrate graph, a ~1 fps preview of exactly what is sent (overlays included), and the full ffmpeg log (every stream key masked as `****`).
- **Profiles**: named profiles (outputs, recording, encoder, resolution/fps/bitrates, overlays, playlist) with New / Rename / Duplicate / Delete and a quick-switch dropdown.
  Saved to `%APPDATA%\EpikStreamer\profiles.json`. Stream keys are saved only when **Remember keys** is ticked, and then it's encrypted with Windows DPAPI (CurrentUser). Otherwise it's kept in memory for the session only.
  A v1 `settings.json` is migrated into a profile named **Default**, and v2.0 profiles (one server + key) become a single EpikChat output.

## New in 2.8.0 — Engage (points, rewards, games, polls)
Turn it on/off and configure everything on the **Engage** tab (per profile). Points are earned while you're live (watch time from the viewers list), for chatting (anti-spam), for chatting first and for daily streaks; they're stored per EpikChat user id in `engage/points.json` with daily backups. Replies use **↩ Reply in chat** (batched, ≤ 250 characters); cards are drawn on stream (into the Ball Pit when it's live).

### Chat command reference
| Command | Who | What |
|---|---|---|
| `!points [@user]` | everyone | balance and rank |
| `!top` | everyone | top N in chat + leaderboard card on stream |
| `!give @user N` | everyone (toggle) | transfer points |
| `!addpoints @user N` / `!removepoints @user N` | host, mods | adjust a balance |
| `!rewards` / `!redeem <name> [text]` | everyone | list / redeem a reward (song, bigball, shake, alert, sound) |
| `!approve N` / `!deny N` | host, mods | pending redemptions (deny refunds) |
| `!gamble <amount\|all\|50%\|1k>` | everyone | double or nothing |
| `!slots <amount>` | everyone | pair ×1.5, triple ×8, 7-7-7 ×25 |
| `!duel @user <amount>` → `!accept` / `!decline` | everyone | 50/50, 60 s to accept |
| `!trivia` | host, mods | ask a question (or every N minutes); first correct answer wins |
| `!heist <amount>` | everyone | join within 60 s; odds grow with the crew |
| `!race` / `!joinrace` / `!bet <racer> <amount>` | host, mods / everyone | Ball Pit race; backers of the winner share the pot |
| `!poll "Question" a \| b \| c [seconds]` / `!endpoll` | host, mods | start / end a poll |
| `!vote N` (or just `N` / the option text) | everyone | vote; voting again changes it |
| `!prediction "Question" a \| b` | host, mods | open a prediction |
| `!predict <outcome#> <points>` | everyone | stake points |
| `!lock` / `!resolve N` / `!cancelprediction` | host, mods | lock, pay out (parimutuel), refund |

Trivia packs are JSON: `[{"q": "Question?", "a": ["answer", "alt"], "category": "music"}]`.

## New in 2.7.1
- **Chat commands work in every room you're in.** The live feed now decodes EpikChat's real realtime protocol (socket.io with binary msgpack packets, verified from a signed-in capture), so `!help`, `!song`, requests etc. are seen in all joined rooms, not just the one on screen. The default **Command room** is now `*` (all rooms; settings from 2.7.0 that were blank are moved to `*` once). You can still type one room name.
- **Replies go back to the room the command came from.** Turn them on or off with the **↩ Reply in chat** toggle on the chat panel's bar (Linux: the **↩ Reply** button next to 💬 Chat); the first time you open the chat panel you're asked "Reply to chat commands in chat? (posts as you)". Replies are always typed into EpikChat's own chat box (never sent over the socket) and stay rate-limited. For a room that isn't showing, the app switches to it through the sidebar, types the reply and switches back — **only while the chat panel is off screen**; while you're looking at it, the reply waits until that room is showing or the panel is hidden.
- **Viewers list populates** from the periodic live-viewers update EpikChat's page already receives, and from the "Your live stream viewers" sidebar.
- **Go live finishes by itself:** after reading the stream key the app clicks the **Done** button of EpikChat's "Connect your encoder" dialog (there is no other confirm step) and starts streaming. "Already live" is detected from the "Your live stream viewers" sidebar / **End live stream** button.

## New in 2.7.0
- **Seamless chat:** chat commands, requests, Ball Pit jumps, alerts and the viewer list now work whichever EpikChat tab, room or panel is showing. You no longer need to keep "Viewing me" open or sit in the room you stream to. The 💬 Chat panel still has to be loaded and signed in.
  - **Live feed:** the app passively reads the chat and viewer events EpikChat's page already receives (read-only, it never sends anything) and merges them with the on-screen reader, so each message counts exactly once. If the live feed isn't recognized, it falls back to the on-screen reader. Settings → Chat shows which one is active ("Chat: live feed ✓" or "DOM fallback").
  - **Command room:** `*` = all rooms you've joined (default since 2.7.1); blank = the room shown when you go live; or type a room name.
  - **Viewer list fallback:** if no live viewer events arrive, the app re-reads the list the "Viewing me" panel uses, at most every 20 s.
  - **Replies** (still off by default) go to the command room. If another room is showing, they wait until you open it. **Allow switching room to reply** switches there briefly and back.
  - ⚠ EpikChat's realtime protocol isn't public, so the live feed recognizes events by their shape and has not yet been verified against the live site. **Chat diagnostics…** now includes a sanitized summary of event and field names (no text, names, tokens or cookies) to help tune it. Advanced: **Protocol map** overrides event names.

## New in 2.6.1
- Fixed Ball Pit + Radio streams falling behind real time and stuttering ("Resumed reading … after a lag of N s" in the ffmpeg log), especially with the bundled ffmpeg 8 build. A live Ball Pit now sets the pace: no `-re`, wall-clock timestamps, and the now-playing card is drawn into the Ball Pit.
- New **pace watchdog**: if the encoder can't keep up, Ball Pit quality drops automatically (every 2nd frame, then a smaller render size) and the app says so.
- AMD AMF: low-latency speed preset, capped at 2.5 Mbps.

## New in 2.6
- **Radio** tab: background music from an internet radio station (paste a `.pls`/`.m3u` playlist or stream URL), with its own volume, mute, levelling and ducking.
  - If the station drops, the stream carries on with silence while the radio reconnects in the background.
  - AzuraCast stations also get now-playing info (on the now-playing card and in an optional Ball Pit radio card), `!song`/`!np`/`!lastsong` answers, and optional `!request` song requests (off by default).
  - An API key is optional; if you set one, it's stored encrypted.
  - Optional Ball Pit beat pulse: the balls bounce to the music.

## New in 2.5
- **Ball Pit** tab: every room member, LIVE viewer or recent chatter becomes a bouncy 3D ball with their avatar and name color.
  - Joiners drop in, leavers pop, and chat makes a ball jump with a speech bubble.
  - Chat commands `!bounce`, `!bigball`, `!color <#hex>` and `!shake` change the balls.
  - Stream it as the whole video (**Source**) or as a transparent **Overlay** on your playlist.
  - The tab has a live preview and test users.
  - ⚠ The avatar and name-color selectors are untested against the live site; see `CHANGELOG.md`.

## New in 2.4.1
- **Start opens the EpikChat live session automatically.** No more clicking the camera icon first. It also runs on reconnects and scheduled starts. It's controlled by *Output → Open Go live on EpikChat automatically when starting* (on by default).
  - If the automation can't finish, click the camera icon in the chat panel. The encoder keeps retrying.
  - ⚠ The selectors are untested against the live site; see `CHANGELOG.md`.

## New in 2.4
- **Linux version** (x64 + arm64) with feature parity: .deb packages and a portable tar.gz. It uses WebKitGTK sign-in/key fetch/chat, keyring-stored keys, NVENC/VA-API/QSV/x264 and apt-friendly updates. See *Install on Linux* above.

## New in 2.3
- **Library** tab: watch media folders (with or without subfolders). A scan reads title/artist, duration, resolution and codecs with ffprobe and makes thumbnails. Results are cached in `%APPDATA%\EpikStreamer\library`, so a rescan only probes new or changed files. You get a list or grid view with search, sort (title, duration, recently added, most played, folder, resolution) and filters (HD/SD, short/long, never played, missing metadata). Saved playlists can be created, renamed, reordered and filled by dragging from the library, then used as the stream playlist or queued.
- **Song requests** (Requests tab): `!request <name>` / `!sr <name>` finds the closest library match (typo-tolerant). `!queue` shows what's next, `!skip` is a vote skip (N votes or a % of viewers) and `!wrongsong` lets the requester undo theirs. You can set a per-user limit, cooldown, max queue length, max song length, allow/block lists and optional host approval, and choose whether requests play next or after the current playlist pass. The host can approve, deny, reorder and clear. Replies appear on stream and are only posted in chat if "Reply in chat" is on.
- **Now playing card** (Overlays tab): a card with the title, thumbnail, progress/time, up next and who requested it. It can appear for N seconds on each track change or stay up all the time, and its position, width, colors and opacity are configurable. It's redrawn live without restarting the stream.
- **Viewers** tab: names are read from the chat panel's LIVE → Viewers list and/or the Room members list. It shows who's here, join/leave times, total watch time and visits. You can turn on an optional on-stream welcome alert. Session history records unique viewers. **Open profile** clicks the name in EpikChat's own list. There are no automated moderation actions. ⚠ The Viewers-list selectors are not verified against the live site yet (see Chat diagnostics).

## New in 2.2
- **Schedule** tab: one-off, daily or weekly streams with start/stop times, a profile and a playlist file. Settings → *Launch at Windows startup* lets the schedule run unattended.
- **Tray & hotkeys**: minimize/close to the tray, balloon notifications, global hotkeys (Settings tab; defaults Ctrl+Alt+S/N/M/K/O).
- **Chat commands & alerts** (Settings tab): `!song`, `!uptime`, `!help` and your own `!command = text` lines, shown as an on-stream overlay. Posting replies into chat is **off by default**. New-viewer and milestone alerts appear on stream, with an optional local sound. The chat panel must be open (signed in) for commands to be seen. Use **Chat diagnostics** and **debug chat log** if something isn't detected.
- **Audio** tab: loudness normalization (target LUFS), background music with ducking, microphone mix-in with a noise gate. Music and mic can be muted or adjusted live.
- **History** tab: per-session stats, a chart and CSV export.
- **Themes**: Dark, Light, Midnight Blue and Epik Purple (Settings tab), plus a compact always-on-top **mini player** (⧉ Mini).

## New in 2.1
### Crossfade transitions
Source / Playlist → **Transition** (16 styles: fade, fadeblack, fadewhite, dissolve, slide left/right/up/down, wipe left/right, circleopen/close, radial, smoothleft, pixelize, distance) and **Crossfade** 0–5 s (0 = hard cut, default 1 s).
Video uses ffmpeg `xfade`, audio uses `acrossfade`. Transitions apply between every pair of items, including the loop back to the first item, shuffle order and **⏭ Next** (skipping starts a crossfade into the next item within ~1–3 s).
After a reconnect the stream resumes about 2 s before the drop (or at the start of the incoming item if the drop happened mid-transition).
Each crossfade is capped at half the length of the items on either side.

### Chat side panel  ⚠ selectors untested against the live site
Click **💬 Chat** (top right) to show `https://www.epikchat.com/chat` in a resizable panel next to the controls. It uses the **same WebView2 session as Sign in**, so there is only ever **one** EpikChat chat session:
the panel unloads itself while the sign-in window is open, unloads when hidden, links to other sites open in your normal browser, and pop-up windows are not created.
**Compact** hides site header/footer/sidebars with CSS. **⟳** reloads.
A small script in the page (MutationObserver) reads the **viewer count** and **live status** and posts them to the app. They're shown on the Stats tab and in the title bar (`● LIVE · 42 viewers`).
The page selectors/regexes live in the `UNTESTED` section of `EpikChatAutomation.cs` (`ViewerCountSelectors`, `LiveSelectors`, `CompactCss`). They were written without access to the live site and only tested against a mock page, so expect to adjust them.

### Multiple outputs & local recording
Output / Encoder → **Outputs**: add any number of destinations with presets (**EpikChat**, **Twitch**, **YouTube**, **Kick**, **Custom**), each with its own server URL, key and an *Enabled* box.
Everything is encoded **once** and sent to all destinations via ffmpeg's `tee` muxer with `onfail=ignore`, so one destination failing doesn't stop the others.
The Stats tab shows each destination's status (Connecting / Live / Failed + reason). If *all* network destinations fail, the normal auto-reconnect kicks in.
Mind the per-service limits: EpikChat caps at 720p30 / 2500 kbps, so if one output is EpikChat keep the profile at those settings.
The Kick server URL differs per account, so copy yours from Kick's dashboard.
**Local recording**: tick **Record to disk** to save the same encoded stream as `.mkv` (crash-safe). Choose a folder (default `Videos\EpikStreamer`) and a filename pattern (`{profile}`, `{date}`, `{time}`, `{part}`).
With **Remux to MP4 when stopped** checked, it's losslessly converted to `.mp4` after the stream ends. A reconnect starts a new `_partN` file.
Keys are kept in memory, or DPAPI-encrypted in `profiles.json` when **Remember keys** is ticked. They're masked everywhere in the log.

### Installer & auto-update
On startup (optional, Output / Encoder → **Updates**) and with **⟳ Check for updates**, the app asks the update source for the latest release.
The default source is `https://api.github.com/repos/nano11b/EpikStreamer/releases/latest` (public repo, **no token needed**). The optional **GitHub token** field only exists to raise GitHub's API rate limit; it's stored DPAPI-encrypted and only sent to api.github.com.
From the release it reads `update.json` (`{"version","url","sha256","notes"}`), or else the `*-setup.exe` asset plus `<setup>.exe.sha256` / `SHA256SUMS.txt`.
If the version is newer, the app shows the notes and asks you first. Then it downloads the installer over HTTPS, **verifies the SHA-256** (and deletes the file on a mismatch), and asks again before closing and running the installer silently. The installer relaunches the app afterwards.
You can also point the URL at your own `update.json` (HTTPS only). See `installer/update.example.json`.

## Sign in to EpikChat & get stream key
On Output / Encoder, click **Sign in to EpikChat & get stream key**. A window shows EpikChat's real login page (Microsoft Edge WebView2), and you sign in yourself, so EpikStreamer never sees or stores your password.
The browser session is kept in `%LOCALAPPDATA%\EpikStreamer\WebView2`, so next time you're usually already signed in. Then click **I'm signed in – get stream key**, and the app drives EpikChat's normal pages for you:
open `/chat` → dismiss first-visit popups → **Go live** → **RTMP encoder (OBS and streaming software)** → Continue → audience **Public**/**Private** (the *Audience* setting, same dialog, no second Continue) → **Create live stream** → **Show stream key**.
It then reads the server URL (expected `rtmp://media.epikchat.com:1935/x`) and the key from the page and fills them into the current profile. Each step is shown in the window. If a step can't be found, it tells you which one. You can finish that step by hand in the page and click **Read key from page**.
- All page labels/selectors are text-based and live in one file: `EpikChatAutomation.cs`. Edit the patterns there if EpikChat renames a button.
- EpikChat allows **one chat session per account** and **limits logins per IP**. Opening the chat in the app may take over (disconnect) a chat session open elsewhere, and the app warns before doing this. The chat panel reuses the same session (see below).
- **Sign out** (in the window or next to the button) clears the WebView2 profile.
- Requires the Microsoft Edge **WebView2 Runtime**, which ships with Windows 10 and 11 (otherwise: https://go.microsoft.com/fwlink/p/?LinkId=2124703).
- The steps follow EpikChat's real Go-live dialog (DOM inspected Oct 2026), verified against a mock page with the same structure (`EpikStreamer.Tests/mock`). Each step and the dialog it sees are logged to `%LOCALAPPDATA%\EpikStreamer\signin.log`.

## How it works
For each playlist item, a "feeder" ffmpeg renders the item (scaled/padded to WxH, target fps, 48 kHz PCM) as MPEG-TS faster than real time, stamped with its exact position on the stream timeline.
Each transition is rendered as its own short segment: the last *X* s of item A and the first *X* s of item B go through `xfade`/`acrossfade`.
The app pumps the segments back to back (fixing up TS continuity counters) into the stdin of one long-lived "encoder" ffmpeg. That encoder paces playback (`-re`), applies the overlay `filter_complex` (overlay + drawtext), encodes once with the chosen encoder and writes the result to one RTMP/FLV connection, or to several outputs plus the recording through `tee`. It also writes `preview.jpg` once a second.
Working files go to `%LOCALAPPDATA%\EpikStreamer\session`.

## ffmpeg lookup order
1. The path set in Output / Encoder → ffmpeg.exe
2. `ffmpeg\ffmpeg.exe` next to `EpikStreamer.exe` (bundled in the full package)
3. `ffmpeg.exe` next to `EpikStreamer.exe`
4. `%LOCALAPPDATA%\EpikStreamer\ffmpeg\ffmpeg.exe` (where **Download ffmpeg** puts it; source: https://github.com/BtbN/FFmpeg-Builds/releases, `ffmpeg-master-latest-win64-gpl.zip`)
5. `PATH`, e.g. after `winget install Gyan.FFmpeg`

`ffprobe.exe` next to ffmpeg is used to detect file durations and missing audio tracks. Without it, every file is assumed to have audio.

## Build
Requires the .NET 8 SDK.
```
dotnet build EpikStreamer
dotnet publish EpikStreamer -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
dotnet publish EpikStreamer -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true    # small, needs .NET 8 Desktop Runtime
```
`EnableWindowsTargeting` is set, so the app also builds on Linux/macOS; it only runs on Windows.
`EpikStreamer.Tests` is a cross-platform console harness: `dotnet run -- unit` (unit tests), `-- xfade` / `-- drop` (real feeder/encoder pipeline with transitions, multi-output, recording, reconnect, against local files).

### Linux build and packages
Requires the .NET 8 SDK, `dpkg-deb` and `fakeroot` (`lintian` is optional).
```
dotnet run --project EpikStreamer.Linux                       # run from source (needs libwebkit2gtk-4.1-0, ffmpeg)
packaging/linux/build-deb.sh 2.5.0 dist/linux                 # amd64 + arm64 .deb, tar.gz, SHA256SUMS-linux.txt
EpikStreamer.Tests/mock/run-linux-host.sh                     # WebKitGTK host, mock key fetch and mock chat through the Linux app
```

### Installer and release (Windows)
Requires Inno Setup 6 (`winget install JRSoftware.InnoSetup`). Add the GitHub CLI if you want to publish.
```
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1            # dist\full, dist\app, dist\EpikStreamer-<ver>-setup.exe
powershell -ExecutionPolicy Bypass -File installer\release.ps1                    # + .sha256, update.json, zips
powershell -ExecutionPolicy Bypass -File installer\release.ps1 -Sign -Publish     # + Authenticode signing + gh release create
```
`-FfmpegDir C:\path\to\bin` uses your own ffmpeg; otherwise the BtbN build is downloaded.
Signing is optional. The certificate only ever comes from environment variables (`SIGN_CERT_THUMBPRINT`, or `SIGN_CERT_PATH` + `SIGN_CERT_PASSWORD`; `SIGN_TIMESTAMP_URL`, `SIGNTOOL`), and nothing secret is in the repo.
The app exe is signed before packaging, then the installer, and each signature is verified.
`-Publish` runs `gh release create v<ver> <setup.exe> <setup.exe.sha256> update.json <zips> --repo nano11b/EpikStreamer`. Add `-NotesFile` for release notes and `-Draft` for a draft.
Without signing, Windows SmartScreen will warn about the unsigned installer.

## License note
The bundled ffmpeg is a GPL build (see `ffmpeg\LICENSE.txt`); source is at https://github.com/BtbN/FFmpeg-Builds and https://ffmpeg.org.
