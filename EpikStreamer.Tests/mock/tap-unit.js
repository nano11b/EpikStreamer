// Unit test of Web/chat-tap.js decoding/classification across plausible realtime framings (node, no browser).
const fs = require('fs'), vm = require('vm');
const src = fs.readFileSync(__dirname + '/../../EpikStreamer/Web/chat-tap.js', 'utf8');
function load(cfg) {
  const out = [];
  const win = { __EPIK_TAP_CFG: cfg || {}, chrome: { webview: { postMessage: m => out.push(m) } }, location: { href: 'https://www.epikchat.com/chat', origin: 'https://www.epikchat.com' }, document: { visibilityState: 'visible' } };
  win.window = win;
  const ctx = vm.createContext(Object.assign(win, { URL, TextDecoder, setInterval: () => 0, setTimeout, console, XMLHttpRequest: function () { }, JSON, Date, Math, RegExp, Map, Set, Object, Array, String, Number, DataView, Uint8Array, ArrayBuffer, BigInt, Promise, Error }));
  ctx.XMLHttpRequest.prototype.open = function () { }; ctx.XMLHttpRequest.prototype.send = function () { };
  vm.runInContext(src, ctx);
  return { win, out, feed: s => win.__epikTapHandle(typeof s === 'string' ? s : JSON.stringify(s)) };
}
let fails = 0; const check = (ok, what) => { console.log((ok ? 'PASS ' : 'FAIL ') + what); if (!ok) fails++; };
const msgs = o => o.out.filter(m => m.type === 'epik-chat');

