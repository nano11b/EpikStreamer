#!/bin/sh
# Headless-Chrome check of Web/chat-observer.js against a mock of EpikChat's real chat DOM.
set -e
cd "$(dirname "$0")"
out=$(mktemp -d)
cp epikchat-chat.html "$out/"
{ echo 'window.__EPIK_CFG={"debug":true,"selfName":""};'; cat ../../EpikStreamer/Web/chat-observer.js; } > "$out/chat-observer.js"
google-chrome --headless=new --no-sandbox --disable-gpu --virtual-time-budget=15000 --dump-dom "file://$out/epikchat-chat.html" 2>/dev/null > "$out/dom.html"
python3 - "$out/dom.html" <<'PY'
import sys,re,json,html
d=json.loads(html.unescape(re.search(r'<title>(.*?)</title>',open(sys.argv[1]).read(),re.S).group(1)))
P=d['posted']; fails=0
def check(ok,what):
    global fails; print(('PASS ' if ok else 'FAIL ')+what); fails+= not ok
for s in d['statuses']: print('   status:',s)
for m in P: print('   msg:',m['room'],m['user'],repr(m['text']),'bot' if m['isBot'] else '','self' if m['self'] else '')
ids=[m['id'] for m in P]
check(len(ids)==len(set(ids)),'each message posted once (no duplicates across virtualization churn)')
check(ids==sorted(ids),'posted in id (chronological) order')
check(not any('backlog' in m['text'] or m['user'].startswith('old') or m['user']=='roomb' for m in P),'no backlog (attach + room switch)')
check(not any(m['user']=='hidden' for m in P),'hidden (aria-hidden) pane ignored')
txt={(m['user'],m['text']) for m in P}
check(('alice','!song') in txt and ('bob','!HELP 🚀 please') in txt and ('dave','!uptime') in txt and ('erin','!song') in txt,'commands detected (case, emoji text, after room switch)')
check(('carol','hello there') in txt,'reply row: quote excluded from text')
check(not any(m['text']=='' or 'React' in m['text'] or 'Reply' in m['text'] for m in P),'toolbar/embed/system rows not used as text')
bots=[m for m in P if m['isBot']]; check(len(bots)==1 and bots[0]['text']=='!uptime','bot row flagged isBot (and empty embed skipped)')
me=[m for m in P if m['user']=='MeStreamer']; check(len(me)==2 and all(m['self'] for m in me),'own messages flagged self (incl. the sent reply)')
check(d['sent']==['Now playing: Test Song'] and d['sendResult']=='submitted','reply send via composer form works')
check(any(s.startswith('attached to Lobby') for s in d['statuses']) and any(s.startswith('attached to Room B') for s in d['statuses']),'observer status: attach + room switch')
check(any('you are MeStreamer' in s for s in d['statuses']),'signed-in username read from header')
dg=json.dumps(d['diag']); check('MeStreamer' not in dg and 'alice' not in dg and 'hello' not in dg and d['diag']['observer']['attached'],'diagnostics sanitized (no names/text)')
# commands as the app would see them (non-bot, non-self, starts with !)
cmds=[m for m in P if not m['isBot'] and not m['self'] and m['text'].lower().startswith('!')]
check(len(cmds)==4,f'exactly 4 commands to answer: {[c["text"] for c in cmds]}')
json.dump(P,open('/tmp/chat-posted.json','w'))
print('CHAT MOCK PASSED' if fails==0 else f'{fails} FAILED'); sys.exit(1 if fails else 0)
PY
