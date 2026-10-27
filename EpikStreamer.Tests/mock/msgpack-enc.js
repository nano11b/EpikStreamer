// Minimal msgpack ENCODER for the v2.7.1 mocks (fake EpikChat socket.io-msgpack server + unit tests). Dates become the
// timestamp extension (type -1); `tsForm` picks the 4-, 8- or 12-byte form like real encoders do depending on the value.
function encode(v, tsForm = 8) {
  const out = [];
  const u8 = (...b) => out.push(Buffer.from(b));
  const be = (n, bytes) => { const b = Buffer.alloc(bytes); if (bytes === 8) b.writeBigUInt64BE(BigInt(n)); else b.writeUIntBE(n, 0, bytes); out.push(b); };
  const w = x => {
    if (x === null || x === undefined) return u8(0xc0);
    if (x === true) return u8(0xc3); if (x === false) return u8(0xc2);
    if (x instanceof Date) {
      const ms = x.getTime(), sec = Math.floor(ms / 1000), ns = (ms % 1000) * 1e6;
      if (tsForm === 4) { u8(0xd6, 0xff); return be(sec, 4); }
      if (tsForm === 8) { u8(0xd7, 0xff); const b = Buffer.alloc(8); b.writeBigUInt64BE((BigInt(ns) << 34n) | BigInt(sec)); return out.push(b); }
      u8(0xc7, 12, 0xff); const b = Buffer.alloc(12); b.writeUInt32BE(ns, 0); b.writeBigInt64BE(BigInt(sec), 4); return out.push(b);
    }
    if (typeof x === 'number') {
      if (Number.isInteger(x)) {
        if (x >= 0 && x < 128) return u8(x); if (x < 0 && x >= -32) return u8(x & 0xff);
        if (x >= 0 && x < 256) return u8(0xcc, x); if (x >= 0 && x < 65536) { u8(0xcd); return be(x, 2); }
        if (x >= 0 && x < 2 ** 32) { u8(0xce); return be(x, 4); }
        if (x < 0 && x >= -(2 ** 31)) { u8(0xd2); const b = Buffer.alloc(4); b.writeInt32BE(x); return out.push(b); }
        u8(0xd3); const b = Buffer.alloc(8); b.writeBigInt64BE(BigInt(x)); return out.push(b);
      }
      u8(0xcb); const b = Buffer.alloc(8); b.writeDoubleBE(x); return out.push(b);
    }
    if (typeof x === 'string') {
      const b = Buffer.from(x, 'utf8');
      if (b.length < 32) u8(0xa0 | b.length); else if (b.length < 256) u8(0xd9, b.length); else if (b.length < 65536) { u8(0xda); be(b.length, 2); } else { u8(0xdb); be(b.length, 4); }
      return out.push(b);
    }
    if (x instanceof Uint8Array) { if (x.length < 256) u8(0xc4, x.length); else { u8(0xc6); be(x.length, 4); } return out.push(Buffer.from(x)); }
    if (Array.isArray(x)) { if (x.length < 16) u8(0x90 | x.length); else if (x.length < 65536) { u8(0xdc); be(x.length, 2); } else { u8(0xdd); be(x.length, 4); } return x.forEach(w); }
    const ks = Object.keys(x).filter(k => x[k] !== undefined);
    if (ks.length < 16) u8(0x80 | ks.length); else { u8(0xde); be(ks.length, 2); }
    for (const k of ks) { w(k); w(x[k]); }
  };
  w(v);
  return Buffer.concat(out);
}
module.exports = { encode };
