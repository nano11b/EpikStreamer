#!/bin/sh
# Headless-Chrome check of the key-fetch automation against the mock EpikChat page (both audiences).
set -e
cd "$(dirname "$0")/.."
out=$(mktemp -d)
cp mock/epikchat-golive.html "$out/"
for aud in Public Private; do
  { dotnet run --no-build -- helperjs; echo; echo "window.__STEPS=$(dotnet run --no-build -- steps $aud);"; } > "$out/helper.js"
  google-chrome --headless=new --no-sandbox --disable-gpu --virtual-time-budget=40000 --dump-dom "file://$out/epikchat-golive.html" 2>/dev/null |
  python3 -c "
import sys,re,json,html
d=json.loads(html.unescape(re.search(r'<title>(.*?)</title>',sys.stdin.read(),re.S).group(1)))
for l in d['log']: print('  ',l)
ok=d['ok'] and 'RTMP encoder' in d['chosen'] and '$aud' in d['audience'] and d['creates']==1 and d['key']=={'server':'rtmp://media.epikchat.com:1935/x','key':'FAKE_mock_Key_12345abc'}
print('PASS' if ok else 'FAIL','mock key fetch, audience=$aud'); sys.exit(0 if ok else 1)"
done
