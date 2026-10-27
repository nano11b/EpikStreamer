// EpikStreamer chat tap (v2.7.1). Injected at DOCUMENT START into EpikChat's normal /chat page, before the site's own code.
// PASSIVE and READ-ONLY: it wraps window.WebSocket / EventSource (and peeks at fetch/XHR responses) only to MIRROR incoming
// frames to the app. It never sends, modifies, delays or drops anything the page sends or receives, and never opens its own
// sockets. The one exception is the optional viewers fallback, which repeats (GET only, ≥15 s apart) a same-origin request
// the page itself already made for its viewer list.
// v2.7.1 — VERIFIED from a signed-in capture (2026-10-27): EpikChat uses ONE wss://ws.epikchat.com/chat socket, Engine.IO v4 +
//   socket.io with the MSGPACK parser: every socket.io packet is one BINARY frame {type, data:[event, payload], nsp, id?}
//   (0 CONNECT, 2 EVENT, 3 ACK); text frames are only engine.io open/ping/pong. Handled below in sioPacket(): "message"
//   (chat), "connectionAccepted" (self + favorite rooms), ACKs (room lists, live-stream viewers). Outgoing frames are decoded
//   (never modified) only to correlate ack ids with request event names; the CONNECT packet (it carries the auth token) is
//   never decoded or inspected.
// The older generic text parser stays as a fallback (unverified for EpikChat): it is deliberately tolerant (socket.io/engine.io, Phoenix, Centrifugo, {type,data}/{event,payload}/{t,d}, batches) and
//   finds chat messages / viewer events by SHAPE. CFG.map can override event names and field paths without a new build, and
//   __epikTapDiag() returns sanitized event names + field names (no values, no text, no tokens, no cookies).
(() => {
  if (window.__epikTapInstalled) return;
  window.__epikTapInstalled = true;
  const CFG = (window.__EPIK_TAP_CFG || {});
  const MAP = CFG.map || {};
  const post = m => { try { window.chrome.webview.postMessage(m); } catch { } try { if (window.__epikTapSink) window.__epikTapSink(m); } catch { } };
  const st = { sockets: 0, open: 0, frames: 0, binary: 0, parsed: 0, msgs: 0, viewerEv: 0, memberEv: 0, restViewers: 0, errors: 0, reconnects: 0, lastFrameAt: 0, startedAt: Date.now(), hosts: {} };
  const diag = new Map();   // "transport|event" → { n, kinds:{}, shape }
  const seenIds = new Set(), seenQ = [];
  const remember = id => { if (seenIds.has(id)) return false; seenIds.add(id); seenQ.push(id); if (seenQ.length > 3000) seenIds.delete(seenQ.shift()); return true; };

  // ---------- helpers ----------
  const isObj = v => v && typeof v === 'object' && !Array.isArray(v);
  const get = (o, path) => { if (!path) return undefined; let v = o; for (const k of String(path).split('.')) { if (v == null) return undefined; v = v[k]; } return v; };
  const first = (o, keys) => { for (const k of keys) { const v = k.includes('.') ? get(o, k) : o[k]; if (v !== undefined && v !== null && v !== '') return v; } return undefined; };
  const strOf = v => typeof v === 'string' ? v : (typeof v === 'number' ? String(v) : '');
  const nameOf = v => {
    if (typeof v === 'string') return v;
    if (isObj(v)) return strOf(first(v, ['username', 'userName', 'displayName', 'display_name', 'nick', 'nickname', 'name', 'handle', 'login']));
    return '';
  };
  const roomOf = o => {
    const r = first(o, MAP.room ? [MAP.room] : ['roomId', 'room_id', 'room', 'channelId', 'channel_id', 'channel', 'groupId', 'group_id', 'group', 'chatId', 'conversationId', 'conversation', 'target', 'to']);
    if (isObj(r)) return { id: strOf(first(r, ['id', '_id', 'slug', 'key'])), name: strOf(first(r, ['name', 'title', 'displayName', 'slug'])) };
    const rn = strOf(first(o, ['roomName', 'room_name', 'channelName', 'groupName']));
    return { id: strOf(r), name: rn };
  };
  // a shape summary for diagnostics: field names and value TYPES only (never values)
  const shape = (v, d) => {
    if (d <= 0) return typeof v;
    if (Array.isArray(v)) return v.length ? [shape(v[0], d - 1)] : [];
    if (isObj(v)) { const o = {}; let n = 0; for (const k of Object.keys(v)) { if (n++ >= 30) { o['…'] = Object.keys(v).length; break; } o[k.slice(0, 40)] = shape(v[k], d - 1); } return o; }
    return v === null ? 'null' : typeof v;
  };
  const note = (transport, ev, kind, payload) => {
    const key = transport + '|' + String(ev || '(none)').slice(0, 60);
    let e = diag.get(key);
    if (!e) { if (diag.size >= 200) return; e = { n: 0, kinds: {}, shape: null }; diag.set(key, e); }
    e.n++; e.kinds[kind] = (e.kinds[kind] || 0) + 1;
    if (!e.shape && payload !== undefined) e.shape = shape(payload, 4);
  };
  const rx = (s, def) => { try { return s ? new RegExp(s, 'i') : def; } catch { return def; } };
  const RX = {
    skip: rx(MAP.skipEvents, /typing|edit|delet|remov|reaction|react|read|seen|ack|pong|ping|heartbeat|presence_diff_ignore/),
    history: rx(MAP.historyEvents, /history|backlog|load|sync|init|snapshot|bootstrap|catch.?up|previous|older/),
    message: rx(MAP.messageEvents, /mess|chat|msg|post|say/),
    viewList: rx(MAP.viewerListEvents, /viewers|watchers|audience|spectators|viewer.?list|viewing.?list/),
    viewJoin: rx(MAP.viewerJoinEvents, /(view|watch|audience|spectat).*(join|add|enter|start|arriv|connect|new)|(join|add|enter|start|arriv|connect|new).*(view|watch|audience|spectat)/),
    viewLeave: rx(MAP.viewerLeaveEvents, /(view|watch|audience|spectat).*(leav|left|remov|exit|stop|part|disconnect|gone)|(leav|left|remov|exit|stop|part|disconnect).*(view|watch|audience|spectat)/),
    memJoin: rx(MAP.memberJoinEvents, /(user|member|presence).*(join|enter|add|online)|(join|enter).*(room|group|channel)/),
    memLeave: rx(MAP.memberLeaveEvents, /(user|member|presence).*(leav|left|exit|remov|offline|part)|(leav|left|part).*(room|group|channel)/),
  };
  const TEXT = MAP.text ? [MAP.text] : ['text', 'message', 'body', 'content', 'msg', 'html', 'markdown'];
  const USER = MAP.user ? [MAP.user] : ['user', 'author', 'sender', 'from', 'username', 'userName', 'nick', 'owner', 'member', 'profile'];
  const ID = MAP.id ? [MAP.id] : ['id', '_id', 'messageId', 'message_id', 'mid', 'uuid', 'key'];
  const TIME = MAP.time ? [MAP.time] : ['createdAt', 'created_at', 'time', 'timestamp', 'ts', 'sentAt', 'date', 'at'];

  // ---------- message extraction ----------
  const asMessage = o => {
    if (!isObj(o)) return null;
    let text = first(o, TEXT);
    if (isObj(text)) text = first(text, ['text', 'body', 'content', 'plain']);
    text = strOf(text);
    if (!text || text.length > 4000) return null;
    if (/<[a-z][^>]*>/i.test(text)) text = text.replace(/<[^>]+>/g, ' ');
    const u = first(o, USER); const user = nameOf(u) || strOf(first(o, ['username', 'authorName', 'senderName', 'nick']));
    const id = strOf(first(o, ID));
    if (!user || !id) return null;
    const room = roomOf(o);
    const t = first(o, TIME);
    const time = typeof t === 'number' ? new Date(t < 1e12 ? t * 1000 : t).toISOString() : strOf(t);
    const bot = !!(MAP.bot ? get(o, MAP.bot) : (o.isBot || o.bot === true || (isObj(u) && (u.isBot || u.bot === true || u.type === 'bot' || u.role === 'bot' || (Array.isArray(u.tags) && u.tags.some(x => /bot/i.test(nameOf(x) || x)))))));
    const sys = !!(o.system || o.isSystem || o.type === 'system' || o.kind === 'system' || o.variant);
    if (sys) return null;
    return { id, user: user.replace(/\s+/g, ' ').trim().slice(0, 60), text: text.replace(/\s+/g, ' ').trim().slice(0, 500), time, room, isBot: bot };
  };
  // find message-shaped objects in a payload (depth ≤ 3; arrays = batches)
  const findMessages = (p, d, out) => {
    if (d < 0 || out.length > 50 || p == null) return out;
    if (Array.isArray(p)) { for (const x of p.slice(0, 60)) findMessages(x, d - 1, out); return out; }
    if (!isObj(p)) return out;
    const m = asMessage(p); if (m) { if (!m.room.id && !m.room.name) m.room = roomOf(p); out.push(m); return out; }
    for (const k of ['message', 'msg', 'data', 'payload', 'item', 'chat', 'messages', 'items', 'result', 'pub', 'push', 'record', 'doc', 'd', 'p']) if (p[k] != null) {
      const before = out.length; findMessages(p[k], d - 1, out);
      for (let i = before; i < out.length; i++) if (!out[i].room.id && !out[i].room.name) out[i].room = roomOf(p);   // room on the envelope
    }
    return out;
  };
  const namesIn = p => {
    const arr = Array.isArray(p) ? p : (isObj(p) ? (first(p, ['viewers', 'watchers', 'users', 'members', 'audience', 'list', 'items', 'data']) || null) : null);
    if (!Array.isArray(arr)) return null;
    const out = []; for (const x of arr.slice(0, 2000)) { const n = nameOf(x) || nameOf(isObj(x) ? (x.user || x.profile || x.member) : null); if (n) out.push(n.slice(0, 40)); }
    return out;
  };
  const oneName = p => isObj(p) ? (nameOf(first(p, ['user', 'viewer', 'member', 'profile', 'who'])) || nameOf(p)) : strOf(p);

  // ---------- frame decoding: returns [{ event, payload, topic }] ----------
  const decode = raw => {
    if (typeof raw !== 'string') return [];
    let s = raw.trim(); if (!s) return [];
    let m;
    // socket.io v2-4 over engine.io: "42[...]", "42/ns,17[...]", "2"/"3" pings, "0{...}" open
    if ((m = /^(\d)(\d)?(?:\/[^,]*,)?(\d*)([\[{].*)$/s.exec(s)) && m[1] === '4') {
      let v; try { v = JSON.parse(m[4]); } catch { return []; }
      if (Array.isArray(v)) return [{ event: strOf(v[0]) || 'message', payload: v.length > 2 ? v.slice(1) : v[1] }];
      return [{ event: 'message', payload: v }];
    }
    if (/^\d+$/.test(s)) return [];                        // engine.io ping/pong/noop
    if (/^\d[\[{]/.test(s)) s = s.slice(1);                // engine.io "0{...}" open etc.
    if (s[0] !== '{' && s[0] !== '[') return [];
    let v; try { v = JSON.parse(s); } catch { return []; }
    return fromJson(v, 0);
  };
  const fromJson = (v, d) => {
    if (d > 2) return [];
    if (Array.isArray(v)) {
      if (v.length === 5 && typeof v[2] === 'string' && typeof v[3] === 'string') return [{ event: v[3], payload: v[4], topic: v[2] }];   // Phoenix
      if (v.length >= 2 && typeof v[0] === 'string' && !isObj(v[0])) return [{ event: v[0], payload: v.length > 2 ? v.slice(1) : v[1] }];   // ["event", payload]
      return v.slice(0, 50).flatMap(x => isObj(x) || Array.isArray(x) ? fromJson(x, d + 1) : []);                                      // batch
    }
    if (!isObj(v)) return [];
    if (isObj(v.push) && isObj(v.push.pub)) return [{ event: 'publication', payload: v.push.pub.data, topic: strOf(v.push.channel) }];   // Centrifugo
    if (isObj(v.result) && isObj(v.result.data) && v.result.channel) return [{ event: 'publication', payload: v.result.data.data, topic: strOf(v.result.channel) }];
    const ev = MAP.eventField ? get(v, MAP.eventField) : first(v, ['type', 'event', 'ev', 't', 'op', 'action', 'e', 'name', 'kind', 'cmd']);
    const pl = MAP.payloadField ? get(v, MAP.payloadField) : first(v, ['data', 'd', 'payload', 'p', 'body', 'message', 'msg', 'args']);
    return [{ event: strOf(ev) || '(none)', payload: pl !== undefined ? pl : v, topic: strOf(first(v, ['channel', 'topic', 'room', 'roomId'])) }];
  };

  // ---------- event handling ----------
  const handle = (transport, raw) => {
    st.frames++; st.lastFrameAt = Date.now();
    let evs; try { evs = decode(raw); } catch { st.errors++; return; }
    for (const e of evs) {
      st.parsed++;
      const ev = e.event || '(none)', p = e.payload;
      try {
        if (RX.skip.test(ev)) { note(transport, ev, 'skipped', p); continue; }
        // viewers of the live stream
        if (RX.viewLeave.test(ev)) { const n = oneName(p); if (n) { st.viewerEv++; rest.lastWsViewerAt = Date.now(); post({ type: 'epik-tap-viewers', op: 'leave', names: [n.slice(0, 40)], room: roomOf(isObj(p) ? p : {}).id }); } note(transport, ev, 'viewer-leave', p); continue; }
        if (RX.viewJoin.test(ev)) { const n = oneName(p); if (n) { st.viewerEv++; rest.lastWsViewerAt = Date.now(); post({ type: 'epik-tap-viewers', op: 'join', names: [n.slice(0, 40)], room: roomOf(isObj(p) ? p : {}).id }); } note(transport, ev, 'viewer-join', p); continue; }
        if (RX.viewList.test(ev)) { const ns = namesIn(p); if (ns) { st.viewerEv++; rest.lastWsViewerAt = Date.now(); post({ type: 'epik-tap-viewers', op: 'snapshot', names: ns, room: roomOf(isObj(p) ? p : {}).id }); } note(transport, ev, ns ? 'viewer-list' : 'viewer-?', p); continue; }
        // room membership (who is in the room) — informational
        if (RX.memLeave.test(ev) || RX.memJoin.test(ev)) {
          const n = oneName(p); const r = roomOf(isObj(p) ? p : {});
          if (n) { st.memberEv++; post({ type: 'epik-tap-members', op: RX.memLeave.test(ev) ? 'leave' : 'join', name: n.slice(0, 40), roomId: r.id, roomName: r.name }); }
          note(transport, ev, 'member', p); continue;
        }
        const msgs = findMessages(p, 3, []);
        if (!msgs.length) { note(transport, ev, 'other', p); continue; }
        const backlog = RX.history.test(ev) || msgs.length > 3;   // history pages / initial syncs are not new messages
        for (const m of msgs) {
          if (!remember(m.id)) continue;
          if (backlog) continue;
          if (!RX.message.test(ev) && ev !== '(none)' && ev !== 'publication' && ev !== 'message' && !MAP.anyEventIsMessage) { note(transport, ev, 'message-shaped(ignored)', p); continue; }
          st.msgs++;
          post({ type: 'epik-chat', source: 'tap', id: m.id, user: m.user, text: m.text, time: m.time, room: m.room.name || '', roomId: m.room.id || e.topic || '', isBot: m.isBot, self: false });
        }
        note(transport, ev, backlog ? 'history' : 'message', p);
      } catch { st.errors++; }
    }
  };
  const textOf = (d, cb) => {
    if (typeof d === 'string') return cb(d);
    st.binary++;
    try {
      if (d instanceof ArrayBuffer) return cb(new TextDecoder('utf-8', { fatal: true }).decode(d));
      if (typeof Blob !== 'undefined' && d instanceof Blob && d.size < 1e6) d.arrayBuffer().then(b => { try { cb(new TextDecoder('utf-8', { fatal: true }).decode(b)); } catch { } }, () => { });
    } catch { /* binary protocol (msgpack/protobuf): counted only */ }
  };
  const hostOf = u => { try { const x = new URL(u, location.href); return x.host + x.pathname.replace(/[0-9a-f]{8,}|\d{3,}/gi, ':id').slice(0, 60); } catch { return '?'; } };


  // ---------- v2.7.1: msgpack + socket.io (EpikChat's verified framing) ----------
  const mpDecode = u8 => {
    const dv = new DataView(u8.buffer, u8.byteOffset, u8.byteLength); let o = 0;
    const td = new TextDecoder('utf-8');
    const str = n => { const v = td.decode(u8.subarray(o, o + n)); o += n; return v; };
    const bin = n => { const v = u8.slice(o, o + n); o += n; return v; };
    const arr = n => { const a = new Array(n); for (let i = 0; i < n; i++) a[i] = rd(); return a; };
    const map = n => { const m = {}; for (let i = 0; i < n; i++) { const k = rd(); m[String(k)] = rd(); } return m; };
    const ext = (n) => { const t = dv.getInt8(o); o++; const at = o; o += n;
      if (t === -1) {   // timestamp → ms
        if (n === 4) return dv.getUint32(at) * 1000;
        if (n === 8) { const hi = dv.getUint32(at), lo = dv.getUint32(at + 4); const ns = Math.floor(hi / 4); const sec = (hi & 3) * 4294967296 + lo; return sec * 1000 + Math.floor(ns / 1e6); }
        if (n === 12) { const ns = dv.getUint32(at); const sec = Number(dv.getBigInt64(at + 4)); return sec * 1000 + Math.floor(ns / 1e6); }
      }
      return { ext: t, size: n }; };
    const rd = () => {
      if (o >= u8.length) throw new Error('eof');
      const b = u8[o++];
      if (b <= 0x7f) return b; if (b >= 0xe0) return b - 256;
      if ((b & 0xf0) === 0x80) return map(b & 15); if ((b & 0xf0) === 0x90) return arr(b & 15); if ((b & 0xe0) === 0xa0) return str(b & 31);
      let v;
      switch (b) {
        case 0xc0: return null; case 0xc2: return false; case 0xc3: return true;
        case 0xc4: v = u8[o]; o += 1; return bin(v); case 0xc5: v = dv.getUint16(o); o += 2; return bin(v); case 0xc6: v = dv.getUint32(o); o += 4; return bin(v);
        case 0xc7: v = u8[o]; o += 1; return ext(v); case 0xc8: v = dv.getUint16(o); o += 2; return ext(v); case 0xc9: v = dv.getUint32(o); o += 4; return ext(v);
        case 0xca: v = dv.getFloat32(o); o += 4; return v; case 0xcb: v = dv.getFloat64(o); o += 8; return v;
        case 0xcc: v = u8[o]; o += 1; return v; case 0xcd: v = dv.getUint16(o); o += 2; return v; case 0xce: v = dv.getUint32(o); o += 4; return v;
        case 0xcf: v = Number(dv.getBigUint64(o)); o += 8; return v;
        case 0xd0: v = dv.getInt8(o); o += 1; return v; case 0xd1: v = dv.getInt16(o); o += 2; return v; case 0xd2: v = dv.getInt32(o); o += 4; return v;
        case 0xd3: v = Number(dv.getBigInt64(o)); o += 8; return v;
        case 0xd4: return ext(1); case 0xd5: return ext(2); case 0xd6: return ext(4); case 0xd7: return ext(8); case 0xd8: return ext(16);
        case 0xd9: v = u8[o]; o += 1; return str(v); case 0xda: v = dv.getUint16(o); o += 2; return str(v); case 0xdb: v = dv.getUint32(o); o += 4; return str(v);
        case 0xdc: v = dv.getUint16(o); o += 2; return arr(v); case 0xdd: v = dv.getUint32(o); o += 4; return arr(v);
        case 0xde: v = dv.getUint16(o); o += 2; return map(v); case 0xdf: v = dv.getUint32(o); o += 4; return map(v);
      }
      throw new Error('bad byte');
    };
    return rd();
  };
  const sio = { acks: new Map(), ackQ: [], self: { id: '', name: '', display: '' }, botIds: new Set(), rooms: new Map(), out: {}, ackShapes: new Map(),
    viewersRequest: '', viewersKey: '', perRoom: {}, packets: 0, outPackets: 0, decodeErrors: 0, viewerAcks: 0, roomsPosted: 0 };
  const HEX24 = /^[0-9a-f]{24}$/i;
  const roomName = id => sio.rooms.get(id) || '';
  const mask = id => typeof id === 'string' && id.length > 8 ? id.slice(0, 4) + '…' + id.slice(-3) : '?';
  // walk any payload for room records: {roomId|_id, name, type:"room"} or {_id, name} inside an array under a "...rooms" key
  const learnRooms = (v) => {
    const found = []; let n = 0;
    const walk = (x, d, key) => {
      if (d > 9 || ++n > 30000 || x == null || typeof x !== 'object') return;
      if (Array.isArray(x)) { for (const y of x.slice(0, 3000)) walk(y, d + 1, key); return; }
      const name = typeof x.name === 'string' ? x.name : (typeof x.roomName === 'string' ? x.roomName : '');
      const id = typeof x.roomId === 'string' ? x.roomId : (typeof x._id === 'string' ? x._id : '');
      const isRoom = x.type === 'room' || x.targetType === 'room' || typeof x.roomId === 'string' || /rooms?$/i.test(key || '');
      if (name && id && HEX24.test(id) && isRoom && name.length <= 100) { if (sio.rooms.get(id) !== name) { sio.rooms.set(id, name); found.push({ id, name }); } }
      for (const k of Object.keys(x).slice(0, 200)) { const y = x[k]; if (y && typeof y === 'object') walk(y, d + 1, k); }
    };
    try { walk(v, 0, ''); } catch { }
    if (found.length && sio.rooms.size < 5000) { sio.roomsPosted += found.length; post({ type: 'epik-tap-rooms', rooms: found.slice(0, 2000) }); }
  };
  const lastViewers = { key: '' };
  const viewerOf = v => isObj(v) ? { name: strOf(first(v, ['username', 'userName', 'displayName', 'name', 'nick'])) || nameOf(v.user), id: strOf(first(v, ['userId', '_id', 'id'])), picture: strOf(first(v, ['picture', 'avatar', 'avatarUrl', 'image'])), country: strOf(first(v, ['country', 'countryCode'])), since: first(v, ['joinedAt', 'since', 'startedAt', 'watchingSince', 'createdAt']) } : (typeof v === 'string' ? { name: v } : null);
  const isViewersResult = r => isObj(r) && Array.isArray(r.viewers) && typeof r.broadcastId === 'string';
  const onViewers = (r, via) => {
    const vs = r.viewers.slice(0, 2000).map(viewerOf).filter(x => x && x.name);
    const names = vs.map(x => x.name.slice(0, 40));
    sio.viewerAcks++; st.viewerEv++; rest.lastWsViewerAt = Date.now();
    const key = names.slice().sort().join('\n');
    if (key === lastViewers.key && sio.viewerAcks > 1) return;   // unchanged poll
    lastViewers.key = key;
    post({ type: 'epik-tap-viewers', op: 'snapshot', names, via, details: vs.slice(0, 500).map(x => ({ name: x.name.slice(0, 40), picture: /^https:\/\//.test(x.picture) ? x.picture.slice(0, 300) : '', country: x.country.slice(0, 8) })) });
  };
  const onChat = (p, live) => {
    if (!isObj(p) || typeof p._id !== 'string') return 'message-?';
    if (!remember(p._id) || !live) return live ? 'dup' : 'history';
    if (p.deleted) return 'deleted';
    if (p.targetType && p.targetType !== 'room') return 'not-room';
    if (p.messageType && p.messageType !== 'text') return 'embed';
    const text = typeof p.content === 'string' ? p.content : ''; if (!text) return 'empty';
    const rid = strOf(p.targetId); const rn = roomName(rid);
    const k = rn || mask(rid); sio.perRoom[k] = (sio.perRoom[k] || 0) + 1;
    const isBot = p.senderType === 'bot' || sio.botIds.has(p.senderId);
    const self = !!sio.self.id && p.senderId === sio.self.id;
    st.msgs++;
    const t = typeof p.createdAt === 'number' ? new Date(p.createdAt).toISOString() : strOf(p.createdAt);
    post({ type: 'epik-chat', source: 'tap', id: p._id, user: strOf(p.senderName).slice(0, 60), text: text.slice(0, 2000), time: t, room: rn, roomId: rid, isBot, self });
    return isBot ? 'message(bot)' : (self ? 'message(self)' : 'message');
  };
  const sioPacket = (pkt, dir) => {
    if (!isObj(pkt) || typeof pkt.type !== 'number') return;
    if (pkt.type === 0 || pkt.type === 1) return;                 // CONNECT/DISCONNECT: never inspected (auth token lives here)
    const id = typeof pkt.id === 'number' ? pkt.id : null;
    if (dir === 'out') {
      sio.outPackets++;
      if (pkt.type === 2 && Array.isArray(pkt.data)) {
        const name = strOf(pkt.data[0]).slice(0, 60) || '(none)';
        sio.out[name] = (sio.out[name] || 0) + 1;
        if (id != null) { sio.acks.set(id, name); sio.ackQ.push(id); if (sio.ackQ.length > 500) sio.acks.delete(sio.ackQ.shift()); }
      }
      return;
    }
    sio.packets++;
    if (pkt.type === 2 && Array.isArray(pkt.data)) {
      const ev = strOf(pkt.data[0]).slice(0, 60) || '(none)'; const p = pkt.data[1];
      let kind = 'other';
      if (ev === 'message') kind = onChat(p, true);
      else if (ev === 'connectionAccepted') {
        const u = isObj(p) && isObj(p.userData) ? p.userData : {};
        sio.self = { id: strOf(u.userId), name: strOf(u.username), display: strOf(u.displayName) };
        for (const b of [u.chatCommandsBotId, ...(Array.isArray(u.mutedBotIds) ? u.mutedBotIds : [])]) if (typeof b === 'string') sio.botIds.add(b);
        post({ type: 'epik-tap-self', name: sio.self.name.slice(0, 60), display: sio.self.display.slice(0, 60) });
        learnRooms(u.favorites ? { favorites: u.favorites } : p); kind = 'session';
      }
      else if (ev === 'userUpdate') kind = 'presence';
      else if (isViewersResult(p)) { onViewers(p, 'event'); kind = 'viewers'; }
      else learnRooms(p);
      note('sio', ev, kind, p);
      return;
    }
    if (pkt.type === 3) {
      const req = (id != null && sio.acks.get(id)) || '(unknown request)';
      if (id != null) sio.acks.delete(id);
      const r = Array.isArray(pkt.data) ? pkt.data[0] : pkt.data;
      let kind = 'ack';
      if (isViewersResult(r)) { sio.viewersRequest = req; onViewers(r, 'ack'); kind = 'viewers'; }
      else {
        learnRooms(r);
        // history in acks: remember message ids so a later re-send never fires
        let n = 0; const walk = (x, d) => { if (d > 6 || ++n > 20000 || !x || typeof x !== 'object') return; if (Array.isArray(x)) { x.slice(0, 2000).forEach(y => walk(y, d + 1)); return; }
          if (typeof x._id === 'string' && typeof x.content === 'string' && x.targetId) remember(x._id); for (const k in x) if (x[k] && typeof x[k] === 'object') walk(x[k], d + 1); };
        walk(r, 0);
      }
      if (!sio.ackShapes.has(req) && sio.ackShapes.size < 60) sio.ackShapes.set(req, shape(r, 3));
      note('ack', req, kind, undefined);
    }
  };
  const handleBinary = (u8, dir) => {
    if (dir === 'in') { st.frames++; st.lastFrameAt = Date.now(); }
    let pkt; try { pkt = mpDecode(u8); } catch { sio.decodeErrors++; return; }
    try { sioPacket(pkt, dir); } catch { st.errors++; }
  };
  const toU8 = (d, cb) => {
    try {
      if (d instanceof ArrayBuffer) return cb(new Uint8Array(d));
      if (ArrayBuffer.isView(d)) return cb(new Uint8Array(d.buffer, d.byteOffset, d.byteLength));
      if (typeof Blob !== 'undefined' && d instanceof Blob && d.size < 8e6) return d.arrayBuffer().then(b => cb(new Uint8Array(b)), () => { });
    } catch { }
  };
  // ---------- WebSocket wrapper (transparent subclass) ----------
  const NativeWS = window.WebSocket;
  if (NativeWS && !NativeWS.__epikWrapped) {
    class TapWS extends NativeWS {
      constructor(url, protocols) {
        super(url, protocols);
        st.sockets++; const h = hostOf(url); st.hosts[h] = (st.hosts[h] || 0) + 1;
        if (st.sockets > 1) st.reconnects++;
        try {
          super.addEventListener('open', () => { st.open++; post({ type: 'epik-tap-status', state: 'open', sockets: st.sockets }); });
          super.addEventListener('close', () => { st.open = Math.max(0, st.open - 1); post({ type: 'epik-tap-status', state: 'closed', sockets: st.sockets }); });
          let chain = Promise.resolve();   // keeps Blob frames in order
          super.addEventListener('message', ev => { try {
            const d = ev.data;
            if (typeof d === 'string') { if (!/^[0-9]$/.test(d)) handle('ws', d); else { st.frames++; st.lastFrameAt = Date.now(); } return; }
            st.binary++;
            if (typeof Blob !== 'undefined' && d instanceof Blob) chain = chain.then(() => toU8(d, u => handleBinary(u, 'in'))).catch(() => { });
            else toU8(d, u => handleBinary(u, 'in'));
          } catch { st.errors++; } });
        } catch { }
      }
      send(data) {   // passive: decode a copy for ack correlation, then send the ORIGINAL data unchanged
        try { if (typeof data !== 'string' && !(typeof Blob !== 'undefined' && data instanceof Blob)) toU8(data, u => handleBinary(u, 'out')); } catch { }
        return super.send(data);
      }
    }
    TapWS.__epikWrapped = true;
    try { window.WebSocket = TapWS; } catch { }
  }
  // ---------- EventSource wrapper ----------
  const NativeES = window.EventSource;
  if (NativeES && !NativeES.__epikWrapped) {
    class TapES extends NativeES {
      constructor(url, init) {
        super(url, init); st.sockets++; const h = hostOf(url); st.hosts['sse:' + h] = (st.hosts['sse:' + h] || 0) + 1;
        try { super.addEventListener('message', ev => { try { handle('sse', ev.data); } catch { } }); } catch { }
      }
      addEventListener(type, fn, opt) {   // named SSE events: mirror them too
        if (type !== 'message' && type !== 'open' && type !== 'error') { try { super.addEventListener(type, ev => { try { handle('sse', JSON.stringify({ type, data: (() => { try { return JSON.parse(ev.data); } catch { return ev.data; } })() })); } catch { } }); } catch { } }
        return super.addEventListener(type, fn, opt);
      }
    }
    TapES.__epikWrapped = true;
    try { window.EventSource = TapES; } catch { }
  }

  // ---------- fetch / XHR: peek at viewer-list responses (read-only clones) ----------
  const VIEW_URL = rx(MAP.viewersUrl, /view(er|ing)|watch(er|ing)|audience|spectat/);
  const rest = { url: '', init: null, at: 0, polls: 0, lastWsViewerAt: 0 };
  const sameOrigin = u => { try { return new URL(u, location.href).origin === location.origin; } catch { return false; } };
  const onRestJson = (url, j) => {
    const ns = namesIn(j) || namesIn(isObj(j) ? (j.data || j.result) : null);
    if (!ns) return false;
    st.restViewers++; note('rest', hostOf(url), 'viewer-list', j);
    post({ type: 'epik-tap-viewers', op: 'snapshot', names: ns, room: '', via: 'rest' });
    return true;
  };
  const nativeFetch = window.fetch;
  if (nativeFetch && !nativeFetch.__epikWrapped) {
    const f = function (input, init) {
      const p = nativeFetch.apply(this, arguments);
      try {
        const url = typeof input === 'string' ? input : (input && input.url) || '';
        const method = String((init && init.method) || (input && input.method) || 'GET').toUpperCase();
        if (method === 'GET' && VIEW_URL.test(url) && !(init && init.__epikPoll)) {
          p.then(r => { try { if (r.ok && /json/i.test(r.headers.get('content-type') || '')) r.clone().json().then(j => { if (onRestJson(url, j) && sameOrigin(url)) { rest.url = url; rest.init = init ? Object.assign({}, init) : null; } }, () => { }); } catch { } }, () => { });
        }
      } catch { }
      return p;
    };
    f.__epikWrapped = true;
    try { window.fetch = f; } catch { }
  }
  const XO = XMLHttpRequest.prototype.open, XS = XMLHttpRequest.prototype.send;
  if (!XO.__epikWrapped) {
    XMLHttpRequest.prototype.open = function (m, u) { try { this.__epikReq = { m: String(m).toUpperCase(), u: String(u) }; } catch { } return XO.apply(this, arguments); };
    XMLHttpRequest.prototype.open.__epikWrapped = true;
    XMLHttpRequest.prototype.send = function () {
      try {
        const q = this.__epikReq;
        if (q && q.m === 'GET' && VIEW_URL.test(q.u)) this.addEventListener('load', () => { try { if (this.status >= 200 && this.status < 300 && (this.responseType === '' || this.responseType === 'text' || this.responseType === 'json')) { const j = this.responseType === 'json' ? this.response : JSON.parse(this.responseText); if (onRestJson(q.u, j) && sameOrigin(q.u) && !rest.url) rest.url = q.u; } } catch { } });
      } catch { }
      return XS.apply(this, arguments);
    };
  }
  // optional fallback: repeat the page's own viewer-list GET while no live viewer events arrive (≥15 s, read-only)
  const pollEvery = Math.max(15, +CFG.restPollSeconds || 20) * 1000;
  if (CFG.restPoll) setInterval(() => {
    if (!rest.url || !nativeFetch || document.visibilityState === 'hidden' && !CFG.pollHidden) return;
    if (Date.now() - rest.lastWsViewerAt < 60000) return;
    rest.polls++;
    const init = Object.assign({}, rest.init || {}, { method: 'GET', credentials: (rest.init && rest.init.credentials) || 'same-origin', __epikPoll: true });
    nativeFetch.call(window, rest.url, init).then(r => r.ok ? r.json() : null).then(j => { if (j) onRestJson(rest.url, j); }, () => { });
  }, pollEvery);
  const _post = post;

  // ---------- diagnostics (sanitized) ----------
  window.__epikTapDiag = () => ({
    verified: true,
    protocol: 'engine.io v4 + socket.io msgpack (binary frames); verified from a signed-in capture 2026-10-27',
    note: 'Event names, field names and value types only; no message text, names, tokens or cookies. The CONNECT packet is never inspected.',
    sio: { packetsIn: sio.packets, packetsOut: sio.outPackets, decodeErrors: sio.decodeErrors, selfKnown: !!sio.self.id, roomsKnown: sio.rooms.size, botIds: sio.botIds.size,
      viewersRequest: sio.viewersRequest, viewerAcks: sio.viewerAcks, outgoingEvents: sio.out, messagesPerRoom: sio.perRoom, pendingAcks: sio.acks.size,
      ackShapes: Object.fromEntries(sio.ackShapes) },
    installedAt: new Date(st.startedAt).toISOString(),
    stats: Object.assign({}, st, { hosts: st.hosts, lastFrameMsAgo: st.lastFrameAt ? Date.now() - st.lastFrameAt : null }),
    restFallback: { learned: !!rest.url, path: rest.url ? hostOf(rest.url) : '', polls: rest.polls, enabled: !!CFG.restPoll },
    mapOverride: Object.keys(MAP),
    events: [...diag.entries()].sort((a, b) => b[1].n - a[1].n).slice(0, 80).map(([k, v]) => ({ transport: k.split('|')[0], event: k.slice(k.indexOf('|') + 1), count: v.n, kinds: v.kinds, fields: v.shape })),
  });
  window.__epikTapStats = () => ({ sockets: st.sockets, open: st.open, frames: st.frames, msgs: st.msgs, viewerEv: st.viewerEv, lastFrameMsAgo: st.lastFrameAt ? Date.now() - st.lastFrameAt : null });
  // test hook for the node unit test
  window.__epikTapDecode = raw => decode(raw);
  window.__epikTapHandle = raw => handle('test', raw);
  window.__epikTapBinary = (u8, dir) => handleBinary(u8, dir || 'in');
  window.__epikMsgpack = u8 => mpDecode(u8);
  setInterval(() => { try { _post({ type: 'epik-tap-stats', stats: window.__epikTapStats() }); } catch { } }, 10000);
})();
