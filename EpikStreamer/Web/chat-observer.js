// EpikStreamer chat observer (v2.3.0). Injected into EpikChat's normal /chat page in the app's WebView2 panel.
// Only READS the page DOM (and, when the user enabled "Reply in chat", types into the normal composer).
// Matches EpikChat's real markup (verified 2026-10-25, Lobby, signed in):
//   list   div[role=log][aria-label="Chat messages"] (react-virtuoso, ~44 rows rendered, rows churn)
//   row    [data-chat-message-row=<24-hex id, chronological>]; system rows have [data-chat-system-variant]
//   layout [data-chat-message-layout] > div:first-child > button:first-child = author; time[datetime]; LAST div = text
//   compose input[data-chat-composer-input] inside a <form>, Enter submits, maxlength 250
(() => {
  if (window.__epikObs) return;
  const CFG = window.__EPIK_CFG || {};
  const S = Object.assign({
    log: 'div[role="log"][aria-label="Chat messages"]',
    row: '[data-chat-message-row]',
    layout: '[data-chat-message-layout]',
    system: '[data-chat-system-variant]',
    botTag: '[data-chat-sender-tags] [data-chat-tag="bot"], [data-chat-tag="bot"]',
    room: 'header [role="heading"][aria-level="1"]',
    composer: 'input[data-chat-composer-input][aria-label="Chat message"], input[data-chat-composer-input]',
    self: 'header button[aria-label^="Account"], header button[aria-label*="account" i], header [data-account-button], header button[aria-haspopup="menu"][aria-label]',
    members: '[role="list"][aria-label="Room members"]',
    // v2.3 viewer list. ⚠ NOT VERIFIED against the live site: EpikChat's LIVE panel "Viewers" list markup is a guess
    // (any list labelled/titled "Viewers"). Override via CFG.sel.viewers; diagnostics report what matched.
    viewers: '[role="list"][aria-label="Viewers" i], [role="list"][aria-label*="viewers" i], [aria-label="Viewers" i] [role="list"], [data-live-viewers] [role="list"]',
    memberRow: '[data-index], [role="listitem"]',
  }, CFG.sel || {});
  const post = m => { try { window.chrome.webview.postMessage(m); } catch { } if (window.__epikTap) window.__epikTap(m); };
  const status = text => post({ type: 'epik-chat-status', text });
  const shown = e => { if (!e || !e.isConnected) return false; if (e.closest('[aria-hidden="true"]')) return false; const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };

  // ---- parsing ----
  const textOf = div => {
    let out = '';
    const walk = n => {
      for (const c of n.childNodes) {
        if (c.nodeType === 3) { out += c.nodeValue; continue; }
        if (c.nodeType !== 1) continue;
        if (c.matches('[data-emoji-fallback], img, [role="toolbar"], button, svg, style, script')) continue;
        if (c.matches('[role="img"][aria-label], [data-emoji-copy-text]')) {
          const cp = c.matches('[data-emoji-copy-text]') ? c : c.querySelector('[data-emoji-copy-text]');
          out += (cp && (cp.getAttribute('data-emoji-copy-text') || cp.textContent)) || c.getAttribute('aria-label') || ''; continue;
        }
        if (c.tagName === 'BR') { out += '\n'; continue; }
        walk(c);
      }
    };
    walk(div); return out.replace(/\s+/g, ' ').trim();
  };
  const parse = row => {
    const id = row.getAttribute('data-chat-message-row') || '';
    if (row.matches(S.system) || row.querySelector(S.system)) return { id, skip: 'system' };
    const lay = row.querySelector(S.layout); if (!lay) return { id, skip: 'no-layout' };
    const head = lay.querySelector(':scope > div:first-child');
    const ab = head && head.querySelector(':scope > button:first-child');
    let user = ab ? ab.textContent.trim() : '';
    if (!user) { const av = row.querySelector('button[aria-label^="Open "]'); if (av) user = av.getAttribute('aria-label').slice(5).trim(); }
    const isBot = !!row.querySelector(S.botTag);
    const t = lay.querySelector('time[datetime]');
    const divs = lay.querySelectorAll(':scope > div');
    const tdiv = divs.length > 1 ? divs[divs.length - 1] : null;   // header is first; optional reply quote (div.mt-px) between
    const text = tdiv && !tdiv.matches('.mt-px') ? textOf(tdiv) : '';
    if (!text) return { id, skip: 'empty' };
    return { id, user, text, time: t ? t.getAttribute('datetime') : '', isBot };
  };

  // ---- state ----
  // Only real 24-hex Mongo-style ids are ordered; system rows (e.g. "welcome:lobby") are tracked in `seen` only.
  const HEX = /^[0-9a-f]{24}$/i;
  const bump = id => { if (HEX.test(id) && id.toLowerCase() > st.maxId) st.maxId = id.toLowerCase(); };
  const st = { pane: null, paneKey: '', room: '', seen: new Set(), maxId: '', rows: 0, processed: 0, skipped: 0, attachedAt: 0, self: '', sent: [], debug: !!CFG.debug, lastScan: 0 };
  let paneSeq = 0;
  const findLog = () => { const all = [...document.querySelectorAll(S.log)]; return all.find(shown) || null; };
  const roomName = () => { const hs = [...document.querySelectorAll(S.room)]; const h = hs.find(shown) || hs[0]; return h ? h.textContent.trim() : ''; };
  const selfName = () => {
    if (CFG.selfName) return CFG.selfName;
    for (const b of document.querySelectorAll(S.self)) { const v = (b.getAttribute('data-username') || b.textContent || '').trim(); if (v && v.length < 40) return v; }
    return '';
  };
  const isSelf = m => {
    const me = (st.self || '').toLowerCase();
    if (me && m.user.toLowerCase() === me) return true;
    const now = Date.now(); st.sent = st.sent.filter(s => now - s.at < 120000);
    return st.sent.some(s => s.text === m.text);   // our own replies even if the username is unknown
  };

  const scan = () => {
    st.lastScan = Date.now();
    const log = findLog();
    if (!log) { if (st.pane) { st.pane = null; status('chat list not visible'); } return; }
    const room = roomName();
    if (!log.__epikPane) log.__epikPane = ++paneSeq;
    const key = log.__epikPane + '|' + room;
    const rows = [...log.querySelectorAll(S.row)];
    st.rows = rows.length;
    if (key !== st.paneKey) {   // attach / room switch: everything already shown is backlog
      st.paneKey = key; st.pane = log; st.room = room; st.self = selfName(); st.attachedAt = Date.now();
      for (const r of rows) { const id = r.getAttribute('data-chat-message-row'); st.seen.add(id); bump(id); }
      status(`attached to ${room || 'chat'}, ${rows.length} rows` + (st.self ? `, you are ${st.self}` : ', username unknown'));
      return;
    }
    if (!st.self) st.self = selfName();
    const fresh = [];
    for (const r of rows) {
      const id = r.getAttribute('data-chat-message-row'); if (!id || st.seen.has(id)) continue;
      if (HEX.test(id) && id.toLowerCase() <= st.maxId) { st.seen.add(id); continue; }           // older row re-rendered by scrolling: not new
      fresh.push(r);
    }
    fresh.sort((a, b) => a.getAttribute('data-chat-message-row') < b.getAttribute('data-chat-message-row') ? -1 : 1);
    for (const r of fresh) {
      const m = parse(r);
      if (m.skip === 'empty' && !r.querySelector(S.system) && r.querySelector(S.layout) && Date.now() - (r.__epikFirst || (r.__epikFirst = Date.now())) < 1500) break; // text may still be rendering: retry next scan
      st.seen.add(m.id); bump(m.id);
      if (m.skip) { st.skipped++; continue; }
      const self = isSelf(m);
      st.processed++;
      post({ type: 'epik-chat', id: m.id, user: m.user.slice(0, 60), text: m.text.slice(0, 500), time: m.time, room: st.room, isBot: m.isBot, self });
    }
    if (st.seen.size > 5000) { const keep = [...st.seen].sort().slice(-2000); st.seen = new Set(keep); }
  };
  let timer = 0;
  const schedule = () => { if (!timer) timer = setTimeout(() => { timer = 0; try { scan(); } catch (e) { status('scan error: ' + e.message); } }, 200); };

  // ---- opt-in reply helpers ----
  const composer = () => { const all = [...document.querySelectorAll(S.composer)]; return all.find(shown) || all[0] || null; };
  window.__epikFocusComposer = () => { const c = composer(); if (!c || c.disabled) return 'no-input'; c.focus(); return document.activeElement === c ? 'ok' : 'no-focus'; };
  window.__epikNoteSent = txt => { st.sent.push({ text: String(txt).replace(/\s+/g, ' ').trim(), at: Date.now() }); return 'ok'; };
  window.__epikComposerValue = () => { const c = composer(); return c ? c.value : null; };
  // JS fallback (the app prefers DevTools Input.insertText + Enter): native value setter + input event + form submit.
  window.__epikSend = txt => {
    const c = composer(); if (!c || c.disabled) return 'no-input';
    txt = String(txt).slice(0, +(c.getAttribute('maxlength') || 250));
    window.__epikNoteSent(txt);
    c.focus();
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(c, txt);
    c.dispatchEvent(new Event('input', { bubbles: true }));
    const f = c.closest('form');
    if (f && f.requestSubmit) { f.requestSubmit(); return 'submitted'; }
    for (const type of ['keydown', 'keypress', 'keyup']) c.dispatchEvent(new KeyboardEvent(type, { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }));
    return 'enter';
  };

  // ---- diagnostics: structure only (no message text, no names) ----
  const skel = (e, depth, budget) => {
    if (!e || depth < 0 || budget.n-- <= 0) return null;
    const o = { tag: e.tagName.toLowerCase() };
    for (const a of e.attributes) {
      if (a.name === 'class' || a.name === 'style' || a.name === 'src' || a.name === 'href' || a.name === 'alt' || a.name === 'title') continue;
      if (a.name === 'aria-label') o[a.name] = /^(Chat messages|Room members|Chat message|Viewers)$/.test(a.value) ? a.value : '…' + a.value.length;
      else o[a.name] = /^(role|aria-level|aria-hidden|type|maxlength|data-index|data-chat-tag|data-chat-system-variant)$/.test(a.name) ? a.value.slice(0, 30) : (a.value ? '…' : '');
    }
    const txt = [...e.childNodes].filter(n => n.nodeType === 3).map(n => n.nodeValue).join('').trim();
    if (txt) o.text = '#' + txt.length;
    const kids = [...e.children].map(c => skel(c, depth - 1, budget)).filter(Boolean);
    if (kids.length) o.children = kids;
    return o;
  };
  window.__epikDiag = () => {
    const log = findLog();
    const count = s => { try { return document.querySelectorAll(s).length; } catch { return 'bad selector'; } };
    const sels = Object.assign({}, S, CFG.extraDiag || {});
    const counts = {}; for (const k in sels) counts[k] = count(sels[k]);
    const rows = log ? [...log.querySelectorAll(S.row)] : [];
    return JSON.stringify({
      url: location.pathname, time: new Date().toISOString(), observer: { attached: !!st.pane, room: st.room ? '…' + st.room.length : '', rows: st.rows, seen: st.seen.size, maxId: st.maxId, processed: st.processed, skipped: st.skipped, selfKnown: !!st.self, lastScanMsAgo: Date.now() - st.lastScan },
      selectorCounts: counts, logsTotal: count(S.log), logsVisible: [...document.querySelectorAll(S.log)].filter(shown).length,
      lastRows: rows.slice(-3).map(r => skel(r, 9, { n: 200 })),
      parsedLast: rows.slice(-5).map(r => { const m = parse(r); return { id: m.id, skip: m.skip || '', userLen: (m.user || '').length, textLen: (m.text || '').length, isBot: !!m.isBot, hasTime: !!m.time }; }),
      composer: (() => { const c = composer(); return c ? { found: true, inForm: !!c.closest('form'), maxlength: c.getAttribute('maxlength') } : { found: false }; })(),
      members: skel(document.querySelector(S.members), 5, { n: 60 }),
      viewers: CFG.viewerDiag ? CFG.viewerDiag() : null,
    }, null, 1);
  };

  // ---- v2.3: viewer / member names (read-only) ----
  // Names come from each row's first button (EpikChat rows open a profile from it) or its first text line.
  const rowName = r => {
    const b = r.querySelector('button'); let n = '';
    if (b) { n = (b.textContent || '').trim(); const al = b.getAttribute('aria-label') || ''; if (!n && /^Open /.test(al)) n = al.slice(5); if (!n && /'s profile$/.test(al)) n = al.slice(0, -10); }
    if (!n) n = ((r.innerText || r.textContent || '').trim().split('\n')[0] || '');
    return n.replace(/\s+/g, ' ').trim().slice(0, 40);
  };
  const viewersList = () => {
    let el = null; try { el = [...document.querySelectorAll(S.viewers)].find(shown) || null; } catch { }
    if (!el) {   // heading "Viewers" (optionally with a count) followed by a list
      const h = [...document.querySelectorAll('h1,h2,h3,h4,[role="heading"],button,span,div')].find(e => e.children.length <= 2 && /^\s*viewers\s*(\(?\d+\)?)?\s*$/i.test(e.textContent || '') && shown(e));
      if (h) { let p = h.parentElement; for (let i = 0; i < 4 && p && !el; i++, p = p.parentElement) el = p.querySelector('[role="list"], ul'); }
    }
    return el && !el.matches(S.members) ? el : null;
  };
  const namesIn = list => list ? [...new Set([...list.querySelectorAll(S.memberRow)].filter(r => !r.parentElement.closest(S.memberRow)).map(rowName).filter(Boolean))] : null;
  const vst = { last: {}, at: 0 };
  const scanViewers = () => {
    for (const [source, list] of [['LiveViewers', viewersList()], ['RoomMembers', [...document.querySelectorAll(S.members)].find(shown) || null]]) {
      const names = namesIn(list); if (!names) continue;
      const key = names.slice().sort().join('\n');
      if (vst.last[source] === key && Date.now() - vst.at < 30000) continue;
      vst.last[source] = key; vst.at = Date.now();
      post({ type: 'epik-viewers', source, names, virtualized: !!(list && list.matches('[data-virtuoso-scroller], [data-virtuoso-scroller] *')) || !!list.querySelector('[data-index]') });
    }
  };
  CFG.viewerDiag = () => {
    const v = viewersList(), m = document.querySelector(S.members);
    return { verified: false, viewersSelectorHits: (() => { try { return document.querySelectorAll(S.viewers).length; } catch { return 'bad selector'; } })(),
      viewersFound: !!v, viewersCount: v ? (namesIn(v) || []).length : 0, viewersSkeleton: skel(v, 5, { n: 40 }),
      membersFound: !!m, membersCount: m ? (namesIn(m) || []).length : 0 };
  };
  // Quick action: open a user's profile the normal way (click their name in the member list or chat). No moderation.
  window.__epikOpenProfile = name => {
    const want = String(name || '').trim().toLowerCase(); if (!want) return 'no name';
    const lists = [viewersList(), document.querySelector(S.members), findLog()].filter(Boolean);
    for (const l of lists) for (const b of l.querySelectorAll('button')) {
      const t = (b.textContent || '').trim().toLowerCase(), al = (b.getAttribute('aria-label') || '').toLowerCase();
      if (t === want || al === 'open ' + want || al === want + "'s profile") { b.scrollIntoView({ block: 'center' }); b.click(); return 'opened'; }
    }
    return 'not found (scroll the member list so the name is visible)';
  };

  window.__epikObs = st;
  const start = () => {
    new MutationObserver(schedule).observe(document.body || document.documentElement, { subtree: true, childList: true });
    setInterval(() => { try { scanViewers(); } catch { } }, 3000); setTimeout(() => { try { scanViewers(); } catch { } }, 500);
    setInterval(schedule, 2000);   // re-resolve the visible pane periodically (room switches toggle aria-hidden)
    schedule();
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
