#!/usr/bin/env python3
# v2.7.1 live-feed checks on what the app's router emitted (Chrome replay or the Linux WebKitGTK selftest), against the mock of
# EpikChat's VERIFIED socket.io-msgpack protocol. usage: tap-check.py result.json [--host]
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
cmds = [(m['room'], m['user'], m['text']) for m in M if not m['isBot'] and not m['self'] and m['text'].startswith('!')]
want = [('The_Bar_Alc_420', 'alice', '!help'), ('Lobby', 'dave', '!uptime'), ('Music', 'carol', '!song'), ('Room B', 'erin', '!help'), ('The_Bar_Alc_420', 'frank', '!help')]
check(cmds == want, f'all-rooms mode: commands from 4 rooms (3 not visible) fire exactly once, in order, with their room: {cmds}')
check(not any(m['user'].startswith('old') for m in M) and not any(m['user'] == 'bob' for m in M), 'sidebar-ack history and embeds never fire')
check(any(m['isBot'] and m['user'] == 'EpikBot' for m in M) and any(m['self'] and m['user'] == 'MeStreamer' for m in M), 'bot (senderType) and own (senderId = userData.userId) messages flagged → never trigger')
check(X['duplicates'] >= 1, 'the message seen by both DOM and tap was counted as a duplicate')
check(X['reconnects'] >= 1, 'socket reconnect detected')
S = [set(v['names']) for v in V if v['source'] == 'LiveViewers']
check(len(S) >= 4 and S[0] == {'bob', 'carol'} and {'bob', 'carol', 'zed'} in S and {'carol', 'zed'} in S, 'viewers populate from the periodic viewers ACK: snapshot, join, leave')
check(S and S[-1] == {'carol', 'zed', 'yan'}, 'viewers after reconnect: re-snapshot + join')
check('live feed' in X['source'], 'source indicator says live feed')
if host:
    P = X['page']; sent = P.get('sent')
    helps = [s for s in sent if s[1].startswith('Commands:')]
    check(sorted(helps) == sorted([['The_Bar_Alc_420', 'Commands: !song !uptime (alice)'], ['Room B', 'Commands: !song !uptime (erin)'], ['The_Bar_Alc_420', 'Commands: !song !uptime (frank)']]),
          f'!help answered once per command, each in its source room: {helps}')
    check(X['replies'].get('alice', '').startswith('queued'), f"reply while the panel is on screen is queued, not switched: {X['replies'].get('alice')!r}")
    check('switched' in X['replies'].get('frank', '') and X['replies'].get('erin', '').startswith('sent'), f"panel off screen: reply switched rooms ({X['replies'].get('frank')!r}); visible room sent directly")
    check(P.get('visible') == 'Room B' and X['queued'] == 0, f"switched back to the user's room afterwards, queue empty (switches {P.get('switches')})")
    check(not any(s[1].startswith('Commands:') and ('MeStreamer' in s[1] or 'EpikBot' in s[1]) for s in sent), 'no reply to own or bot messages')
    td = X.get('tapDiag'); td = json.loads(td) if isinstance(td, str) else td
    d = json.dumps(td or '')
    check(td and '"verified": true' in d and 'getBroadcastViewers' in d and not any(w in d for w in ['alice', 'carol', '!help', 'Commands:', 'FAKE-mock']), 'tap diagnostics reach C#: verified, viewers request named, no names/text/token')
print('TAP FEED PASSED' if fails == 0 else f'{fails} FAILED'); sys.exit(1 if fails else 0)
