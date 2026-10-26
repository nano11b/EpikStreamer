#!/bin/sh
# v2.4.1: headless-Chrome (Chromium = the WebView2 engine) check of the Start-button "Go live" helper JS against the session mock
# (ingest refused until a session exists). Same flow as LiveSession.EnsureAsync/FinalizeAsync; the C# side runs on the WebKit host test.
set -e
cd "$(dirname "$0")/.."
out=$(mktemp -d)
for q in "" "?noaria=1" "?plain=1" "?live=1"; do
 for aud in Public Private; do
  { dotnet run --no-build -- helperjs; echo; echo "window.__STEPS=$(dotnet run --no-build -- steps $aud);"; } > "$out/helper.js"
  sed 's#</body>#<script src="helper.js"></script><script src="runner.js"></script></body>#' mock/epikchat-session.html > "$out/s.html"
  cat > "$out/runner.js" <<'JS'
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms)); const log = []; const E = window.__epik;
  const before = __mockIngest('FAKE_mock_Key_12345abc'); let ok = true, key = {};
  const st0 = E.live();
  if (!st0.live) {
    for (const s of window.__STEPS.filter(s => s.Id !== 'showkey')) {
      const end = Date.now() + s.TimeoutSec * 1000; let r;
      while (Date.now() < end) { r = E.step(s.Id, s.Arg); const l = s.Id + ': ' + r.state + ' (' + r.info + ')';
        if (log[log.length - 1] !== l) log.push(l); if (r.state === 'done') break; await sleep(500); }
      if (r.state !== 'done' && !s.Optional) { ok = false; break; }
    }
    for (let i = 0; i < 20 && ok; i++) { key = E.read(); if (key.key) break; E.step('showkey'); await sleep(500); }
  }
  const ing = key.key ? __mockIngest(key.key) : '';
  let fin = '';
  for (let i = 0; i < 40 && !st0.live; i++) { await sleep(500); const r = E.step('finalize'); fin = r.state + ' ' + r.info; if (E.live().live) break; }
  const st1 = E.live(); const diag = E.goLiveDiag();
  document.title = JSON.stringify({ ok, before, ing, st0, st1, fin, key, creates: __creates, golive: __golive, finals: __finalClicks, cancelled: !!window.__cancelled,
    chosen: window.__chosen || '', audience: window.__audience || '', diagVerified: diag.verified ?? (diag.selectors||{}).verified, log });
})();
JS
  google-chrome --headless=new --no-sandbox --disable-gpu --virtual-time-budget=60000 --dump-dom "file://$out/s.html$q" 2>/dev/null |
  python3 -c "
import sys,re,json,html
d=json.loads(html.unescape(re.search(r'<title>(.*?)</title>',sys.stdin.read(),re.S).group(1)))
for l in d['log']: print('  ',l)
q='$q'; aud='$aud'
if 'live' in q: ok=d['st0']['live'] and d['creates']==0 and d['golive']==0
else: ok=(d['ok'] and 'no live session' in d['before'] and d['ing']=='accepted' and d['creates']==1 and d['golive']==1 and 'RTMP encoder' in d['chosen']
   and aud in d['audience'] and d['key'].get('key')=='FAKE_session1_Key_9z8y' and d['finals']==1 and not d['cancelled'] and d['st1']['live'])
print('PASS' if ok else 'FAIL', 'chrome go-live session mock', q or '(nav camera)', aud, '|', d['fin'], '|', d['st1']['how'])
if not ok: print(json.dumps(d)[:1500])
sys.exit(0 if ok else 1)"
  [ "$q" = "?live=1" ] && break
 done
done
