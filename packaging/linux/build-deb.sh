#!/bin/bash
# Builds the Linux packages: .deb (amd64 + arm64) and portable .tar.gz for EpikStreamer.Linux.
# usage: packaging/linux/build-deb.sh VERSION [OUTDIR]     (run from anywhere; needs dotnet 8 SDK, dpkg-deb, fakeroot optional)
set -euo pipefail
V=${1:?version}; HERE=$(cd "$(dirname "$0")" && pwd); ROOT=$(cd "$HERE/../.." && pwd); OUT=${2:-$ROOT/dist/linux}
export DOTNET_CLI_TELEMETRY_OPTOUT=1
mkdir -p "$OUT"; WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
for pair in x64:amd64 arm64:arm64; do
  RID=linux-${pair%%:*}; ARCH=${pair##*:}
  PUB=$WORK/pub-$ARCH
  dotnet publish "$ROOT/EpikStreamer.Linux" -c Release -r $RID --self-contained true -p:PublishSingleFile=false \
     -p:DebugType=none -p:DebugSymbols=false -o "$PUB" -nologo -v q
  rm -f "$PUB"/*.pdb
  cp "$ROOT/EpikStreamer.Linux/Assets/icon.png" "$PUB/epikstreamer.png"
  cp "$ROOT/README.md" "$ROOT/CHANGELOG.md" "$PUB/"
  # ---- .deb ----
  P=$WORK/deb-$ARCH
  install -d "$P/DEBIAN" "$P/opt/epikstreamer" "$P/usr/bin" "$P/usr/share/applications" "$P/usr/share/doc/epikstreamer" "$P/usr/share/lintian/overrides"
  cp -a "$PUB/." "$P/opt/epikstreamer/"
  find "$P/opt/epikstreamer" -type d -exec chmod 755 {} +; find "$P/opt/epikstreamer" -type f -exec chmod 644 {} +
  chmod 755 "$P/opt/epikstreamer/epikstreamer" "$P/opt/epikstreamer/createdump" 2>/dev/null || true
  install -m 755 "$HERE/launcher.sh" "$P/usr/bin/epikstreamer"
  install -m 644 "$HERE/epikstreamer.desktop" "$P/usr/share/applications/epikstreamer.desktop"
  for s in 48 64 128 256; do
    install -d "$P/usr/share/icons/hicolor/${s}x${s}/apps"
    if command -v convert >/dev/null; then convert "$PUB/epikstreamer.png" -resize ${s}x${s} "$P/usr/share/icons/hicolor/${s}x${s}/apps/epikstreamer.png"
    else cp "$PUB/epikstreamer.png" "$P/usr/share/icons/hicolor/${s}x${s}/apps/epikstreamer.png"; fi
    chmod 644 "$P/usr/share/icons/hicolor/${s}x${s}/apps/epikstreamer.png"
  done
  cat > "$P/usr/share/doc/epikstreamer/copyright" <<CR
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: EpikStreamer
Source: https://github.com/nano11b/EpikStreamer

Files: *
Copyright: 2025-2026 nano11b
License: see https://github.com/nano11b/EpikStreamer
 Bundles the .NET 8 runtime (MIT) and Avalonia UI (MIT), SkiaSharp/HarfBuzzSharp (MIT).
CR
  { echo "epikstreamer ($V) stable; urgency=medium"; echo; echo "  * See /opt/epikstreamer/CHANGELOG.md"; echo; echo " -- nano11b <nano11b@users.noreply.github.com>  $(date -R)"; } | gzip -9n > "$P/usr/share/doc/epikstreamer/changelog.gz"
  chmod 644 "$P/usr/share/doc/epikstreamer/"*
  install -d "$P/usr/share/man/man1"; sed "s/@VERSION@/$V/g" "$HERE/epikstreamer.1" | gzip -9n > "$P/usr/share/man/man1/epikstreamer.1.gz"; chmod 644 "$P/usr/share/man/man1/epikstreamer.1.gz"
  # third-party binary app in /opt with a bundled runtime: these lintian tags are expected
  cat > "$P/usr/share/lintian/overrides/epikstreamer" <<LO
epikstreamer: dir-or-file-in-opt *
epikstreamer: embedded-library *
epikstreamer: shared-library-lacks-prerequisites *
epikstreamer: unstripped-binary-or-object *
epikstreamer: hardening-no-fortify-functions *
epikstreamer: hardening-no-relro *
epikstreamer: hardening-no-pie *
epikstreamer: shlib-without-PT_GNU_STACK-section *
epikstreamer: executable-in-usr-lib *
epikstreamer: library-not-linked-against-libc *
LO
  chmod 644 "$P/usr/share/lintian/overrides/epikstreamer"
  SIZE=$(du -sk --exclude=DEBIAN "$P" | cut -f1)
  cat > "$P/DEBIAN/control" <<CTL
Package: epikstreamer
Version: $V
Architecture: $ARCH
Maintainer: nano11b <nano11b@users.noreply.github.com>
Installed-Size: $SIZE
Depends: libc6 (>= 2.31), libstdc++6, libgcc-s1, zlib1g, ffmpeg, libgtk-3-0 | libgtk-3-0t64, libwebkit2gtk-4.1-0, libsecret-1-0, ca-certificates, libx11-6, libfontconfig1, libice6, libsm6, fonts-dejavu-core, libssl3 | libssl3t64
Recommends: libnotify-bin, pulseaudio-utils, gnome-keyring | kwalletmanager, xdg-utils, fonts-noto-color-emoji
Section: video
Priority: optional
Homepage: https://github.com/nano11b/EpikStreamer
Description: playlist live streamer for EpikChat and RTMP services
 EpikStreamer streams a playlist of videos back to back (gapless, with
 crossfades) over one RTMP connection, with overlays, background music,
 microphone, now-playing card, scheduling, chat commands and song requests.
 Includes EpikChat sign-in / stream-key helper and chat panel (WebKitGTK).
CTL
  DEB="$OUT/epikstreamer_${V}_${ARCH}.deb"
  if command -v fakeroot >/dev/null; then fakeroot dpkg-deb -Zxz --build "$P" "$DEB" >/dev/null; else dpkg-deb --root-owner-group -Zxz --build "$P" "$DEB" >/dev/null; fi
  # ---- portable tar.gz ----
  T=$WORK/tar-$ARCH/epikstreamer-$V; mkdir -p "$T"; cp -a "$PUB/." "$T/"
  cat > "$T/INSTALL-LINUX.txt" <<TXT
EpikStreamer $V — portable Linux build ($RID)
Requirements (Debian/Ubuntu):  sudo apt install ffmpeg libwebkit2gtk-4.1-0 libsecret-1-0 libnotify-bin pulseaudio-utils
Run:  ./epikstreamer
Prefer the .deb for menu entry / icon / updates:  sudo apt install ./epikstreamer_${V}_${ARCH}.deb
TXT
  tar -C "$WORK/tar-$ARCH" --owner=0 --group=0 -czf "$OUT/epikstreamer-$V-linux-$ARCH.tar.gz" "epikstreamer-$V"
done
cd "$OUT"; sha256sum epikstreamer_${V}_*.deb epikstreamer-$V-linux-*.tar.gz > SHA256SUMS-linux.txt; cat SHA256SUMS-linux.txt
