using System.Text.Json;
using System.Text.RegularExpressions;
using EpikStreamer.Browser;
using EpikStreamer.Core;
namespace EpikStreamer;

/// <summary>
/// Headless-ish self tests run against the real WebKitGTK host (needs a DISPLAY):
///   --selftest-host                 protocol round-trip (eval, addScript, message bridge)
///   --selftest-keyfetch DIR AUD     key-fetch automation against the mock go-live page (DIR = EpikStreamer.Tests/mock)
///   --selftest-chat DIR OUT.json    chat observer against the mock chat page; writes page results + C#-received messages
///   --selftest-tap DIR OUT.json     v2.7 live feed: tap + observer + router against ws-server.js (UI on another room)
///   --selftest-secret               keyring/file secret store round-trip
/// Only mock pages and fake keys are used.
/// </summary>
static class SelfTest
{
    static void P(string s) => Console.WriteLine(s);

    public static async Task<int> Run(string[] args)
    {
        Environment.SetEnvironmentVariable("EPIK_TEST_ALLOW_FILE", "1");
        try
        {
            return args[0] switch
            {
                "--selftest-host" => await Host(),
                "--selftest-keyfetch" => await KeyFetch(args[1], args.Length > 2 ? args[2] : "Public"),
                "--selftest-chat" => await Chat(args[1], args[2]),
                "--selftest-golive" => await GoLive(args[1]),
                "--selftest-members" => await Members(args[1], args[2]),
                "--selftest-tap" => await Tap(args[1], args[2]),
                "--selftest-secret" => Secret_(),
                _ => Usage(),
            };
        }
        finally { BrowserClient.Shutdown(); }
    }
    static int Usage() { P("usage: --selftest-host | --selftest-keyfetch MOCKDIR AUD | --selftest-chat MOCKDIR OUT | --selftest-golive MOCKDIR | --selftest-secret"); return 2; }

    static async Task<BrowserClient> Client()
    {
        var b = await BrowserClient.GetAsync() ?? throw new Exception("browser host unavailable: " + BrowserClient.Unavailable);
        P("browser host ready, WebKitGTK " + b.EngineVersion);
        return b;
    }

    static string Tmp(string name, string html)
    {
        var dir = Path.Combine(Path.GetTempPath(), "epik-selftest-" + Environment.ProcessId); Directory.CreateDirectory(dir);
        if (!html.Contains("charset", StringComparison.OrdinalIgnoreCase)) html = "<meta charset=\"utf-8\">" + html;   // file:// pages default to Latin-1 in WebKit
        var f = Path.Combine(dir, name); File.WriteAllText(f, html); return f;
    }

