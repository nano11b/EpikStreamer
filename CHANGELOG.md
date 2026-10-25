# Changelog

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
