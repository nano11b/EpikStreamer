// Fake EpikChat realtime server for the v2.7.1 tap tests (localhost only). Speaks the VERIFIED framing captured from the
// signed-in site: Engine.IO v4 + socket.io with the msgpack parser — text frames only for "0{sid…}" open and "2"/"3"
// ping/pong; every socket.io packet is ONE binary msgpack frame {type, nsp:"/", data, id?} (0 CONNECT, 2 EVENT, 3 ACK).
// Shapes mirror the capture: connectionAccepted.userData.favorites, a getSidebarData ack with the other rooms, "message"
// events with createdAt as a msgpack timestamp ext, userUpdate presence, and the periodic live-viewers ACK
// {status:"success", viewers, broadcastId:"meowington-<12hex>", revision, viewerIds}. All tokens/keys here are FAKE.
// The page drives the timeline with an EVENT "step" (no id) so it works under any timer speed. Prints "PORT n".
const http = require('http'), crypto = require('crypto'), { encode, decode } = require('./msgpack-mini');
let conn = 0;
const LOBBY = '591fc3fae3492a17c8efdbcf', BAR = '5b10000000000000000000b1', MUSIC = '5b10000000000000000000c2', ROOMB = '5b10000000000000000000d3';
const SELF = '6a0000000000000000000001', BOT = '6a00000000000000000000b0';
const oid = n => (0x67200000000000000000000n + BigInt(n)).toString(16).padStart(24, '0');
const uid = s => crypto.createHash('md5').update(s).digest('hex').slice(0, 24);
const msg = (n, room, name, text, x = {}) => ({ _id: oid(n), createdAt: new Date(Date.now() - 1000), senderId: x.senderId || uid(name), senderName: name, senderType: x.senderType || 'user',
  isIncognito: false, targetId: room, targetType: 'room', messageType: x.messageType || 'text', content: text, deleted: false, format: { bold: false }, shareId: null, reactions: {},
  ...(x.messageType === 'embed' ? { embed: { url: 'https://example.invalid/x', title: 'clip' } } : {}) });
const viewer = name => ({ userId: uid(name), username: name, displayName: name[0].toUpperCase() + name.slice(1), picture: 'https://cdn.example.invalid/' + name + '.png', country: 'US', joinedAt: new Date() });
const VIEWERS = { 1: [['bob', 'carol'], ['bob', 'carol', 'zed'], ['carol', 'zed']], 2: [['carol', 'zed', 'yan'], ['carol', 'zed', 'yan']] };
const ev = (e, p) => ({ type: 2, nsp: '/', data: [e, p] });
const accepted = () => ev('connectionAccepted', { serverVersion: '3.14.0', userData: { displayName: 'MeStreamer', username: 'meowington', userId: SELF, picture: 'https://cdn.example.invalid/me.png',
  favorites: [{ type: 'room', roomId: LOBBY, name: 'Lobby', unread: 0 }], chatCommandsBotId: BOT, mutedBotIds: [] } });
const sidebar = c => ({ rooms: [{ _id: BAR, name: 'The_Bar_Alc_420', type: 'room', memberCount: 41 }, { _id: MUSIC, name: 'Music', type: 'room', memberCount: 9 }, { _id: ROOMB, name: 'Room B', type: 'room', memberCount: 3 }],
  recentMessages: c === 1 ? [msg(1, BAR, 'old1', '!help'), msg(2, LOBBY, 'old2', '!song')] : [msg(1, BAR, 'old1', '!help'), msg(2, LOBBY, 'old2', '!song'), msg(10, BAR, 'alice', '!help')] });