// 1. socket.io v4
let t = load();
t.feed('0{"sid":"x","pingInterval":25000}'); t.feed('40'); t.feed('2');
t.feed('42["message:new",{"_id":"6721aaaaaaaaaaaaaaaaaaa1","roomId":"r1","roomName":"Lobby","user":{"username":"alice"},"text":"!song","createdAt":"2026-10-27T10:00:00Z"}]');
t.feed('42["typing",{"roomId":"r1","user":{"username":"bob"}}]');
t.feed('42["message:new",{"_id":"6721aaaaaaaaaaaaaaaaaaa1","roomId":"r1","user":{"username":"alice"},"text":"!song"}]');   // repeat frame
let m = msgs(t);
check(m.length === 1 && m[0].user === 'alice' && m[0].text === '!song' && m[0].roomId === 'r1' && m[0].room === 'Lobby' && m[0].source === 'tap', 'socket.io: message parsed once (room id+name, typing ignored)');
// 2. {type,data} plain JSON with nested message + envelope room
t = load();
t.feed({ type: 'chat.message', data: { room: { id: 'r2', name: 'Room B' }, message: { id: 'm2', author: { displayName: 'Bob', isBot: true }, body: 'hi <b>there</b>', ts: 1793000000 } } });
m = msgs(t); check(m.length === 1 && m[0].isBot && m[0].text === 'hi there' && m[0].roomId === 'r2' && m[0].room === 'Room B' && /^20/.test(m[0].time), '{type,data}: nested message, envelope room, bot flag, html stripped, epoch time');
// 3. Phoenix + Centrifugo
t = load();
t.feed(['1', '5', 'room:r3', 'new_msg', { id: 'p1', user: 'carol', text: '!uptime' }]);
t.feed({ push: { channel: 'chat:r4', pub: { data: { id: 'c1', sender: { nick: 'dave' }, content: '!help' } } } });
m = msgs(t); check(m.length === 2 && m[0].roomId === 'room:r3' && m[1].roomId === 'chat:r4' && m[1].user === 'dave', 'Phoenix and Centrifugo framings (topic as room)');
// 4. history batches are backlog
t = load();
t.feed({ event: 'room:history', payload: { messages: [{ id: 'h1', user: 'x', text: 'old' }, { id: 'h2', user: 'y', text: '!song' }] } });
t.feed({ event: 'messages', payload: [1, 2, 3, 4].map(i => ({ id: 'b' + i, user: 'u' + i, text: 'old ' + i })) });
t.feed({ event: 'message', payload: { id: 'h2', user: 'y', text: '!song' } });   // same id later: already seen
check(msgs(t).length === 0, 'history / big batches never become new messages (ids remembered)');
// 5. viewers
t = load();
t.feed('42["stream:viewer_joined",{"streamId":"s1","user":{"username":"zed"}}]');
t.feed('42["stream:viewer_left",{"streamId":"s1","user":{"username":"bob"}}]');
t.feed('42["stream:viewers",{"viewers":[{"username":"carol"},{"username":"zed"}]}]');
t.feed({ type: 'presence.join', data: { roomId: 'r1', user: { username: 'eve' } } });
const v = t.out.filter(x => x.type === 'epik-tap-viewers').map(x => x.op + ':' + x.names.join(','));
check(JSON.stringify(v) === JSON.stringify(['join:zed', 'leave:bob', 'snapshot:carol,zed']), 'viewer join / leave / list events: ' + v.join(' '));
check(t.out.some(x => x.type === 'epik-tap-members' && x.op === 'join' && x.name === 'eve' && x.roomId === 'r1'), 'room membership event (presence.join)');
// 6. map override
t = load({ map: { messageEvents: '^zz$', eventField: 'k', payloadField: 'v', text: 'q', user: 'w', id: 'n' } });
t.feed({ k: 'zz', v: { n: 'o1', w: 'frank', q: '!song' } });
t.feed({ k: 'chat', v: { n: 'o2', w: 'gina', q: '!song' } });
m = msgs(t); check(m.length === 1 && m[0].user === 'frank', 'CFG.map overrides event name and field paths');
// 7. diagnostics sanitized
t = load();
t.feed('42["message:new",{"_id":"d1","roomId":"r1","user":{"username":"secretuser"},"text":"secret text","token":"abc.def.ghi"}]');
const dg = JSON.stringify(t.win.__epikTapDiag());
check(!/secretuser|secret text|abc\.def/.test(dg) && /message:new/.test(dg) && /"username":"string"/.test(dg) && /"verified":true/.test(dg), 'diagnostics: event names + field names/types only (no values)');
// 8. junk never throws
t = load(); for (const j of ['', '3', '{bad', '42[bad', '[]', 'null', '42["x"]', '{"type":"message","data":null}']) t.feed(j);
check(msgs(t).length === 0, 'malformed frames ignored');
// ---- v2.7.1: EpikChat's verified framing: socket.io-msgpack binary packets ----
{
  const { encode } = require('./msgpack-enc');
  const t = load(); const B = (pkt, form) => new Uint8Array(encode(pkt, form));
  const LOBBY = '591fc3fae3492a17c8efdbcf', BAR = '5a00000000000000000000b1', MUSIC = '5a00000000000000000000c2';
  const msg = (id, room, name, text, extra = {}) => ({ _id: id, createdAt: new Date(1793100000123), senderId: extra.senderId || 'aaaaaaaaaaaaaaaaaaaaaaaa', senderName: name, senderType: extra.senderType || 'user', isIncognito: false, targetId: room, targetType: 'room', messageType: extra.messageType || 'text', content: text, deleted: false, format: { color: '#fff' }, shareId: null, reactions: {} });
  // round-trip of the decoder incl. all 3 timestamp forms
  for (const f of [4, 8, 12]) { const d = t.win.__epikMsgpack(B({ a: [1, -5, 300, 70000, -70000, 1.5, null, true, 'x'.repeat(40), new Date(1793100000000)], b: new Uint8Array([1, 2]) }, f));
    check(d.a[0] === 1 && d.a[1] === -5 && d.a[2] === 300 && d.a[3] === 70000 && d.a[4] === -70000 && d.a[5] === 1.5 && d.a[6] === null && d.a[7] === true && d.a[8].length === 40 && d.a[9] === 1793100000000 && d.b.length === 2, 'msgpack decode (ints, floats, nil, bool, str8, bin, timestamp ext ' + f + '-byte)'); }
  t.win.__epikTapBinary(B({ type: 0, nsp: '/', data: { token: 'SECRET.TOKEN.VALUE' } }), 'out');   // CONNECT: never inspected
  t.win.__epikTapBinary(B({ type: 2, nsp: '/', data: ['connectionAccepted', { serverVersion: '5', userData: { displayName: 'Meow', username: 'meowington', userId: 'bbbbbbbbbbbbbbbbbbbbbbbb', picture: 'https://x/p.png', favorites: [{ type: 'room', roomId: LOBBY, name: 'Lobby' }], chatCommandsBotId: 'cccccccccccccccccccccccc', mutedBotIds: [] } }] }));
  t.win.__epikTapBinary(B({ type: 2, nsp: '/', data: ['getSidebarData', {}], options: { compress: true }, id: 7 }), 'out');
  t.win.__epikTapBinary(B({ type: 3, nsp: '/', id: 7, data: [{ rooms: [{ _id: BAR, name: 'The_Bar_Alc_420', memberCount: 12 }, { _id: MUSIC, name: 'Music', type: 'room' }], history: [msg('6720000000000000000000a0', BAR, 'old', '!help')] }] }));
  t.win.__epikTapBinary(B({ type: 2, nsp: '/', data: ['message', msg('6720000000000000000000a1', BAR, 'alice', '!help')] }, 12));
  t.win.__epikTapBinary(B({ type: 2, nsp: '/', data: ['message', msg('6720000000000000000000a0', BAR, 'old', '!help')] }));   // ack history id → not new
  t.win.__epikTapBinary(B({ type: 2, nsp: '/', data: ['message', msg('6720000000000000000000a2', LOBBY, 'Meow', '!song', { senderId: 'bbbbbbbbbbbbbbbbbbbbbbbb' })] }));
  t.win.__epikTapBinary(B({ type: 2, nsp: '/', data: ['message', msg('6720000000000000000000a3', MUSIC, 'EpikBot', '!uptime', { senderType: 'bot' })] }));
  t.win.__epikTapBinary(B({ type: 2, nsp: '/', data: ['message', msg('6720000000000000000000a4', MUSIC, 'bob', '', { messageType: 'embed' })] }));
  t.win.__epikTapBinary(B({ type: 2, nsp: '/', data: ['userUpdate', { userId: 'x', rooms: [LOBBY], update: { userStatus: 'online' } }] }));
  t.win.__epikTapBinary(B({ type: 2, nsp: '/', data: ['getLiveViewers', { broadcastId: 'meowington-0123456789ab' }], options: { compress: true }, id: 9 }), 'out');
  t.win.__epikTapBinary(B({ type: 3, nsp: '/', id: 9, data: [{ status: 'success', viewers: [{ userId: 'u1', username: 'carol', picture: 'https://cdn/x.png', country: 'US' }, { _id: 'u2', displayName: 'Dan' }], broadcastId: 'meowington-0123456789ab', revision: 3, viewerIds: ['u1', 'u2'] }] }));
  const M = msgs(t), V = t.out.filter(m => m.type === 'epik-tap-viewers'), R = t.out.filter(m => m.type === 'epik-tap-rooms').flatMap(m => m.rooms);
  check(R.some(r => r.id === LOBBY && r.name === 'Lobby') && R.some(r => r.id === BAR && r.name === 'The_Bar_Alc_420') && R.some(r => r.id === MUSIC), 'rooms learned from connectionAccepted favorites + sidebar ack');
  const a = M.find(m => m.user === 'alice');
  check(a && a.room === 'The_Bar_Alc_420' && a.roomId === BAR && a.text === '!help' && a.time === new Date(1793100000123).toISOString(), 'chat message decoded: room name via targetId, timestamp ext → ISO');
  check(!M.some(m => m.user === 'old') && !M.some(m => m.user === 'bob'), 'ack history ids and embeds never fire');
  check(M.find(m => m.user === 'Meow').self === true && M.find(m => m.user === 'EpikBot').isBot === true, 'own (senderId = userData.userId) and bot messages flagged');
  check(V.length === 1 && V[0].names.join() === 'carol,Dan' && V[0].via === 'ack', 'live-stream viewers ack mapped (username / displayName)');
  const dg = JSON.stringify(t.win.__epikTapDiag());
  check(/"viewersRequest":"getLiveViewers"/.test(dg) && /getSidebarData/.test(dg) && /"The_Bar_Alc_420":1/.test(dg) && !/SECRET|carol|alice|!help/.test(dg), 'diagnostics: viewers request name, ack shapes, per-room counters; no token/text/names');
}
console.log(fails ? fails + ' FAILED' : 'TAP UNIT PASSED'); if (fails) process.exitCode = 1;