    static async Task<int> Host()
    {
        var b = await Client(); int fails = 0;
        void Check(bool ok, string what) { P((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
        var msgs = new List<string>(); b.Message += (v, u, d) => { lock (msgs) msgs.Add(d); };
        var loaded = new TaskCompletionSource<string>(); b.Loaded += (v, u) => { if (v == "t" && u.StartsWith("file:")) loaded.TrySetResult(u); };
        var page = Tmp("host.html", "<html><head><title>host</title></head><body><p id=x>hi</p><script>window.chrome.webview.postMessage({type:'hello',n:1});</script></body></html>");
        b.Open("t", "selftest", 400, 300, show: false);
        b.AddScript("t", "window.__early = 42;");
        b.Navigate("t", "file://" + page);
        Check(await Task.WhenAny(loaded.Task, Task.Delay(15000)) == loaded.Task, "loaded event for file page");
        Check(await b.EvalAsync("t", "1+2") == "3", "eval number");
        Check(await b.EvalAsync("t", "document.getElementById('x').textContent") == "\"hi\"", "eval string (DOM)");
        Check(await b.EvalAsync("t", "({a:[1,'b']})") == "{\"a\":[1,\"b\"]}", "eval object → JSON");
        Check(await b.EvalAsync("t", "undefined") is "null" or "", "eval undefined → null");
        Check(await b.EvalAsync("t", "window.__early") == "42", "document-start script injected");
        await Task.Delay(500);
        lock (msgs) Check(msgs.Any(m => m.Contains("\"hello\"")), "chrome.webview.postMessage shim → host message");
        try { await b.EvalAsync("t", "throw new Error('boom')"); Check(true, "eval exception reported without crashing host"); } catch (Exception ex) { Check(ex.Message.Contains("boom"), "eval exception surfaced: " + ex.Message); }
        Check(await b.EvalAsync("t", "2*21") == "42", "host still alive after exception");
        b.Close("t");
        return fails;
    }

    static async Task<int> KeyFetch(string mockDir, string aud)
    {
        var html = File.ReadAllText(Path.Combine(mockDir, "epikchat-golive.html"));
        // the page's own runner + helper are for the Chrome test; here C# (KeyFetcher) drives the steps through WebKit
        html = Regex.Replace(html, @"<script src=""helper.js""></script>\s*<script>.*?</script>", "", RegexOptions.Singleline);
        var f = Tmp("golive.html", html);
        var b = await Client();
        b.Open("signin", "selftest sign-in", 1000, 760);
        var kf = new KeyFetcher(b, "signin") { ChatUrl = "file://" + f, SettleMs = 800 };
        kf.Status = (s, err) => P((err ? "  ! " : "  · ") + s);
        var loaded = new TaskCompletionSource(); void H(string v, string u) { if (v == "signin") loaded.TrySetResult(); }
        b.Loaded += H; b.Navigate("signin", "file://" + f); await Task.WhenAny(loaded.Task, Task.Delay(15000)); b.Loaded -= H;
        var ok = await kf.RunAsync(aud);
        string Ev(string js) => b.EvalAsync("signin", js).GetAwaiter().GetResult();
        var chosen = Ev("window.__chosen || ''"); var audience = Ev("window.__audience || ''"); var creates = Ev("window.__creates || 0");
        P($"  chosen={chosen} audience={audience} creates={creates} server={kf.ServerUrl} key={(kf.StreamKey is { Length: > 4 } k ? "••••" + k[^4..] : "-")}");
        bool pass = ok && chosen.Contains("RTMP encoder") && audience.Contains(aud) && creates == "1"
                    && kf.ServerUrl == "rtmp://media.epikchat.com:1935/x" && kf.StreamKey == "FAKE_mock_Key_12345abc";
        P((pass ? "PASS" : "FAIL") + " mock key fetch via WebKitGTK host, audience=" + aud);
        b.Close("signin");
        return pass ? 0 : 1;
    }

    static async Task<int> Chat(string mockDir, string outFile)
    {
        var html = File.ReadAllText(Path.Combine(mockDir, "epikchat-chat.html"));
        html = html.Replace("<script src=\"chat-observer.js\"></script>", "");   // ChatPanel injects the observer at document start instead
        var f = Tmp("chat.html", html);
        var received = new List<ChatMessage>(); var statuses = new List<string>(); var viewers = new List<string[]>();
        var chat = new ChatPanel { ChatUrl = "file://" + f, DebugMessages = true, SelfName = "" };
        chat.MessageReceived += m => { lock (received) received.Add(m); };
        chat.ObserverStatus += s => { lock (statuses) statuses.Add(s); };
        chat.ViewersReceived += (src, n) => { lock (viewers) viewers.Add(new[] { src, string.Join(",", n) }); };
        chat.Note += n => { if (n.Length > 0) P("  note: " + n); };
        await chat.SetActive(true);
        var b = await Client();
        // the mock's scripted timeline runs ~12 s; wait for its final title
        string title = "";
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            title = await b.EvalAsync("chat", "document.title");
            if (title.Contains("posted") && title.Contains("openRes")) { await Task.Delay(1500); title = await b.EvalAsync("chat", "document.title"); if (title.Contains("sendResult")) break; }
        }
        var t = JsonSerializer.Deserialize<string>(title) ?? "";
        List<object> recv; lock (received) recv = received.Select(m => (object)new { user = m.User, text = m.Text, id = m.Id, isBot = m.IsBot, self = m.Self, room = m.Room }).ToList();
        File.WriteAllText(outFile, JsonSerializer.Serialize(new { page = JsonDocument.Parse(t.Length > 0 ? t : "{}").RootElement, received = recv, statuses, viewers }));
        P($"  page title {t.Length} chars, C# received {recv.Count} chat messages, {statuses.Count} statuses, {viewers.Count} viewer snapshots → {outFile}");
        await chat.SetActive(false);
        return t.Length > 0 ? 0 : 1;
    }


    /// <summary>v2.7: the real ChatPanel (tap registered first, observer, ChatFeedRouter) against the tap mock page and the fake
    /// local WebSocket server. Command room = Lobby while the UI shows Room B; the viewers panel is never opened. A reply sent
    /// while Room B is visible must be queued and posted after the UI switches to Lobby.</summary>
    static async Task<int> Tap(string mockDir, string outFile)
    {
        var srv = new System.Diagnostics.Process { StartInfo = new("node", $"\"{Path.Combine(mockDir, "ws-server.js")}\" 0 90") { RedirectStandardOutput = true, UseShellExecute = false } };
        srv.Start();
        try
        {
            var line = await srv.StandardOutput.ReadLineAsync() ?? ""; var port = Regex.Match(line, @"PORT (\d+)").Groups[1].Value;
            var html = File.ReadAllText(Path.Combine(mockDir, "epikchat-tap.html")).Replace("<script src=\"chat-observer.js\"></script>", "").Replace("<script src=\"chat-tap.js\"></script>", "")
                .Replace("const t0 = Date.now();", "const t0 = Date.now(); window.__holdMs = 4000;");
            var f = Tmp("tap.html", html);
            var received = new List<object>(); var viewers = new List<object>(); var statuses = new List<string>();
            var chat = new ChatPanel { ChatUrl = "file://" + f + "#port=" + port, DebugMessages = true, SelfName = "" };
            chat.Feed.S = new ChatFeedSettings { CommandRoom = "Lobby", ViewersRestFallback = false };
            chat.MessageReceived += m => { lock (received) received.Add(new { user = m.User, text = m.Text, id = m.Id, room = m.Room, isBot = m.IsBot, self = m.Self }); };
            chat.ViewersReceived += (src, n) => { lock (viewers) viewers.Add(new { source = src, names = n.ToList() }); };
            chat.ObserverStatus += s => { lock (statuses) statuses.Add(s); };
            await chat.SetActive(true);
            var b = await Client();
            string reply = "";
            for (int i = 0; i < 40 && chat.Feed.VisibleRoom != "Room B"; i++) await Task.Delay(250);
            reply = await chat.SendChat("Now playing: Test Song");          // command room Lobby is not visible → queued
            string title = "";
            for (int i = 0; i < 80 && !title.Contains("\\\"done\\\""); i++) { await Task.Delay(500); title = await b.EvalAsync("chat", "document.title"); }
            var t = JsonSerializer.Deserialize<string>(title) ?? "";
            await Task.Delay(1500);   // let the 1 s flush timer run
            var tapDiag = await chat.TapDiagnostics();
            object snap; lock (received) lock (viewers) lock (statuses)
                snap = new { page = JsonDocument.Parse(t.StartsWith("{") ? t : "{}").RootElement, received = received.ToList(), viewers = viewers.ToList(), statuses = statuses.ToList(), reply, queued = chat.QueuedReplies,
                    source = chat.Feed.SourceStatus(DateTime.Now), fromTap = chat.Feed.FromTap, fromDom = chat.Feed.FromDom, duplicates = chat.Feed.Duplicates, otherRoom = chat.Feed.OtherRoom, unmatched = chat.Feed.Unmatched, reconnects = chat.Feed.TapReconnects, tapDiag };
            File.WriteAllText(outFile, JsonSerializer.Serialize(snap));
            P($"  tap selftest: page {t.Length} chars, C# received {received.Count} messages, {viewers.Count} viewer snapshots, reply '{reply}' → {outFile}");
            await chat.SetActive(false);
            return t.StartsWith("{") ? 0 : 1;
        }
        finally { try { srv.Kill(true); } catch { } }
    }

    /// <summary>v2.5 Ball Pit: the member collector through the real ChatPanel/WebKitGTK host (virtualized list mock), plus the
    /// avatar-fetch fallback. Writes the page's own record (checked by members-check.py) and the C# side to outFile.</summary>
    static async Task<int> Members(string mockDir, string outFile)
    {
        var html = File.ReadAllText(Path.Combine(mockDir, "epikchat-members.html")).Replace("<script src=\"chat-observer.js\"></script>", "");
        var f = Tmp("members.html", html);
        var scans = new List<object>();
        var chat = new ChatPanel { ChatUrl = "file://" + f, DebugMessages = true, SelfName = "" };
        chat.MembersReceived += (l, complete, total, found) => { lock (scans) scans.Add(new { n = l.Count, complete, total, found, avatars = l.Count(m => !string.IsNullOrEmpty(m.Avatar)), colors = l.Count(m => !string.IsNullOrEmpty(m.Color)), m1 = l.FirstOrDefault(m => m.Name == "member001") }); };
        await chat.SetActive(true);
        var b = await Client();
        string title = "";
        for (int i = 0; i < 120 && !title.Contains("\"done\""); i++) { await Task.Delay(500); title = await b.EvalAsync("chat", "document.title"); }
        var png = await b.EvalAsync("chat", "window.__pngUrl");
        var bytes = await chat.FetchAvatarViaPage(JsonSerializer.Deserialize<string>(png) ?? "");
        var t = JsonSerializer.Deserialize<string>(title) ?? "";
        List<object> sc; lock (scans) sc = scans.ToList();
        File.WriteAllText(outFile + ".page.json", t);
        File.WriteAllText(outFile, JsonSerializer.Serialize(new { scans = sc, avatarBytes = bytes?.Length ?? -1, avatarIsPng = bytes is { Length: > 8 } && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G' }));
        P($"  page title {t.Length} chars, C# got {sc.Count} member scans, avatar fetch {bytes?.Length ?? -1} bytes → {outFile}");
        await chat.SetActive(false);
        return t.Contains("\"done\"") ? 0 : 1;
    }

    /// <summary>v2.4.1: LiveSession (the Start-button "Go live" automation) through the real ChatPanel/WebKitGTK host against the
    /// session mock, where the RTMP ingest is refused until a live session exists.</summary>
    static async Task<int> GoLive(string mockDir)
    {
        int fails = 0; void Check(bool ok, string what) { P((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
        var f = Tmp("session.html", File.ReadAllText(Path.Combine(mockDir, "epikchat-session.html")));
        const string Saved = "FAKE_mock_Key_12345abc";
        async Task<(ChatPanel chat, Func<string, string> ev)> Open(string q)
        {
            var chat = new ChatPanel { ChatUrl = "file://" + f + q };
            await chat.SetActive(true);
            var b = await Client();
            string Ev(string js) => b.EvalAsync("chat", js).GetAwaiter().GetResult();
            if (!await chat.WaitPageReady(20)) throw new Exception("mock page not ready");
            return (chat, Ev);
        }
        LiveSession Ls(ChatPanel c) { var l = c.NewLiveSession(); l.PollMs = 250; l.Status = (s, e) => P((e ? "  ! " : "  · ") + s); return l; }
        string Mask(string? k) => k is { Length: > 4 } ? "••••" + k[^4..] : "-";

        foreach (var aud in new[] { "Public", "Private" })
        {
            P($"-- not live, new key, audience {aud}");
            var (chat, Ev) = await Open("");
            Check(Ev("__mockIngest('" + Saved + "')").Contains("refused: no live session"), "ingest refused before a live session exists");
            var ls = Ls(chat);
            var r = await ls.EnsureAsync(aud, Saved, 60);
            P($"  outcome={r.Outcome} msg={r.Message} server={r.Server} key={Mask(r.Key)} changed={r.KeyChanged}");
            Check(r.Outcome == LiveSession.Outcome.Created, "session created");
            Check(Ev("__creates") == "1" && Ev("__golive") == "1", "camera clicked once, exactly one create");
            Check(Ev("__chosen").Contains("RTMP encoder") && Ev("__audience").Contains(aud), "RTMP encoder source + profile audience chosen");
            Check(r.Key == "FAKE_session1_Key_9z8y" && r.KeyChanged && r.Server == "rtmp://media.epikchat.com:1935/x", "new key read and flagged as changed");
            Check(Ev("__mockIngest('" + Saved + "')").Contains("wrong key"), "old saved key would be refused (so replacing it matters)");
            var fin = ls.FinalizeAsync(30);
            await Task.Delay(1200);
            Check(Ev("__mockIngest('" + r.Key + "')") == "\"accepted\"", "ingest accepted with the new key once the session exists");
            var fr = await fin; P("  finalize: " + fr);
            Check(Ev("__finalClicks") == "1" && Ev("__live") == "true" && Ev("!!window.__cancelled") == "false", "final confirm clicked once (never Cancel), page is live");
            var r2 = await ls.EnsureAsync(aud, r.Key, 20);
            Check(r2.Outcome == LiveSession.Outcome.AlreadyLive && Ev("__creates") == "1", "second Start: detected live, no duplicate session (" + r2.Message + ")");
            var dg = await chat.Diagnostics() ?? "";
            Check(dg.Contains("\"goLive\"") && dg.Contains("verified") && !dg.Contains("FAKE_"), "go-live selectors in chat diagnostics (no key)");
            await chat.SetActive(false);
        }

        P("-- not live, session key equals saved key");
        { var (chat, Ev) = await Open("?samekey=1"); var r = await Ls(chat).EnsureAsync("Public", Saved, 60);
          Check(r.Outcome == LiveSession.Outcome.Created && !r.KeyChanged && r.Key == Saved, "same key → kept, KeyChanged=false"); await chat.SetActive(false); }
        P("-- already live");
        { var (chat, Ev) = await Open("?live=1"); var r = await Ls(chat).EnsureAsync("Public", Saved, 20);
          Check(r.Outcome == LiveSession.Outcome.AlreadyLive && Ev("__golive") == "0" && Ev("__creates") == "0", "already live → nothing clicked (" + r.Message + ")"); await chat.SetActive(false); }
        foreach (var q in new[] { "?noaria=1", "?plain=1" })
        {
            P("-- icon-only camera button " + q);
            var (chat, Ev) = await Open(q); var r = await Ls(chat).EnsureAsync("Public", Saved, 60);
            Check(r.Outcome == LiveSession.Outcome.Created && Ev("__creates") == "1", "camera found without aria-label " + q + " (" + r.Message + ")");
            await chat.SetActive(false);
        }
        return fails;
    }

    static int Secret_()
    {
        int fails = 0; void Check(bool ok, string what) { P((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
        LinuxSecretStore.Install();
        const string fake = "FAKE_selftest_key_0000";
        var a = Secret.Protect(fake); var a2 = Secret.Protect(fake);
        P("  backend: " + LinuxSecretStore.Backend + "  stored form: " + (a?.Split(':')[0] ?? "null"));
        Check(a != null && !a.Contains(fake), "protected value does not contain the plaintext");
        Check(Secret.Unprotect(a) == fake, "round-trip");
        if (a!.StartsWith("ls:")) Check(a == a2, "same secret → same keyring item id (no keyring pile-up)");
        else Check(a != a2 && Secret.Unprotect(a2) == fake, "AES-GCM fallback uses a fresh nonce per save");
        Check(Secret.Unprotect("garbage") == "" || Secret.Unprotect("garbage") != fake, "garbage → not the secret");
        if (File.Exists(LinuxSecretStore.KeyFile))
        {
            var mode = File.GetUnixFileMode(LinuxSecretStore.KeyFile);
            Check(mode == (UnixFileMode.UserRead | UnixFileMode.UserWrite), $"fallback key file mode is 0600 ({mode})");
        }
        LinuxSecretStore.Forget(a);
        return fails;
    }
}
