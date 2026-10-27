// Tiny msgpack encode/decode for the v2.7.1 mocks only (fake EpikChat site code in the browser + the fake socket.io server in
// node + tap-unit.js). Dates ⇄ timestamp extension type -1; encode(v, tsForm) picks its 4/8/12-byte form. Not app code.
(function (root) {
  function encode(v, tsForm) {
    tsForm = tsForm || 8; const out = []; const te = new TextEncoder();
    const b = (...x) => out.push(...x);
    const n = (x, bytes) => { for (let i = bytes - 1; i >= 0; i--) out.push(Number((BigInt(x) >> BigInt(8 * i)) & 255n)); };
    const w = x => {
      if (x === null || x === undefined) return b(0xc0);
      if (x === true) return b(0xc3); if (x === false) return b(0xc2);
      if (x instanceof Date) {
        const ms = x.getTime(), sec = Math.floor(ms / 1000), ns = (ms - sec * 1000) * 1e6;
        if (tsForm === 4) { b(0xd6, 0xff); return n(sec, 4); }
        if (tsForm === 8) { b(0xd7, 0xff); return n((BigInt(ns) << 34n) | BigInt(sec), 8); }
        b(0xc7, 12, 0xff); n(ns, 4); return n(BigInt.asUintN(64, BigInt(sec)), 8);
      }
      if (typeof x === 'number') {
        if (Number.isInteger(x)) {
          if (x >= 0 && x < 128) return b(x); if (x < 0 && x >= -32) return b(x & 0xff);
          if (x >= 0 && x < 256) return b(0xcc, x); if (x >= 0 && x < 65536) { b(0xcd); return n(x, 2); }
          if (x >= 0 && x < 2 ** 32) { b(0xce); return n(x, 4); }
          if (x < 0 && x >= -(2 ** 31)) { b(0xd2); return n(BigInt.asUintN(32, BigInt(x)), 4); }
          b(0xd3); return n(BigInt.asUintN(64, BigInt(x)), 8);
        }
        const dv = new DataView(new ArrayBuffer(8)); dv.setFloat64(0, x); b(0xcb); return b(...new Uint8Array(dv.buffer));
      }
      if (typeof x === 'string') {
        const s = te.encode(x);
        if (s.length < 32) b(0xa0 | s.length); else if (s.length < 256) b(0xd9, s.length); else if (s.length < 65536) { b(0xda); n(s.length, 2); } else { b(0xdb); n(s.length, 4); }
        for (const c of s) out.push(c); return;
      }
      if (x instanceof Uint8Array) { if (x.length < 256) b(0xc4, x.length); else { b(0xc6); n(x.length, 4); } for (const c of x) out.push(c); return; }
      if (Array.isArray(x)) { if (x.length < 16) b(0x90 | x.length); else if (x.length < 65536) { b(0xdc); n(x.length, 2); } else { b(0xdd); n(x.length, 4); } return x.forEach(w); }
      const ks = Object.keys(x).filter(k => x[k] !== undefined);
      if (ks.length < 16) b(0x80 | ks.length); else { b(0xde); n(ks.length, 2); }
      for (const k of ks) { w(k); w(x[k]); }
    };
    w(v); return new Uint8Array(out);
  }
  function decode(u8) {
    const dv = new DataView(u8.buffer, u8.byteOffset, u8.byteLength); const td = new TextDecoder(); let p = 0;
    const str = l => { const s = td.decode(u8.subarray(p, p + l)); p += l; return s; };
    const arr = l => { const a = []; for (let i = 0; i < l; i++) a.push(r()); return a; };
    const map = l => { const o = {}; for (let i = 0; i < l; i++) { const k = r(); o[k] = r(); } return o; };
    const ext = (l, t) => { const s = p; p += l; if (t !== -1) return null;
      if (l === 4) return dv.getUint32(s) * 1000;
      if (l === 8) { const hi = dv.getUint32(s), lo = dv.getUint32(s + 4); return ((hi & 3) * 2 ** 32 + lo) * 1000 + Math.floor((hi >>> 2) / 1e6); }
      return Number(dv.getBigInt64(s + 4)) * 1000 + Math.floor(dv.getUint32(s) / 1e6); };
    const r = () => {
      const c = u8[p++];
      if (c < 0x80) return c; if (c >= 0xe0) return c - 256;
      if ((c & 0xf0) === 0x80) return map(c & 15); if ((c & 0xf0) === 0x90) return arr(c & 15); if ((c & 0xe0) === 0xa0) return str(c & 31);
      let v;
      switch (c) {
        case 0xc0: return null; case 0xc2: return false; case 0xc3: return true;
        case 0xc4: v = u8[p++]; p += v; return u8.slice(p - v, p);
        case 0xc6: v = dv.getUint32(p); p += 4 + v; return u8.slice(p - v, p);
        case 0xc7: v = u8[p]; return ext(v, dv.getInt8((p += 2) - 1));
        case 0xcb: v = dv.getFloat64(p); p += 8; return v;
        case 0xcc: return u8[p++]; case 0xcd: v = dv.getUint16(p); p += 2; return v; case 0xce: v = dv.getUint32(p); p += 4; return v;
        case 0xd2: v = dv.getInt32(p); p += 4; return v; case 0xd3: v = Number(dv.getBigInt64(p)); p += 8; return v;
        case 0xd6: p += 1; return ext(4, dv.getInt8(p - 1)); case 0xd7: p += 1; return ext(8, dv.getInt8(p - 1));
        case 0xd9: return str(u8[p++]); case 0xda: v = dv.getUint16(p); p += 2; return str(v); case 0xdb: v = dv.getUint32(p); p += 4; return str(v);
        case 0xdc: v = dv.getUint16(p); p += 2; return arr(v); case 0xdd: v = dv.getUint32(p); p += 4; return arr(v);
        case 0xde: v = dv.getUint16(p); p += 2; return map(v);
      }
      throw new Error('msgpack-mini: unsupported 0x' + c.toString(16));
    };
    return r();
  }
  const api = { encode, decode };
  if (typeof module !== 'undefined' && module.exports) module.exports = api; else root.MsgpackMini = api;
})(typeof self !== 'undefined' ? self : this);