function steps(c, n) {
  if (c === 1) return ({
    0: [ev('message', msg(10, BAR, 'alice', '!help')),                                         // not the visible room
        ev('message', msg(11, LOBBY, 'MeStreamer', '!song', { senderId: SELF })),               // self
        ev('message', msg(12, MUSIC, 'EpikBot', '!uptime', { senderType: 'bot', senderId: BOT })),
        ev('message', msg(13, MUSIC, 'bob', '', { messageType: 'embed' })),
        ev('userUpdate', { userId: uid('carol'), rooms: [LOBBY, MUSIC], update: { userStatus: 'online' } })],
    1: [{ ts: 4, p: ev('message', msg(14, LOBBY, 'dave', '!uptime')) }, { ts: 12, p: ev('message', msg(15, MUSIC, 'carol', '!song')) }],
    2: ['PING'],
    3: ['CLOSE'],
  })[n] || [];
  return ({   // reconnect: the sidebar ack replays history incl. alice's message, and the server resends it — all deduped
    0: [ev('message', msg(10, BAR, 'alice', '!help')), ev('message', msg(16, ROOMB, 'erin', '!help'))],   // erin: the visible room (DOM + tap)
    1: [ev('message', msg(17, BAR, 'frank', '!help'))],
    2: ['PING'],
  })[n] || [];
}
const frame = (data, op) => { const b = Buffer.from(data); const l = b.length;
  const h = l < 126 ? Buffer.from([0x80 | op, l]) : l < 65536 ? Buffer.from([0x80 | op, 126, l >> 8, l & 255]) : Buffer.concat([Buffer.from([0x80 | op, 127]), (() => { const x = Buffer.alloc(8); x.writeBigUInt64BE(BigInt(l)); return x; })()]);
  return Buffer.concat([h, b]); };
const srv = http.createServer((q, r) => { r.writeHead(404); r.end(); });
srv.on('upgrade', (req, sock) => {
  const c = ++conn; const stats = { pongs: 0 };
  const acc = crypto.createHash('sha1').update(req.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  sock.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + acc + '\r\n\r\n');
  const text = s => sock.write(frame(s, 1)), bin = (p, ts) => sock.write(frame(encode(p, ts || 8), 2));
  text('0' + JSON.stringify({ sid: 'eio' + c, upgrades: [], pingInterval: 25000, pingTimeout: 20000, maxPayload: 1000000 }));
  let buf = Buffer.alloc(0), viewerPoll = 0;
  sock.on('data', d => {
    buf = Buffer.concat([buf, d]);
    while (buf.length >= 6) {
      let len = buf[1] & 127, off = 2; if (len === 126) { len = buf.readUInt16BE(2); off = 4; }
      if (buf.length < off + 4 + len) break;
      const op = buf[0] & 15, mask = buf.slice(off, off + 4), p = Buffer.from(buf.slice(off + 4, off + 4 + len).map((x, i) => x ^ mask[i % 4]));
      buf = buf.slice(off + 4 + len);
      if (op === 8) { sock.end(); return; }
      if (op === 1) { if (p.toString() === '3') stats.pongs++; continue; }        // only engine.io pong is text from the client
      if (op !== 2) continue;
      let pk; try { pk = decode(new Uint8Array(p)); } catch { continue; }
      if (pk.type === 0) { bin({ type: 0, nsp: '/', data: { sid: 'sio' + c } }); bin(accepted()); continue; }   // token in pk.data is ignored
      if (pk.type !== 2 || !Array.isArray(pk.data)) continue;
      const [name, arg] = pk.data;
      if (name === 'getSidebarData' && pk.id != null) bin({ type: 3, nsp: '/', id: pk.id, data: [sidebar(c)] });
      else if (name === 'resumeOpenApp' && pk.id != null) bin({ type: 3, nsp: '/', id: pk.id, data: [{ status: 'success', openRooms: [ROOMB] }] });
      else if (name === 'getBroadcastViewers' && pk.id != null) {
        const set = VIEWERS[c][Math.min(viewerPoll, VIEWERS[c].length - 1)]; viewerPoll++;
        bin({ type: 3, nsp: '/', id: pk.id, data: [{ status: 'success', viewers: set.map(viewer), broadcastId: 'meowington-0123456789ab', revision: 100 * c + viewerPoll, viewerIds: set.map(uid) }] });
      }
      else if (name === 'step') for (const out of steps(c, +arg)) {
        if (out === 'CLOSE') { setTimeout(() => sock.end(Buffer.from([0x88, 0])), 50); break; }
        if (out === 'PING') { text('2'); continue; }
        if (out.ts) bin(out.p, out.ts); else bin(out);
      }
    }
  });
  sock.on('error', () => { });
});
srv.listen(+(process.argv[2] || 0), '127.0.0.1', () => console.log('PORT ' + srv.address().port));
setTimeout(() => process.exit(0), +(process.argv[3] || 120) * 1000);
