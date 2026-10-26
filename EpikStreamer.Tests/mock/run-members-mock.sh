#!/bin/sh
# v2.5 Ball Pit: headless-Chrome check of the member collector (virtualized Room members list) in Web/chat-observer.js.
set -e
cd "$(dirname "$0")"
out=$(mktemp -d)
cp epikchat-members.html "$out/"
{ echo 'window.__EPIK_CFG={"debug":true,"selfName":""};'; cat ../../EpikStreamer/Web/chat-observer.js; } > "$out/chat-observer.js"
google-chrome --headless=new --no-sandbox --disable-gpu --virtual-time-budget=30000 --dump-dom "file://$out/epikchat-members.html" 2>/dev/null > "$out/dom.html"
python3 members-check.py "$out/dom.html"
