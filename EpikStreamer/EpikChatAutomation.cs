namespace EpikStreamer;

/// <summary>
/// Everything that depends on EpikChat's page text lives here. If EpikChat changes a label, edit only this file.
/// Patterns are case-insensitive JavaScript regular expressions matched against an element's visible text
/// (or aria-label / title / value). The first pattern that matches a visible element wins.
/// </summary>
public static class EpikChatAutomation
{
    public const string LoginUrl = "https://www.epikchat.com/login";
    public const string ChatUrl = "https://www.epikchat.com/chat";
    public const string ExpectedServer = "rtmp://media.epikchat.com:1935/x";

    /// <summary>One automation step. The page helper's <c>__epik.step(Id, Arg)</c> is idempotent: it returns
    /// state "done" once the step's result is visible, otherwise it (re)clicks and returns "clicked"/"waiting"/"notfound".</summary>
    public record Step(string Id, string Name, string Arg = "", bool Optional = false, int TimeoutSec = 20);

    /// <summary>First-visit popups / banners to dismiss (all optional, tried a few times).</summary>
    public static readonly string[] DismissPatterns =
        { "^(got it|ok|okay|close|dismiss|no thanks|not now|skip|maybe later|accept( all)?|i agree|continue to chat)$", "^[×✕x]$" };

    // Real EpikChat flow (DOM inspected 2026-10-25). Dialog = React portal at the end of <body>:
    //   div.fixed.inset-0.z-50 > div[role=dialog][aria-modal=true][aria-label=...]   (a hidden emoji-picker
    //   [role=dialog][aria-hidden] also exists, so never take the first [role=dialog]).
    //   "What do you want to stream?" : div[role=group][aria-label="Stream source"] > button[aria-pressed] x3 ; footer Cancel / Continue
    //   (same dialog) "Choose your audience" : button[aria-pressed] Public / Private ; "Ready to go live" summary ; footer Back / Create live stream
    //   "Connect your encoder" : server rtmp://media.epikchat.com:1935/x, "Show stream key" button.
    public static Step[] Steps(string audience) => new[]
    {
        new Step("golive", "Open \"Go live\""),
        new Step("source", "Choose \"RTMP encoder\""),
        new Step("continue", "Continue"),
        new Step("audience", $"Audience: {audience}", audience.Equals("Private", StringComparison.OrdinalIgnoreCase) ? "Private" : "Public"),
        new Step("create", "Create live stream", TimeoutSec: 30),
        new Step("showkey", "Show stream key", Optional: true, TimeoutSec: 15),
    };

