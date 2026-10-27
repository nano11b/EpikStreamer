#!/bin/sh
# v2.7 live feed in headless Chrome: fake WS server + tap mock page, then the page's posts replayed through the app's router.
set -e
cd "$(dirname "$0")"
T=${T:-../bin/Debug/net8.0/EpikStreamer.Tests.dll}
out=$(mktemp -d)
node run-tap-mock.js "$out/page.json" 2>&1 | grep -v -i experimental || true
test -s "$out/page.json"
dotnet "$T" feedreplay "$out/page.json" "$out/feed.json" Lobby
python3 tap-check.py "$out/feed.json"
python3 - "$out/page.json" <<'PY'
import sys,json
d=json.load(open(sys.argv[1]))['diag']; s=json.dumps(d)
ok = d and d.get('verified') is False and not any(w in s for w in ['alice','carol','!song','backlog'])
print(('PASS' if ok else 'FAIL')+' page tap diagnostics: event/field names only, flagged unverified'); sys.exit(0 if ok else 1)
PY
