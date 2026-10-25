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

    public record Step(string Name, string[] Patterns, bool Optional = false, int TimeoutSec = 20);

    /// <summary>First-visit popups / banners to dismiss (all optional, tried a few times).</summary>
    public static readonly string[] DismissPatterns =
        { "^(got it|ok|okay|close|dismiss|no thanks|not now|skip|maybe later|accept( all)?|i agree|continue to chat)$", "^[×✕x]$" };

    public static Step[] Steps(string audience) => new[]
    {
        new Step("Open \"Go live\"", new[] { "^go live$", "go live" }),
        new Step("Choose RTMP encoder", new[] { "^rtmp encoder", "rtmp encoder \\(obs", "rtmp", "obs and streaming software" }),
        new Step("Continue", new[] { "^continue$", "^next$" }),
        new Step($"Audience: {audience}", audience.Equals("Private", StringComparison.OrdinalIgnoreCase)
            ? new[] { "^private$", "^private\\b" } : new[] { "^public$", "^public\\b" }),
        new Step("Continue", new[] { "^continue$", "^next$" }),
        new Step("Create live stream", new[] { "^create live stream$", "create live stream", "^create stream$", "^create$" }),
        new Step("Show stream key", new[] { "^show stream key$", "show stream key", "^show key$", "^show$", "^reveal" }, Optional: true),
    };

    /// <summary>Helper injected into the page. Exposes window.__epik.click(patterns) and window.__epik.read().</summary>
    public const string HelperJs = """
(() => {
  if (window.__epik) return 'ready';
  const vis = e => { const r = e.getBoundingClientRect(); const s = getComputedStyle(e);
    return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && !e.disabled; };
  const label = e => ((e.innerText || '') || e.getAttribute('aria-label') || e.getAttribute('title') || e.value || '').replace(/\s+/g, ' ').trim();
  const SEL = 'button,a,[role=button],[role=radio],[role=option],[role=menuitem],[role=tab],label,input[type=button],input[type=submit],li,div,span';
  window.__epik = {
    find(patterns) {
      const els = [...document.querySelectorAll(SEL)].filter(vis);
      for (const p of patterns) {
        const re = new RegExp(p, 'i');
        const m = els.filter(e => { const t = label(e); return t.length > 0 && t.length < 120 && re.test(t); });
        if (m.length) {
          const INT = 'button,a,[role=button],[role=radio],[role=option],[role=menuitem],[role=tab],label,input';
          const score = e => (e.closest(INT) ? 0 : 1);
          const depth = e => { let d = 0; while (e) { d++; e = e.parentElement; } return d; };
          m.sort((a, b) => score(a) - score(b) || label(a).length - label(b).length || depth(b) - depth(a));
          return m[0];
        }
      }
      return null;
    },
    click(patterns) {
      const e = this.find(patterns); if (!e) return { ok: false };
      const t = e.closest('button,a,[role=button],[role=radio],[role=option],[role=menuitem],label') || e;
      t.scrollIntoView({ block: 'center' }); t.click();
      const radio = t.querySelector && t.querySelector('input[type=radio]'); if (radio && !radio.checked) radio.click();
      return { ok: true, text: label(t).slice(0, 80) };
    },
    read() {
      const texts = [];
      document.querySelectorAll('input,textarea').forEach(i => { if (i.value) texts.push({ v: i.value.trim(), ctx: (i.closest('div,label,section,form')?.innerText || '') + ' ' + (i.name || '') + ' ' + (i.id || '') + ' ' + (i.placeholder || '') }); });
      const body = document.body.innerText || '';
      const urlRe = /rtmps?:\/\/[^\s"'<>]+/i;
      let server = (texts.find(t => urlRe.test(t.v)) || {}).v || (body.match(urlRe) || [])[0] || '';
      let key = '';
      const k = texts.find(t => !urlRe.test(t.v) && /key/i.test(t.ctx) && t.v.length >= 6 && !/^[•*]+$/.test(t.v));
      if (k) key = k.v;
      if (!key) { const m = body.match(/stream key\s*[:\n]\s*([A-Za-z0-9_\-?=&.]{6,})/i); if (m) key = m[1]; }
      if (server.includes('/') && key && server.endsWith('/' + key)) server = server.slice(0, -key.length - 1);
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
    new MutationObserver(schedule).observe(document.documentElement, { subtree: true, childList: true, characterData: true, attributes: true });
    setInterval(schedule, 15000); post();
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
""".Replace("__CFG__", System.Text.Json.JsonSerializer.Serialize(new
    {
        css = CompactCss, viewerSel = ViewerCountSelectors, viewerRe = ViewerTextRegex, liveSel = LiveSelectors, liveRe = LiveTextRegex, compact
    }));
}