    /// <summary>Helper injected into the page: window.__epik.step(id, arg), .read(), .click(patterns), .loggedIn(), .dialog().</summary>
    public const string HelperJs = """
(() => {
  if (window.__epik && window.__epik.v === 4) return 'ready';
  const txt = e => (e && e.textContent || '').replace(/\s+/g, ' ').trim();
  const vis = e => { if (!e || !e.isConnected) return false; if (e.closest('[aria-hidden="true"]')) return false;
    const r = e.getBoundingClientRect(); const s = getComputedStyle(e);
    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0'; };
  const label = e => ((e.innerText || '') || e.getAttribute('aria-label') || e.getAttribute('title') || e.value || '').replace(/\s+/g, ' ').trim();
  const accName = e => (e.getAttribute('aria-label') || txt(e)).trim();
  const press = el => {           // robust click for React: scroll, pointer/mouse sequence ending in click
    el.scrollIntoView({ block: 'center', inline: 'center' });
    const r = el.getBoundingClientRect(); const o = { bubbles: true, cancelable: true, composed: true, view: window, clientX: r.left + r.width / 2, clientY: r.top + r.height / 2, button: 0 };
    try { el.dispatchEvent(new PointerEvent('pointerdown', { ...o, pointerId: 1, pointerType: 'mouse', isPrimary: true })); } catch { }
    el.dispatchEvent(new MouseEvent('mousedown', o));
    try { el.dispatchEvent(new PointerEvent('pointerup', { ...o, pointerId: 1, pointerType: 'mouse', isPrimary: true })); } catch { }
    el.dispatchEvent(new MouseEvent('mouseup', o));
    el.dispatchEvent(new MouseEvent('click', o));
  };
  // the open modal dialog: aria-modal=true (or a labelled dialog), visible, not aria-hidden; last one = topmost portal
  const dialog = () => { const d = [...document.querySelectorAll('[role=dialog],[role=alertdialog]')]
      .filter(e => e.getAttribute('aria-hidden') !== 'true' && (e.getAttribute('aria-modal') === 'true' || e.hasAttribute('aria-label')) && vis(e));
    return d.length ? d[d.length - 1] : null; };
  const dlgLabel = d => d ? (d.getAttribute('aria-label') || txt(d.querySelector('h1,h2,h3')) || '(dialog)') : '';
  const buttons = root => [...(root || document).querySelectorAll('button,[role=button]')].filter(vis);
  const last = {};
  const GAP = { create: 15000, golive: 4000, finalize: 8000, closeEncoder: 5000 };   // never double-create a stream on a slow page
  const throttled = id => { const now = Date.now(); if (last[id] && now - last[id] < (GAP[id] || 2500)) return true; last[id] = now; return false; };
  const RTMP = /rtmps?:\/\/[^\s"'<>]+/i;
  const stage = d => { const l = dlgLabel(d); const t = d ? txt(d) : '';
    if (/connect your encoder/i.test(l) || /connect your encoder|show stream key|your encoder connects next/i.test(t) || RTMP.test(t)) return 'encoder';
    if (/audience/i.test(l)) return 'audience';
    if (/what do you want to stream/i.test(l) || (d && d.querySelector('[role=group][aria-label="Stream source"]'))) return 'source';
    return d ? 'other' : 'none'; };
  const ORDER = { none: 0, other: 0, source: 1, audience: 2, encoder: 3 };
  const res = (state, info, d) => ({ state, info: info || '', dialog: dlgLabel(d === undefined ? dialog() : d), stage: stage(d === undefined ? dialog() : d) });
  const clickOnce = (id, el, d, what) => { if (throttled(id)) return res('waiting', 'clicked ' + what + ', waiting for the page', d); press(el); return res('clicked', what, d); };

  // ---- v2.4.1: the nav "Go live" (camera) button and "already live" detection. Selectors are listed in GOLIVE_SEL for diagnostics. ----
  const nameOf = e => (e.getAttribute('aria-label') || e.getAttribute('title') || txt(e) || '').replace(/\s+/g, ' ').trim();
  const GL_STRONG = /^(go live|go live now|start (a )?(live ?)?stream(ing)?|start broadcast(ing)?|new live stream|create (a )?live stream)$/i;  // anywhere outside dialogs
  const GL_WEAK = /^(stream|broadcast|live ?stream|camera|video|go-live|golive)$/i;                                                          // nav only
  const LIVE_NAME = /^((end|stop|finish)( (the|your))? ?(live ?)?(stream|broadcast|live)( now)?|(you('|’)?re|you are) live|live now|on air|manage (your )?(live ?)?stream)$/i;
  const NAV = 'header,nav,[role=banner],[role=navigation]';
  const inNav = e => !!e.closest(NAV) || e.getBoundingClientRect().top < 90;
  const iconHint = e => /video|camera|webcam|broadcast|go-?live|stream/.test([...e.querySelectorAll('svg,use,i,img')].map(x =>
      [x.getAttribute('class'), x.getAttribute('data-icon'), x.getAttribute('href'), x.getAttribute('xlink:href'), x.getAttribute('alt'), x.getAttribute('aria-label'), x.getAttribute('data-testid')].join(' ')).join(' ').toLowerCase());
  const greenish = e => { for (const x of [e, ...e.querySelectorAll('svg,svg *,span')].slice(0, 8)) { const cs = getComputedStyle(x);
      for (const c of [cs.color, cs.backgroundColor, cs.stroke, cs.fill]) { const m = /rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(c || ''); if (m) { const r = +m[1], g = +m[2], b = +m[3]; if (g > 110 && g > r + 40 && g > b + 15) return true; } } } return false; };
  const clickables = () => [...document.querySelectorAll('button,a,[role=button]')].filter(vis).filter(b => !b.closest('[role=dialog],[role=alertdialog]'));
  const findGoLive = () => {
    const all = clickables();
    const strong = all.filter(b => GL_STRONG.test(nameOf(b)) || GL_STRONG.test(accName(b)));
    let b = strong.find(inNav) || strong[0];
    if (b) return { el: b, how: 'name "' + nameOf(b) + '"' + (inNav(b) ? ' in nav' : '') };
    const nav = all.filter(inNav);
    b = nav.find(b => GL_WEAK.test(nameOf(b))); if (b) return { el: b, how: 'nav button "' + nameOf(b) + '"' };
    const icons = nav.filter(b => txt(b).length < 3 && b.querySelector('svg,img,i'));
    b = icons.find(iconHint); if (b) return { el: b, how: 'nav icon button (video/camera icon)' };
    b = icons.find(greenish); if (b) return { el: b, how: 'green nav icon button' };
    return null;
  };
  const liveState = () => {
    const d = dialog(); const st = stage(d);
    const endB = [...document.querySelectorAll('button,a,[role=button],[role=status]')].filter(vis).find(b => LIVE_NAME.test(nameOf(b)));
    const g = findGoLive();
    const active = g && ['aria-pressed', 'aria-current', 'data-active', 'data-live'].some(a => g.el.getAttribute(a) === 'true') || (g && g.el.getAttribute('data-state') === 'active');
    const flag = [...document.querySelectorAll('[data-live="true"],[data-streaming="true"],[data-broadcasting="true"]')].find(vis);
    let live = false, how = '';
    if (endB) { live = true; how = 'button "' + nameOf(endB) + '"'; }
    else if (active) { live = true; how = 'Go live button is active (' + g.how + ')'; }
    else if (flag) { live = true; how = 'element ' + flag.tagName.toLowerCase() + '[data-live]'; }
    return { live, how, session: live || st === 'encoder', stage: st, dialog: dlgLabel(d), golive: g ? g.how : '', loggedIn: !/\/login/i.test(location.pathname) && !document.querySelector('input[type=password]') };
  };
  const GOLIVE_SEL = { strong: GL_STRONG.source, navWeak: GL_WEAK.source, live: LIVE_NAME.source, nav: NAV, icon: 'svg/img class|data-icon|href ~ video|camera|webcam|broadcast|go-live|stream', green: 'icon-only nav button with a green color', verified: false };

  const steps = {
    golive() {
      const d = dialog(); if (ORDER[stage(d)] >= 1) return res('done', '', d);
      const g = findGoLive();
      return g ? clickOnce('golive', g.el, d, 'Go live (' + g.how + ')') : res('notfound', 'no Go live / camera button found in the nav', d);
    },
    source() {
      const d = dialog(); const st = stage(d);
      if (ORDER[st] >= 2) return res('done', '', d);
      if (st !== 'source') return res('notfound', 'stream-source dialog not open', d);
      const grp = d.querySelector('[role=group][aria-label="Stream source"]') || d;
      const b = [...grp.querySelectorAll('button')].find(b => txt(b).toLowerCase().includes('rtmp encoder'));
      if (!b) return res('notfound', 'no "RTMP encoder" button in the Stream source group', d);
      if (b.getAttribute('aria-pressed') === 'true') return res('done', 'RTMP encoder selected', d);
      return clickOnce('source', b, d, '"RTMP encoder"');
    },
    continue() {
      const d = dialog(); const st = stage(d);
      if (ORDER[st] >= 2) return res('done', '', d);
      if (st !== 'source') return res('notfound', 'stream-source dialog not open', d);
      const rt = [...d.querySelectorAll('button')].find(b => txt(b).toLowerCase().includes('rtmp encoder'));
      if (rt && rt.getAttribute('aria-pressed') !== 'true') return res('waiting', 'RTMP encoder not selected yet', d);
      const b = buttons(d).find(b => txt(b) === 'Continue');
      return b ? clickOnce('continue', b, d, '"Continue"') : res('notfound', 'no "Continue" button in the dialog', d);
    },
    audience(arg) {
      const d = dialog(); const st = stage(d);
      if (st === 'encoder') return res('done', '', d);
      if (st !== 'audience') return res('notfound', 'audience dialog not open', d);
      const want = (arg || 'Public').toLowerCase();
      const b = [...d.querySelectorAll('button[aria-pressed]')].find(b => {
        const first = [...b.querySelectorAll('span')].filter(sp => !sp.closest('[aria-hidden="true"]') && sp.children.length === 0).map(txt).find(t => /[a-z]/i.test(t)) || '';
        return first.toLowerCase() === want || txt(b).toLowerCase().replace(/^[^a-z]+/, '').startsWith(want); });
      if (!b) return res('notfound', 'no "' + arg + '" option', d);
      if (b.getAttribute('aria-pressed') === 'true') return res('done', arg + ' selected', d);
      return clickOnce('audience', b, d, '"' + arg + '"');
    },
    create() {
      const d = dialog(); const st = stage(d);
      if (st === 'encoder') return res('done', '', d);
      if (st !== 'audience') return res('notfound', 'audience dialog not open', d);
      const b = buttons(d).find(b => txt(b).toLowerCase() === 'create live stream');
      return b ? clickOnce('create', b, d, '"Create live stream"') : res('notfound', 'no "Create live stream" button', d);
    },
    // after "Connect your encoder": click a final "Go live"/"Start streaming" button once it is enabled (UNVERIFIED: the real
    // dialog may not have one); report whether the page says the ingest was detected.
    finalize() {
      const d = dialog(); const st = stage(d); const ls = liveState();
      if (st !== 'encoder') return res(ls.live ? 'done' : 'none', ls.live ? 'live (' + ls.how + ')' : 'no encoder dialog', d);
      const bs = buttons(d).filter(b => !b.disabled && b.getAttribute('aria-disabled') !== 'true');
      const go = bs.find(b => /^(go live|go live now|start (the )?(live ?)?stream(ing)?|start broadcast(ing)?|publish( stream)?|i'?m ready)$/i.test(nameOf(b)));
      if (go) return clickOnce('finalize', go, d, '"' + nameOf(go) + '"');
      const t = txt(d);
      const connected = /(encoder (is )?connected|receiving (video|your stream|data)|stream (is )?(detected|connected|received|live)|signal (detected|received)|you('|’)?re live|you are live)/i.test(t);
      const done = bs.find(b => /^(done|finish(ed)?|got it)$/i.test(txt(b)));
      return res(connected ? 'connected' : 'waiting', (connected ? 'ingest detected' : 'waiting for the ingest') + (done ? '; "' + txt(done) + '" available' : ''), d);
    },
    // close the encoder dialog only through a positive "Done"/"Finish"/"Got it" button; never Cancel / End / ×
    closeEncoder() {
      const d = dialog(); if (stage(d) !== 'encoder') return res('done', '', d);
      const b = buttons(d).find(b => /^(done|finish(ed)?|got it)$/i.test(txt(b)));
      return b ? clickOnce('closeEncoder', b, d, '"' + txt(b) + '"') : res('notfound', 'no Done button; dialog left open', d);
    },
    showkey() {
      const r = window.__epik.read(); if (r.key) return res('done', 'key visible');
      const d = dialog();
      const b = buttons(d || document).find(b => /^(show|reveal)( stream)? key$/i.test(txt(b)) || /^show$/i.test(txt(b)));
      return b ? clickOnce('showkey', b, d, '"Show stream key"') : res('notfound', 'no "Show stream key" button');
    },
  };

  window.__epik = {
    v: 4,
    live() { try { return liveState(); } catch (e) { return { live: false, how: 'error ' + e, session: false, stage: '', dialog: '', golive: '', loggedIn: true }; } },
    goLiveDiag() { const ls = this.live(); return { ...ls, selectors: GOLIVE_SEL }; },
    dialog() { const d = dialog(); return { label: dlgLabel(d), stage: stage(d) }; },
    step(id, arg) { try { return steps[id](arg); } catch (e) { return { state: 'error', info: String(e), dialog: '', stage: '' }; } },
    find(patterns) {
      const els = [...document.querySelectorAll('button,a,[role=button]')].filter(vis);
      for (const p of patterns) { const re = new RegExp(p, 'i'); const m = els.filter(e => { const t = label(e); return t.length > 0 && t.length < 120 && re.test(t); }); if (m.length) return m[0]; }
      return null;
    },
    click(patterns) { const e = this.find(patterns); if (!e) return { ok: false }; press(e); return { ok: true, text: label(e).slice(0, 80) }; },
    read() {
      const root = (() => { const d = dialog(); return d && stage(d) === 'encoder' ? d : document.body; })();
      const masked = v => /^[•*●·]+$/.test(v) || /^[•*●·\s]+$/.test(v);
      const cands = [];        // { v, ctx }
      const ctxOf = e => { let n = e, c = ''; for (let i = 0; i < 3 && n; i++, n = n.parentElement) c += ' ' + (n.getAttribute && (n.getAttribute('aria-label') || '')) + ' ' + (n.previousElementSibling ? txt(n.previousElementSibling) : ''); return c + ' ' + (e.name || '') + ' ' + (e.id || '') + ' ' + (e.placeholder || ''); };
      root.querySelectorAll('input,textarea').forEach(i => { if (i.value) cands.push({ v: i.value.trim(), ctx: ctxOf(i), src: 'input' }); });
      root.querySelectorAll('code,pre,kbd,samp,[data-clipboard-text],[data-copy],[data-value]').forEach(e => {
        const v = (e.getAttribute('data-clipboard-text') || e.getAttribute('data-copy') || e.getAttribute('data-value') || txt(e)).trim(); if (v) cands.push({ v, ctx: ctxOf(e), src: 'code' }); });
      root.querySelectorAll('span,div,p,dd,td').forEach(e => { if (e.children.length === 0) { const v = txt(e); if (v && v.length < 200) cands.push({ v, ctx: ctxOf(e), src: 'text' }); } });
      let server = (cands.find(c => RTMP.test(c.v)) || {}).v || ((root.innerText || '').match(RTMP) || [])[0] || '';
      server = (server.match(RTMP) || [''])[0];
      const KEYRE = /^[A-Za-z0-9_\-?=&.:]{8,128}$/;
      const okKey = v => KEYRE.test(v) && !RTMP.test(v) && !masked(v) && !/^\d{1,2}:\d{2}$/.test(v) && /\d/.test(v) && /[A-Za-z]/.test(v);
      let key = '';
      const byCtx = cands.filter(c => /key/i.test(c.ctx) && okKey(c.v));
      if (byCtx.length) key = byCtx.find(c => c.src !== 'text')?.v || byCtx[0].v;
      if (!key) { const m = (root.innerText || '').match(/stream key\s*[:\n]\s*([A-Za-z0-9_\-?=&.]{8,})/i); if (m && okKey(m[1])) key = m[1]; }
      if (!key) { const k = cands.find(c => c.src !== 'text' && okKey(c.v)); if (k) key = k.v; }
      // full URL with the key appended (rtmp://host/app/KEY)
      const full = /^(rtmps?:\/\/[^\/\s]+\/[^\/\s]+)\/([^\/\s]{8,})$/i.exec(server);
      if (full && (!key || full[2] === key)) { server = full[1]; key = key || full[2]; }
      return { server, key };
    },
    loggedIn() { return !/\/login/i.test(location.pathname) && !document.querySelector('input[type=password]'); }
  };
  return 'ready';
})()
""";

