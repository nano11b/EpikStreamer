# v2.8.1: checks the --selftest-rooms record (Lobby active, panel on screen, commands from background rooms).
import sys, json
X = json.load(open(sys.argv[1])); P = X['page']; R = X['replies']; fails = 0
def check(ok, what):
    global fails; print(('PASS ' if ok else 'FAIL ') + what); fails += not ok
print('   replies:', json.dumps(R, ensure_ascii=False)); print('   sent:', P['sent']); print('   switches:', P['switches'], 'visible:', P['visible'])
print('   rooms:', json.dumps(X['rooms'], ensure_ascii=False))
cmds = sorted((m['user'], m['room']) for m in X['received'] if m['text'] == '!help' and not m['self'])
check(cmds == sorted([('nano11b', 'ImpulsiveThots'), ('nano11b', 'Lobby'), ('zoe', "Drugs''R''Us"), ('yuki', 'ImpulsiveThots')]),
      f"!help from background rooms (and the same text in Lobby) all delivered while Lobby is active: {cmds}")
helps = [s for s in P['sent'] if s[1].startswith('Commands:')]
want = {'(nano11b)': None, '(zoe)': "Drugs''R''Us", '(yuki)': 'ImpulsiveThots'}
nano = sorted(s[0] for s in helps if '(nano11b)' in s[1])
check(nano == ['ImpulsiveThots', 'Lobby'], f"nano11b answered in ImpulsiveThots and in Lobby: {nano}")
check([s[0] for s in helps if '(zoe)' in s[1]] == ["Drugs''R''Us"], "zoe answered in the apostrophe room")
check([s[0] for s in helps if '(yuki)' in s[1]] == ['ImpulsiveThots'], "yuki answered in ImpulsiveThots")
check(len(helps) == 4, f"each command answered exactly once: {len(helps)}")
check(all(v.startswith('sent ✓') for v in R.values()) and len(R) == 4, "every reply confirmed by the site's own-message echo in its room")
check(X['queued'] == 0, "nothing left queued although the panel stayed on screen")
check(P['visible'] == 'Music', f"tab clicked by the user mid-reply (Music) kept; final tab {P['visible']}")
sw = P['switches']
i = sw.index('Music') if 'Music' in sw else -1
check(i > 0 and "Drugs''R''Us" in sw[:i] and sw[i - 1] != 'Lobby' and 'Lobby' in sw[:i], f"Lobby restored after the ImpulsiveThots reply, not after the user's own click: {sw}")
rooms = {k.lower(): v for k, v in X['rooms'].items()}
imp = next((v for k, v in rooms.items() if 'impulsive' in k), {})
check(imp.get('commandsSeen', 0) >= 2 and imp.get('repliesSent', 0) + imp.get('repliesConfirmed', 0) >= 2, f"per-room diagnostics for ImpulsiveThots: {imp}")
d = json.dumps(X['rooms']); check('!help' not in d and 'Commands:' not in d, 'per-room diagnostics carry no message text')
print('ROOMS MOCK PASSED' if fails == 0 else f'{fails} FAILED'); sys.exit(1 if fails else 0)
