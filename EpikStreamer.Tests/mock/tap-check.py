#!/usr/bin/env python3
# v2.7 live-feed checks on what the app's router emitted (Chrome replay or the Linux WebKitGTK selftest).
# usage: tap-check.py result.json [--host]   (protocol is the INFERRED mock protocol; EpikChat's real one is unverified)
import sys, json
X = json.load(open(sys.argv[1])); host = '--host' in sys.argv
M = X.get('messages', X.get('received')); V = X['viewers']; fails = 0
def check(ok, what):
    global fails; print(('PASS ' if ok else 'FAIL ') + what); fails += not ok
for m in M: print('   msg:', m['room'], m['user'], repr(m['text']), 'bot' if m['isBot'] else '', 'self' if m['self'] else '')
for v in V: print('   viewers:', v['source'], ','.join(v['names']))
print('   source:', X['source'], '| tap', X['fromTap'], 'dom', X['fromDom'], 'dup', X['duplicates'], 'otherRoom', X['otherRoom'], 'unmatched', X['unmatched'], 'reconnects', X['reconnects'])
ids = [m['id'] for m in M]
check(len(ids) == len(set(ids)), 'no message emitted twice (DOM + tap + reconnect replay deduped)')
cmds = [(m['user'], m['text']) for m in M if not m['isBot'] and not m['self'] and m['text'].startswith('!')]
check(cmds == [('alice', '!song'), ('dave', '!uptime'), ('erin', '!song'), ('frank', '!help')], f'commands from the command room fire exactly once, in order, while another room is shown: {cmds}')
check(all(m['room'] == 'Lobby' for m in M), 'only command-room messages reach the app (Room B traffic filtered)')
check(not any(m['user'].startswith('old') for m in M), 'history/backlog (incl. the replay after reconnect) never fires')
check(any(m['isBot'] for m in M) and any(m['self'] for m in M), 'bot and own messages flagged (ignored by the command handler)')
check(X['duplicates'] >= 1, 'the message seen by both DOM and tap was counted as a duplicate')
check(X['reconnects'] >= 1, 'socket reconnect detected')
S = [set(v['names']) for v in V if v['source'] == 'LiveViewers']
check(len(S) >= 4 and S[0] == {'bob', 'carol'} and {'bob', 'carol', 'zed'} in S and {'carol', 'zed'} in S, 'viewers populate with the panel closed: snapshot, join, leave')
check(S and S[-1] == {'carol', 'zed', 'yan'}, 'viewers after reconnect: re-snapshot + join')
check('live feed' in X['source'], 'source indicator says live feed')
if host:
    P = X['page']
    check(X['reply'].startswith('queued'), f"reply while Room B is visible is queued: {X['reply']!r}")
    check(P.get('sent') == [['Lobby', 'Now playing: Test Song']] and X['queued'] == 0, f"queued reply posted once the command room is visible: {P.get('sent')}")
    td = X.get('tapDiag'); td = json.loads(td) if isinstance(td, str) else td
    d = json.dumps(td or '')
    check(td and '"verified": false' in d and not any(w in d for w in ['alice', 'carol', '!song', 'Now playing']), 'tap diagnostics reach C#, flagged unverified, no names/text')
print('TAP FEED PASSED' if fails == 0 else f'{fails} FAILED'); sys.exit(1 if fails else 0)