    // ===================================================================================================
    // Chat side panel (v2.1). UNTESTED against the live site: EpikChat's markup wasn't available when this
    // was written. All selectors/patterns live here so they can be fixed in one place. Everything only
    // *reads* the normal /chat page that the user has open; no EpikChat API is called.
    // ===================================================================================================

    /// <summary>CSS selectors tried (in order) for the viewer count element. UNTESTED guesses.</summary>
    public static readonly string[] ViewerCountSelectors =
    {
        "[data-viewer-count]", "[data-viewers]", ".viewer-count", ".viewers-count", ".viewers", "[class*='viewer'] [class*='count']",
        "[class*='viewer']", "[title*='viewer' i]", "[aria-label*='viewer' i]",
    };
    /// <summary>Text fallback: "12 viewers", "1.2k watching", "Viewers: 12". UNTESTED.</summary>
    public const string ViewerTextRegex = @"(?:(\d[\d,.]*\s*[kK]?)\s*(?:viewers?|watching))|(?:viewers?\s*[:\-]?\s*(\d[\d,.]*\s*[kK]?))";
    /// <summary>Selectors whose presence (visible) means "you are live". UNTESTED guesses.</summary>
    public static readonly string[] LiveSelectors =
    {
        "[data-live='true']", ".is-live", ".live-badge", ".broadcasting", "[class*='onair' i]", "[class*='live-indicator' i]",
    };
    /// <summary>Text fallback for live status ("You're live", "Broadcasting", "Stop broadcast"). UNTESTED.</summary>
    public const string LiveTextRegex = @"\b(you(?:'|’)?re live|you are live|broadcasting|on air|stop (?:broadcast|stream(?:ing)?)|end (?:broadcast|stream))\b";

