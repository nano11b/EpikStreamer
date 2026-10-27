// EpikStreamer chat observer (v2.7.0). Injected into EpikChat's normal /chat page in the app's WebView2 panel.
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
    // v2.5 Ball Pit. ⚠ NOT VERIFIED: avatar = first <img> in a member row; name colour = computed colour of the name element.
    memberAvatar: 'img[src]',
    memberName: 'button, [data-username], span',
    membersTotal: '[aria-label="Room members"][aria-setsize], [data-members-count]',
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
  // v2.7.1 VERIFIED container: <aside aria-label="Your live stream viewers"> → region "Viewers" (a "Search viewers" box, then
  // rows or "No viewers yet"). Row markup is still unknown, so rows are read generically.
  const named = (e, re) => { const al = e.getAttribute('aria-label'); if (al) return re.test(al.trim());
    const ids = (e.getAttribute('aria-labelledby') || '').split(/\s+/).filter(Boolean); return ids.length > 0 && re.test(ids.map(i => (document.getElementById(i) || {}).textContent || '').join(' ').trim()); };
  const liveAside = () => [...document.querySelectorAll('aside, [role="complementary"]')].find(e => named(e, /^your live stream viewers$/i)) || null;
  const liveViewersRegion = () => { const a = liveAside(); if (!a) return null;
    return [...a.querySelectorAll('[role="region"], section')].find(e => named(e, /^viewers$/i)) || a; };
  const namesInRegion = reg => {
    if (/^\s*no viewers yet\s*$/im.test(reg.innerText || reg.textContent || '') && !reg.querySelector('[role="listitem"], li')) return [];
    let rows = [...reg.querySelectorAll('[role="listitem"], li, [role="row"], [role="option"]')].filter(r => !r.parentElement.closest('[role="listitem"], li, [role="row"], [role="option"]'));
    if (!rows.length) rows = [...reg.querySelectorAll('button, a')].filter(b => !/search|close|copy|show|stream|options|end live|preview|credentials/i.test(b.getAttribute('aria-label') || b.textContent || ''));
    const nm = r => { const al = (r.getAttribute('aria-label') || (r.querySelector('[aria-label]') || { getAttribute: () => '' }).getAttribute('aria-label') || '').trim();
      let n = /^Open (.+?)('s profile)?$/i.exec(al)?.[1] || /^(.+?)'s profile$/i.exec(al)?.[1] || '';
      if (!n) n = ((r.innerText || r.textContent || '').trim().split('\n')[0] || '');
      n = n.replace(/\s+/g, ' ').trim(); return /^(no viewers yet|search viewers|viewers)$/i.test(n) ? '' : n.slice(0, 40); };
    return [...new Set(rows.filter(r => !r.closest('input, [role="searchbox"], [role="textbox"]')).map(nm).filter(Boolean))];
  };
  window.__epikLiveAside = liveAside;
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
    const reg = liveViewersRegion();
    for (const [source, list] of [['LiveViewers', reg || viewersList()], ['RoomMembers', memberList()]]) {
      const names = list && list === reg ? namesInRegion(reg) : namesIn(list); if (!names) continue;
      const key = names.slice().sort().join('\n');
      if (vst.last[source] === key && Date.now() - vst.at < 30000) continue;
      vst.last[source] = key; vst.at = Date.now();
      post({ type: 'epik-viewers', source, names, virtualized: !!(list && list.matches('[data-virtuoso-scroller], [data-virtuoso-scroller] *')) || !!list.querySelector('[data-index]') });
    }
  };
  CFG.viewerDiag = () => {
    const v = viewersList(), m = document.querySelector(S.members);
    return { verified: false, liveViewersAside: !!liveAside(), liveViewersRegion: (() => { const r = liveViewersRegion(); return r ? (r === liveAside() ? 'aside only' : 'region') : null; })(), viewersSelectorHits: (() => { try { return document.querySelectorAll(S.viewers).length; } catch { return 'bad selector'; } })(),
      viewersFound: !!v, viewersCount: v ? (namesIn(v) || []).length : 0, viewersSkeleton: skel(v, 5, { n: 40 }),
      membersFound: !!m, membersCount: m ? (namesIn(m) || []).length : 0, ballPit: ballPitDiag() };
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

  // v2.7 opt-in ("allow switching room to reply"): open a room by clicking its entry in EpikChat's own room list, like a user would.
  // ⚠ NOT VERIFIED against the live site: any visible link/button/tab/list item (outside the chat log and member lists) whose
  // text is exactly the room name.
  window.__epikSwitchRoom = name => {
    // 2.8.1: quotes normalized like the C# side (the socket can carry "Drugs''R''Us" where the page shows "Drugs'R'Us" / ’)
    const norm = s => String(s || '').trim().replace(/[\u2018\u2019\u02BC`]/g, "'").replace(/'{2,}/g, "'").replace(/\s+/g, ' ').toLowerCase();
    const want = norm(name); if (!want) return 'no name';
    if (norm(st.room) === want) return 'clicked';
    const log = findLog(); const VIEWERS_ASIDE = 'aside[aria-label="Your live stream viewers" i]';
    const cands = [...document.querySelectorAll('a, button, [role="link"], [role="tab"], [role="treeitem"], [role="option"], [role="menuitem"], [role="listitem"]')]
      .filter(e => shown(e) && !(log && log.contains(e)) && !e.closest(S.members) && !e.closest('[role="dialog"]') && !e.closest(VIEWERS_ASIDE));
    const lbl = e => norm((e.getAttribute('aria-label') || '').trim() || (e.textContent || ''));
    // v2.7.1 (verified): the sidebar rail's room buttons carry the room name as their accessible name, e.g. "The_Bar_Alc_420"
    const hit = cands.find(e => norm(e.getAttribute('aria-label')) === want) || cands.find(e => lbl(e) === want) || cands.find(e => lbl(e).replace(/^#\s*/, '').replace(/\s*\d+$/, '') === want);
    if (!hit) return 'room not found in the room list';
    hit.click(); setTimeout(schedule, 300); return 'clicked';
  };

  // ---- v2.5 Ball Pit: full member roster (read-only; scrolls the virtualized list, then puts it back) ----
  // absolutely-positioned virtual rows can leave the list element itself 0px tall, so also accept a visible-tree list with rows
  const memberList = () => { const all = [...document.querySelectorAll(S.members)]; return all.find(shown) || all.find(e => e.isConnected && !e.closest('[aria-hidden="true"]') && e.querySelector(S.memberRow)) || null; };
  const mst = { userAt: 0, busy: false, ours: 0 };
  const scroller = list => { for (let e = list; e && e !== document.body; e = e.parentElement) { const cs = getComputedStyle(e); if (/(auto|scroll)/.test(cs.overflowY) && e.scrollHeight > e.clientHeight + 4) return e; } return null; };
  const avatarOf = r => { const i = r.querySelector(S.memberAvatar); const u = i && (i.currentSrc || i.getAttribute('src') || ''); return u && !/^data:image\/svg/.test(u) ? new URL(u, location.href).href : ''; };
  const colorOf = r => {
    const b = r.querySelector('button'); const cands = b ? [b, ...b.querySelectorAll('*')] : [...r.querySelectorAll(S.memberName)];
    const name = rowName(r);
    const base = getComputedStyle(r.closest(S.members) || r).color;   // inherited default text colour = no name colour
    for (const e of cands.reverse()) { if ((e.textContent || '').trim() !== name) continue; const c = getComputedStyle(e).color; if (c && c !== base) return c; }   // deepest first
    return '';
  };
  const memberRows = list => [...list.querySelectorAll(S.memberRow)].filter(r => !r.parentElement.closest(S.memberRow));
  const totalOf = list => {
    const a = list.getAttribute('aria-setsize') || (list.querySelector('[aria-setsize]') || { getAttribute: () => '' }).getAttribute('aria-setsize');
    if (+a > 0) return +a;
    for (let p = list.parentElement, i = 0; p && i < 4; p = p.parentElement, i++) {   // a "Members 123" / "123 online" heading nearby
      const h = [...p.querySelectorAll('h1,h2,h3,h4,[role="heading"],span,div')].find(e => e.children.length <= 2 && !list.contains(e) && /^\s*(members|online|people)?\s*[(·:]?\s*\d{1,5}\s*\)?\s*(members|online|people)?\s*$/i.test(e.textContent || '') && /\D/.test((e.textContent || '').trim()));
      if (h) return +((h.textContent || '').match(/\d+/) || [0])[0];
    }
    return 0;
  };
  window.__epikMembers = async (full) => {
    const list = memberList();
    if (!list) { post({ type: 'epik-members', members: [], total: 0, complete: false, virtualized: false, found: false }); return 'no list'; }
    if (mst.busy) return 'busy';
    const out = new Map();
    const take = () => { for (const r of memberRows(list)) { const name = rowName(r); if (!name) continue; const k = name.toLowerCase(); const o = out.get(k) || { name, avatar: '', color: '' }; o.avatar = o.avatar || avatarOf(r); o.color = o.color || colorOf(r); out.set(k, o); } };
    const sc = scroller(list);
    const virtualized = !!list.querySelector('[data-index]') || !!list.closest('[data-virtuoso-scroller]');
    let complete = !sc;
    take();
    if (sc && full && Date.now() - mst.userAt > 5000) {   // don't fight the user's own scrolling
      mst.busy = true; const keep = sc.scrollTop;
      try {
        mst.ours = Date.now(); sc.scrollTop = 0; await new Promise(r => setTimeout(r, 150)); take();
        for (let i = 0; i < 400; i++) {
          if (Date.now() - mst.userAt < 1000 && mst.userAt > mst.ours + 50) break;   // user grabbed the list: stop
          const before = sc.scrollTop; mst.ours = Date.now(); sc.scrollTop = before + Math.max(40, sc.clientHeight * 0.8);
          await new Promise(r => setTimeout(r, 120)); take();
          if (sc.scrollTop <= before + 1) { complete = true; break; }
        }
      } finally { mst.ours = Date.now(); sc.scrollTop = keep; mst.busy = false; }
    }
    const members = [...out.values()].slice(0, 2000);
    const total = totalOf(list);
    if (total > 0 && members.length < total) complete = false;
    post({ type: 'epik-members', members, total, complete, virtualized, found: true });
    return members.length + (complete ? ' (complete)' : ' (partial)');
  };
  document.addEventListener('wheel', e => { if (e.target.closest && e.target.closest(S.members)) mst.userAt = Date.now(); }, { passive: true, capture: true });
  document.addEventListener('pointerdown', e => { if (e.target.closest && e.target.closest(S.members)) mst.userAt = Date.now(); }, { passive: true, capture: true });
  document.addEventListener('touchmove', e => { if (e.target.closest && e.target.closest(S.members)) mst.userAt = Date.now(); }, { passive: true, capture: true });
  // Avatar fallback: re-fetch an image the page already shows (same request the page makes) and hand it over as a data: URL.
  window.__epikFetchAvatar = async (url) => {
    const done = (data, err) => post({ type: 'epik-avatar', url, data: data || '', error: err || '' });
    try {
      const r = await fetch(url, { credentials: 'include', cache: 'force-cache' });
      if (!r.ok) return done('', 'http ' + r.status);
      const b = await r.blob(); if (b.size > 2000000) return done('', 'too big');
      const fr = new FileReader(); fr.onload = () => done(String(fr.result)); fr.onerror = () => done('', 'read'); fr.readAsDataURL(b);
    } catch (e) {   // CORS: draw the already-loaded <img> instead (works when the image is same-origin / CORS-enabled)
      try {
        const img = [...document.images].find(i => i.currentSrc === url || i.src === url);
        if (!img || !img.complete) return done('', 'not loaded');
        const c = document.createElement('canvas'); c.width = Math.min(128, img.naturalWidth || 64); c.height = Math.min(128, img.naturalHeight || 64);
        c.getContext('2d').drawImage(img, 0, 0, c.width, c.height); done(c.toDataURL('image/png'));
      } catch (e2) { done('', 'blocked'); }
    }
    return 'started';
  };
  const ballPitDiag = () => {
    const m = memberList(); const rows = m ? memberRows(m) : [];
    return { verified: false, note: 'Ball Pit member avatar/colour/total selectors are guesses until checked on the live site',
      selectors: { members: S.members, memberRow: S.memberRow, memberAvatar: S.memberAvatar, memberName: S.memberName, membersTotal: S.membersTotal },
      listFound: !!m, rowsRendered: rows.length, rowsWithAvatar: rows.filter(r => !!avatarOf(r)).length, rowsWithColor: rows.filter(r => !!colorOf(r)).length,
      scrollable: !!(m && scroller(m)), virtualized: !!(m && (m.querySelector('[data-index]') || m.closest('[data-virtuoso-scroller]'))), totalShown: m ? totalOf(m) : 0 };
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
