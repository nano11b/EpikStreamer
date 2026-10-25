# EpikStreamer 2.2.2

Windows desktop app (WPF, .NET 8) that streams local video files to RTMP ingests such as EpikChat's
**Go live → RTMP encoder** (server `rtmp://media.epikchat.com:1935/x` + your stream key) using ffmpeg.
Defaults match EpikChat's cap: 1280x720, 30 fps, 2500 kbps video, 128 kbps AAC, keyframe every 2 s.

## Downloads (GitHub Releases: https://github.com/nano11b/EpikStreamer/releases)
| Package | Contents |
|---|---|
| `EpikStreamer-2.2.2-setup.exe` | **Recommended.** Per-user installer (no admin rights): installs to `%LOCALAPPDATA%\Programs\EpikStreamer` with the self-contained app and ffmpeg, Start-menu shortcut, optional desktop shortcut, uninstaller (Settings → Apps). Checks for the WebView2 Runtime. |
| `EpikStreamer-v2.2.2-full-win-x64.zip` | Portable: self-contained `EpikStreamer.exe` (no .NET needed) + `ffmpeg\ffmpeg.exe`/`ffprobe.exe` (BtbN win64 GPL build: libx264, NVENC, QuickSync, AMF). Unzip and run. |
| `EpikStreamer-v2.2.2-app-win-x64.zip` | Small framework-dependent `EpikStreamer.exe`. Needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). No ffmpeg: click **Download ffmpeg** in the app. |

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