    /// <summary>v2.4.1: adds the Go-live detection result + the selectors it uses (key "goLive") to the chat diagnostics JSON.</summary>
    public static string MergeGoLiveDiag(string? diagJson, string goLiveJson)
    {
        System.Text.Json.Nodes.JsonObject o;
        try { o = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(diagJson) ? "{}" : diagJson) as System.Text.Json.Nodes.JsonObject ?? new(); } catch { o = new() { ["raw"] = diagJson }; }
        try { o["goLive"] = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(goLiveJson) ? "null" : goLiveJson); } catch { o["goLive"] = goLiveJson; }
        return o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>v2.2.2: chat messages are read by Web/chat-observer.js, which matches EpikChat's real DOM
    /// (role=log "Chat messages" Virtuoso list, [data-chat-message-row] ids, [data-chat-message-layout], composer input).</summary>
    public static string ChatObserverJs(bool debug, string selfName)
    {
        using var st = typeof(EpikChatAutomation).Assembly.GetManifestResourceStream("EpikStreamer.Web.chat-observer.js")!;
        using var rd = new System.IO.StreamReader(st);
        var cfg = System.Text.Json.JsonSerializer.Serialize(new { debug, selfName });
        return "window.__EPIK_CFG = " + cfg + ";\n" + rd.ReadToEnd();
    }

    /// <summary>v2.7: the passive network tap (Web/chat-tap.js), registered BEFORE the page's own scripts run. Config: REST viewers
    /// fallback on/off + interval, and an optional protocol map override (validated JSON object, else ignored).</summary>
    public static string ChatTapJs(Core.ChatFeedSettings s)
    {
        using var st = typeof(EpikChatAutomation).Assembly.GetManifestResourceStream("EpikStreamer.Web.chat-tap.js")!;
        using var rd = new System.IO.StreamReader(st);
        System.Text.Json.Nodes.JsonNode? map = null;
        try { if (!string.IsNullOrWhiteSpace(s.ProtocolMapJson)) map = System.Text.Json.Nodes.JsonNode.Parse(s.ProtocolMapJson) as System.Text.Json.Nodes.JsonObject; } catch { map = null; }
        var cfg = new System.Text.Json.Nodes.JsonObject { ["restPoll"] = s.ViewersRestFallback, ["restPollSeconds"] = Math.Max(15, s.ViewersRestSeconds), ["map"] = map ?? new System.Text.Json.Nodes.JsonObject() };
        return "window.__EPIK_TAP_CFG = " + cfg.ToJsonString() + ";\n" + rd.ReadToEnd();
    }

    /// <summary>Compact mode: hide site chrome (header/nav/footer/ads) and tighten spacing so chat fits a narrow panel. UNTESTED.</summary>
    public const string CompactCss = """
header, nav, footer, .navbar, .site-header, .site-footer, [class*='advert' i], [id*='advert' i], [class*='banner' i], [class*='cookie' i] { display: none !important; }
body { font-size: 13px !important; min-width: 0 !important; }
[class*='sidebar' i]:not([class*='chat' i]):not([class*='user' i]) { display: none !important; }
[class*='message' i] { padding-top: 2px !important; padding-bottom: 2px !important; margin-top: 0 !important; margin-bottom: 0 !important; }
img[class*='avatar' i] { width: 22px !important; height: 22px !important; }
""";

    /// <summary>Injected on every /chat page load. Watches the DOM with a MutationObserver and posts
    /// { type:'epik-status', viewers, live } to the app via chrome.webview.postMessage when anything changes.</summary>
    public static string ObserverJs(bool compact) => """
(() => {
  const CFG = __CFG__;
  const STYLE_ID = '__epikstreamer_compact';
  window.__epikSetCompact = on => {
    let st = document.getElementById(STYLE_ID);
    if (on && !st) { st = document.createElement('style'); st.id = STYLE_ID; st.textContent = CFG.css; (document.head || document.documentElement).appendChild(st); }
    if (!on && st) st.remove();
  };
  const vis = e => { if (!e) return false; const r = e.getBoundingClientRect(); const s = getComputedStyle(e); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
  const num = t => { if (!t) return null; t = String(t).replace(/,/g, '').trim(); const m = t.match(/(\d+(?:\.\d+)?)\s*([kK])?/); if (!m) return null; return Math.round(parseFloat(m[1]) * (m[2] ? 1000 : 1)); };
  const read = () => {
    let viewers = null, live = false;
    for (const sel of CFG.viewerSel) {
      let e; try { e = document.querySelector(sel); } catch { continue; }
      if (!e || !vis(e)) continue;
      const v = num(e.getAttribute('data-viewer-count') || e.getAttribute('data-viewers') || e.innerText);
      if (v !== null) { viewers = v; break; }
    }
    const body = (document.body && document.body.innerText) || '';
    if (viewers === null) { const m = body.match(new RegExp(CFG.viewerRe, 'i')); if (m) viewers = num(m[1] || m[2]); }
    for (const sel of CFG.liveSel) { try { if ([...document.querySelectorAll(sel)].some(vis)) { live = true; break; } } catch { } }
    if (!live) live = new RegExp(CFG.liveRe, 'i').test(body);
    return { viewers, live };
  };
  let last = '', timer = 0;
  const post = () => {
    timer = 0;
    const s = read(); const k = JSON.stringify(s);
    if (k !== last) { last = k; try { window.chrome.webview.postMessage({ type: 'epik-status', viewers: s.viewers, live: s.live, url: location.pathname }); } catch { } }
  };
  const schedule = () => { if (!timer) timer = setTimeout(post, 1000); };   // throttle: at most once a second
  const start = () => {
    window.__epikSetCompact(CFG.compact);
    new MutationObserver(() => schedule()).observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true });
    setInterval(schedule, 15000); post();
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
""".Replace("__CFG__", System.Text.Json.JsonSerializer.Serialize(new
    {
        css = CompactCss, viewerSel = ViewerCountSelectors, viewerRe = ViewerTextRegex, liveSel = LiveSelectors, liveRe = LiveTextRegex, compact
    }));
}

