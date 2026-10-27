// Fake EpikChat realtime server for the v2.7 tap tests (localhost only). Minimal RFC 6455, socket.io-v4-style framing:
// "0{...}" open, client "40" → "40{sid}", events "42[event, payload]". The protocol is INFERRED (EpikChat's real one is unverified).
// The page drives the timeline by sending "42[\"step\",N]" so it works under any timer speed. Prints "PORT n" when listening.
const http = require('http'), crypto = require('crypto');
let conn = 0;
const LOBBY = { id: 'r1', name: 'Lobby' }, ROOMB = { id: 'r2', name: 'Room B' };
const id = n => (0x65b000000000000000000000n + BigInt(n)).toString(16).padStart(24, '0');
const msg = (n, room, user, text, extra = {}) => ({ id: id(n), room, user: { id: 'u' + user, username: user }, content: text, createdAt: new Date().toISOString(), ...extra });
const ev = (e, p) => '42' + JSON.stringify([e, p]);
function steps(c, n) {
  // first connection: backlog, viewers, live traffic in BOTH rooms; then the server drops the socket
  if (c === 1) return ({
    0: [ev('room:history', { room: LOBBY, messages: [1, 2, 3, 4, 5].map(i => msg(i, LOBBY, 'old' + i, i === 3 ? '!song' : 'backlog ' + i)) }),
        ev('room:history', { room: ROOMB, messages: [6, 7].map(i => msg(i, ROOMB, 'oldb', '!uptime')) }),
        ev('typing', { room: LOBBY, user: { username: 'alice' } }),
        ev('viewers:list', { roomId: 'r1', viewers: [{ username: 'bob' }, { username: 'carol' }] })],
    1: [ev('message:new', msg(10, LOBBY, 'alice', '!song')),
        ev('message:new', msg(11, ROOMB, 'roomb', '!uptime')),          // other room: shown in the UI, must NOT fire
        ev('message:new', msg(12, LOBBY, 'EpikBot', '!uptime', { isBot: true })),
        ev('message:new', msg(13, LOBBY, 'MeStreamer', '!song'))],      // self
    2: [ev('viewer:joined', { roomId: 'r1', user: { username: 'zed' } })],
    3: [ev('viewer:left', { roomId: 'r1', user: { username: 'bob' } }), 'CLOSE'],
  })[n] || [];
  return ({   // reconnect: the server replays history (+ the alice message again) — all must be deduped
    0: [ev('room:history', { room: LOBBY, messages: [1, 2, 3, 4, 5, 10].map(i => msg(i, LOBBY, i === 10 ? 'alice' : 'old' + i, i === 10 ? '!song' : i === 3 ? '!song' : 'backlog ' + i)) }),
        ev('viewers:list', { roomId: 'r1', viewers: [{ username: 'carol' }, { username: 'zed' }] })],
    1: [ev('message:new', msg(10, LOBBY, 'alice', '!song')),            // duplicate resend
        ev('message:new', msg(20, LOBBY, 'dave', '!uptime'))],
    2: [ev('message:new', msg(21, LOBBY, 'erin', '!song'))],            // sent after the UI switched to Lobby: DOM + tap both see it
    3: [ev('viewer:joined', { roomId: 'r1', user: { username: 'yan' } }),
        ev('message:new', msg(22, LOBBY, 'frank', '!help'))],          // after the DOM observer re-attached: seen by both sources
  })[n] || [];
}
const frame = s => { const b = Buffer.from(s); const h = b.length < 126 ? Buffer.from([0x81, b.length]) : Buffer.from([0x81, 126, b.length >> 8, b.length & 255]); return Buffer.concat([h, b]); };
const srv = http.createServer((q, r) => { r.writeHead(404); r.end(); });
srv.on('upgrade', (req, sock) => {
  const c = ++conn;
  const acc = crypto.createHash('sha1').update(req.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  sock.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + acc + '\r\n\r\n');
  sock.write(frame('0' + JSON.stringify({ sid: 's' + c, pingInterval: 25000 })));
  let buf = Buffer.alloc(0);
  sock.on('data', d => {
    buf = Buffer.concat([buf, d]);
    while (buf.length >= 6) {
      let len = buf[1] & 127, off = 2; if (len === 126) { len = buf.readUInt16BE(2); off = 4; }
      if (buf.length < off + 4 + len) break;
      const op = buf[0] & 15, mask = buf.slice(off, off + 4), p = Buffer.from(buf.slice(off + 4, off + 4 + len).map((x, i) => x ^ mask[i % 4]));
      buf = buf.slice(off + 4 + len);
      if (op === 8) { sock.end(); return; }
      const t = p.toString();
      if (t === '40') { sock.write(frame('40' + JSON.stringify({ sid: 'n' + c }))); continue; }
      const m = /^42\["step",(\d+)\]$/.exec(t); if (!m) continue;
      for (const out of steps(c, +m[1])) {
        if (out === 'CLOSE') { setTimeout(() => sock.end(Buffer.from([0x88, 0])), 50); break; }
        sock.write(frame(out));
      }
    }
  });
  sock.on('error', () => { });
});
srv.listen(+(process.argv[2] || 0), '127.0.0.1', () => console.log('PORT ' + srv.address().port));
setTimeout(() => process.exit(0), +(process.argv[3] || 120) * 1000);
