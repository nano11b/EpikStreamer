# Changelog

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