/// <summary>
/// v2.4.1: makes sure an EpikChat live session exists before ffmpeg pushes to the ingest (EpikChat refuses the RTMP
/// stream until "Go live → RTMP encoder → audience → Create live stream" was done). Host-agnostic: <paramref name="eval"/>
/// runs JS in the signed-in chat page (WebView2 ExecuteScriptAsync / WebKitGTK eval) and returns the result as JSON.
/// Only the site's normal buttons are clicked; no EpikChat API is called and no password is seen.
/// </summary>
public sealed class LiveSession
{
    public enum Outcome { AlreadyLive, Created, Failed }
    public record Result(Outcome Outcome, string Message, string? Server = null, string? Key = null, bool KeyChanged = false);

    readonly Func<string, Task<string>> _eval;
    public Action<string, bool> Status { get; set; } = (_, _) => { };
    public int PollMs { get; set; } = 500;
    public LiveSession(Func<string, Task<string>> eval) { _eval = eval; }

    public const string Fallback = "Click the camera icon (Go live) in the EpikChat chat panel and finish the dialog; the encoder keeps retrying.";

    async Task<System.Text.Json.JsonElement> Js(string script)
    {
        var raw = await _eval(script);
        try { return System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(raw) ? "null" : raw).RootElement.Clone(); }
        catch { return default; }
    }
    Task EnsureHelper() => Js(EpikChatAutomation.HelperJs + "\n;0");
    static string S(System.Text.Json.JsonElement e, string k) => e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
    static bool B(System.Text.Json.JsonElement e, string k) => e.ValueKind == System.Text.Json.JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

    /// <summary>Current page state: { live, how, session, stage, dialog, golive, loggedIn }.</summary>
    public async Task<(bool Live, bool Session, string How, string Stage, bool LoggedIn)> StateAsync()
    {
        await EnsureHelper();
        var r = await Js("window.__epik.live()");
        if (r.ValueKind != System.Text.Json.JsonValueKind.Object) return (false, false, "page not ready", "", true);
        return (B(r, "live"), B(r, "session"), S(r, "how"), S(r, "stage"), !r.TryGetProperty("loggedIn", out var li) || li.ValueKind != System.Text.Json.JsonValueKind.False);
    }

    /// <summary>Detects an existing live session; otherwise runs the Go-live dialog and reads the session's key.
    /// Never creates a second session when one is detected (live indicator or the encoder dialog already open).</summary>
    public async Task<Result> EnsureAsync(string audience, string? savedKey, int timeoutSec, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, timeoutSec));
        try
        {
            var st = await StateAsync();
            if (!st.LoggedIn) return new(Outcome.Failed, "Not signed in to EpikChat in the chat panel. " + Fallback);
            if (st.Live) { Status("Live session ready (already live on EpikChat: " + st.How + ")", false); return new(Outcome.AlreadyLive, "already live: " + st.How); }
            if (st.Stage != "encoder")
            {
                Status("Opening Go live on EpikChat…", false);
                foreach (var s in EpikChatAutomation.Steps(audience))
                {
                    if (s.Id == "showkey") break;
                    string state = "", info = ""; int clicks = 0;
                    var end = Min(deadline, DateTime.UtcNow.AddSeconds(s.TimeoutSec));
                    while (DateTime.UtcNow < end)
                    {
                        ct.ThrowIfCancellationRequested();
                        await EnsureHelper();
                        var r = await Js($"window.__epik.step({System.Text.Json.JsonSerializer.Serialize(s.Id)}, {System.Text.Json.JsonSerializer.Serialize(s.Arg)})");
                        state = S(r, "state"); info = S(r, "info");
                        if (state == "done") break;
                        if (s.Id == "golive" && state == "clicked" && ++clicks >= 2)
                        {   // never keep toggling the nav button: if the second click still didn't open the source dialog, stop
                            await Task.Delay(PollMs * 4, ct);
                            var d = await Js("window.__epik.dialog()");
                            if (S(d, "stage") is "source" or "audience" or "encoder") continue;
                            return new(Outcome.Failed, (S(d, "stage") == "other" ? $"The Go live button opened \"{S(d, "label")}\" instead of the stream-source dialog (maybe you're already live). " : "The Go live button didn't open the stream dialog. ") + Fallback);
                        }
                        if (s.Id == "golive" && state == "notfound")
                        {   // maybe we went live meanwhile (another tab / the user clicked) → don't create a second session
                            var again = await StateAsync();
                            if (again.Live) { Status("Live session ready (" + again.How + ")", false); return new(Outcome.AlreadyLive, "already live: " + again.How); }
                        }
                        await Task.Delay(PollMs, ct);
                    }
                    if (state != "done")
                        return new(Outcome.Failed, $"Go live step \"{s.Name}\" didn't complete ({state}{(info.Length > 0 ? ": " + info : "")}). " + Fallback);
                    Status("Opening Go live on EpikChat… " + s.Name + " ✓", false);
                }
            }
            // "Connect your encoder": read the session's server + key (reveal it if needed)
            string server = "", key = "";
            while (DateTime.UtcNow < deadline && key.Length == 0)
            {
                ct.ThrowIfCancellationRequested();
                await EnsureHelper();
                var r = await Js("window.__epik.read()");
                server = S(r, "server"); key = S(r, "key");
                if (key.Length == 0) { await Js("window.__epik.step('showkey', '')"); await Task.Delay(PollMs * 2, ct); }
            }
            if (key.Length == 0)
            {
                if (!string.IsNullOrEmpty(savedKey)) { Status("Live session ready (created; couldn't read the key, using the saved one)", false); return new(Outcome.Created, "created, key not read", null, null); }
                return new(Outcome.Failed, "Live session created but the stream key wasn't found on the page. " + Fallback);
            }
            if (server.Length == 0) server = EpikChatAutomation.ExpectedServer;
            bool changed = !string.Equals(key, savedKey?.Trim(), StringComparison.Ordinal);
            Status(changed ? "Live session ready (EpikChat issued a different stream key; using it)" : "Live session ready", false);
            return new(Outcome.Created, changed ? "created, new key" : "created, same key", server, key, changed);
        }
        catch (OperationCanceledException) { return new(Outcome.Failed, "Go live cancelled."); }
        catch (Exception ex) { return new(Outcome.Failed, "Go live automation error: " + ex.Message + ". " + Fallback); }
    }

    /// <summary>After ffmpeg started: while the encoder dialog is open, click a final "Go live"/"Start streaming" button once it
    /// appears/enables (UNVERIFIED that EpikChat has one), then close the dialog with its own "Done" button if it has one.
    /// Returns a short description of what happened.</summary>
    public async Task<string> FinalizeAsync(int timeoutSec, CancellationToken ct = default)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutSec); string last = "";
        try
        {
            while (DateTime.UtcNow < end)
            {
                ct.ThrowIfCancellationRequested();
                await EnsureHelper();
                var r = await Js("window.__epik.step('finalize', '')");
                var state = S(r, "state"); var info = S(r, "info"); last = state + (info.Length > 0 ? " (" + info + ")" : "");
                if (state == "done") return "live on EpikChat: " + info;
                if (state == "none") return "no encoder dialog open";
                if (state == "clicked") Status("Clicked " + info + " on EpikChat", false);
                if (state == "connected")
                {
                    var c = await Js("window.__epik.step('closeEncoder', '')");
                    return "ingest detected" + (S(c, "state") == "clicked" ? "; closed the encoder dialog" : "; encoder dialog left open");
                }
                await Task.Delay(PollMs * 2, ct);
            }
        }
        catch (OperationCanceledException) { return "cancelled"; }
        catch (Exception ex) { return "error: " + ex.Message; }
        return "timed out waiting for EpikChat to confirm (" + last + ")";
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
