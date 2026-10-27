#!/bin/sh
# Runs the mock key-fetch and chat tests through the Linux app's real WebKitGTK browser host (needs DISPLAY).
# usage: run-linux-host.sh [path/to/epikstreamer]
set -e
cd "$(dirname "$0")"
B=${1:-../../EpikStreamer.Linux/bin/Debug/net8.0/epikstreamer}
"$B" --selftest-host
for aud in Public Private; do "$B" --selftest-keyfetch . $aud; done
"$B" --selftest-golive .
out=$(mktemp -d)
"$B" --selftest-chat . "$out/chat.json"
python3 - "$out/chat.json" <<'PY'
import sys,re,json,html
X=json.load(open(sys.argv[1])); d=X['page']
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
V=d['viewers']; print('   viewers:',V)
lv=[n for s,n in V if s=='LiveViewers']
check(lv[:1]==['bob,carol'] and 'bob,carol,zed' in lv and lv[-1]=='carol,zed','LIVE viewers list snapshots (join zed, leave bob)')
check(any(s=='RoomMembers' and n=='alice,MeStreamer' for s,n in V),'room members list read')
check(d['openRes']==['opened','not found (scroll the member list so the name is visible)'] and d['profileClicks']==1,'open-profile quick action clicks the normal member button')
check(d['diag']['viewers']['verified'] is False and d['diag']['viewers']['viewersFound'] and 'carol' not in json.dumps(d['diag']['viewers']),'viewer selectors flagged unverified in diagnostics (sanitized)')
R=X['received']
check([(m['user'],m['text'],m['id']) for m in R]==[(m['user'],m['text'],m['id']) for m in P],'C# (WebKit host) received exactly the posted messages, in order')
check([m['isBot'] for m in R]==[m['isBot'] for m in P] and [m['self'] for m in R]==[m['self'] for m in P],'isBot/self flags survive the WebKit bridge')
check(X['statuses']==d['statuses'],'observer status lines reach C#')
check([list(v) for v in X['viewers']]==[list(v) for v in d['viewers']],'viewer snapshots reach C#')
print('CHAT MOCK PASSED' if fails==0 else f'{fails} FAILED'); sys.exit(1 if fails else 0)
PY
# v2.5 Ball Pit: member collector + avatar fallback through the WebKitGTK bridge
"$B" --selftest-members . "$out/mem.json"
python3 members-check.py "$out/mem.json.page.json"
python3 - "$out/mem.json" <<'PY'
import sys,json
X=json.load(open(sys.argv[1])); S=X['scans']; fails=0
def check(ok,what):
    global fails; print(('PASS ' if ok else 'FAIL ')+what); fails+= not ok
check([s['n'] for s in S]==[12,150,150,11] or (len(S)==4 and S[1]['n']==150 and S[2]['n']==150),'member scans reach C# (150 after full scans)')
check(S[1]['complete'] and S[1]['total']==150 and S[1]['avatars']==120 and S[1]['colors']==113,'complete flag, total, avatar URLs and colours survive the bridge')
check(S[1]['m1']=={'Name':'member001','Avatar':'https://cdn.example.test/avatars/1.png','Color':'rgb(96, 165, 250)'},'member record parsed')
check(X['avatarIsPng'] and X['avatarBytes']>50,'FetchAvatarViaPage returns PNG bytes')
print('MEMBERS BRIDGE PASSED' if fails==0 else f'{fails} FAILED'); sys.exit(1 if fails else 0)
PY
# v2.7 live feed (tap + observer + router) through the WebKitGTK host, with the fake local WebSocket server
"$B" --selftest-tap . "$out/tap.json"
python3 tap-check.py "$out/tap.json" --host
"$B" --selftest-rooms . "$out/rooms.json"
python3 rooms-check.py "$out/rooms.json"
# v2.8 Engage: commands from several rooms → EngageEngine → replies posted through the composer in each source room
"$B" --selftest-engage . "$out/engage.json"
python3 engage-check.py "$out/engage.json"
