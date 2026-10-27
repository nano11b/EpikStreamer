# v2.8: checks an engagereplay record (Chrome) or the Linux host's --selftest-engage record (same shape + "sent").
import sys, json, hashlib
X = json.load(open(sys.argv[1])); fails = 0
def check(ok, what):
    global fails; print(('PASS ' if ok else 'FAIL ') + what); fails += not ok
uid = lambda s: hashlib.md5(s.encode()).hexdigest()[:24]
M = X['messages']; R = X['replies']
for r in R: print('   reply:', r['room'], '|', r['text'])
check(X['selfId'] == '6a0000000000000000000001', 'self userId learned from the socket handshake')
check(all(m['userId'] == (uid(m['user']) if m['user'] != 'MeStreamer' else '6a0000000000000000000001') for m in M), 'every message carries the sender userId')
check(all(m['self'] for m in M if m['user'] == 'MeStreamer'), 'host messages flagged self (by userId)')
def room_of(substr): return [r['room'] for r in R if substr in r['text']]
check(room_of('@bob you have') == ['Music'], '!points answered in Music (the room it came from)')
check(room_of('Poll (60s): Best colour?') == ['Lobby'], 'host !poll from Lobby announced in Lobby (60 s duration parsed)')
check(room_of('bob now has 500') == ['Lobby'], 'host !addpoints answered in Lobby')
check(room_of('@zed you only have 0') == ['Music'], 'game with too few points refused in Music')
check(len(room_of('🎲 bob')) == 1 and room_of('🎲 bob')[0] == 'Music', 'bob\'s !gamble answered in Music (after the reconnect)')
res = [r for r in R if 'Poll closed' in r['text']]
check(len(res) == 1 and res[0]['room'] == 'Lobby' and 'red 67% (2)' in res[0]['text'] and 'blue 33% (1)' in res[0]['text'], 'poll result in Lobby: bob\'s changed vote + carol\'s bare vote from another room counted once each')
check(room_of('Top Epik Points') == ['The_Bar_Alc_420'] and 'bob' in [r for r in R if 'Top' in r['text']][0]['text'], '!top answered in The_Bar_Alc_420')
check(all(len(r['text'].encode('utf-16-le')) // 2 <= 250 for r in R), 'every reply ≤ 250 characters')
L = {u['Name']: u for u in X['ledger']}
check(L['bob']['Id'] == uid('bob') and L['alice']['Id'] == uid('alice') and 'MeStreamer' not in L, 'ledger keyed by userId; host never earns')
check(L['alice']['Points'] == 105, 'alice: first-chatter 100 + chat 5 (commands and votes earn nothing)')
if 'sent' in X:   # Linux host: what the mock composer actually received
    S = X['sent']; print('   composer:', S)
    check(sorted(map(tuple, S)) == sorted((r['room'], r['text']) for r in R), 'every batched reply was posted through the composer in its own room')
print('ENGAGE MOCK PASSED' if fails == 0 else f'{fails} FAILED'); sys.exit(1 if fails else 0)
