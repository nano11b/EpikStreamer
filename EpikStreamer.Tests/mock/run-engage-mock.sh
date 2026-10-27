#!/bin/sh
# v2.8 Engage end-to-end in headless Chrome: fake WS server (engage scenario) + tap mock page → bridge/router → EngageEngine.
set -e
cd "$(dirname "$0")"
T=${T:-../bin/Debug/net8.0/EpikStreamer.Tests.dll}
out=$(mktemp -d)
EPIK_SCENARIO=engage node run-tap-mock.js "$out/page.json" 2>&1 | grep -v -i experimental || true
test -s "$out/page.json"
dotnet "$T" engagereplay "$out/page.json" "$out/engage.json"
python3 engage-check.py "$out/engage.json"
