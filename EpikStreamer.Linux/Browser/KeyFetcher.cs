using System.Text.Json;
using EpikStreamer.Browser;
namespace EpikStreamer;

/// <summary>
/// The "get stream key" automation (same steps as the Windows SignInWindow): drives EpikChat's own Go-live UI in the signed-in
/// WebKit view via EpikChatAutomation.HelperJs and reads the RTMP server + key from the page. Never sees the password.
/// </summary>
public sealed class KeyFetcher
{
    readonly BrowserClient _b; readonly string _view;
    public Action<string, bool> Status { get; set; } = (_, _) => { };
    public Func<Task<bool>> ConfirmSession { get; set; } = () => Task.FromResult(true);
    public string ChatUrl { get; set; } = EpikChatAutomation.ChatUrl;
    public int SettleMs { get; set; } = 2500;
    public bool Manual { get; private set; }
    public string? ServerUrl { get; private set; }
    public string? StreamKey { get; private set; }

    public KeyFetcher(BrowserClient b, string view) { _b = b; _view = view; }

    async Task<JsonElement> Js(string script)
    {
        var raw = await _b.EvalAsync(_view, script);
        return JsonDocument.Parse(string.IsNullOrEmpty(raw) ? "null" : raw).RootElement.Clone();
    }
    Task EnsureHelper() => Js(EpikChatAutomation.HelperJs + "\n;0");
    static string JsArr(string[] a) => JsonSerializer.Serialize(a);

    async Task<bool> TryClick(string[] patterns, int timeoutSec)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < end)
        {
            await EnsureHelper();
            var r = await Js($"window.__epik.click({JsArr(patterns)})");
            if (r.ValueKind == JsonValueKind.Object && r.GetProperty("ok").GetBoolean()) return true;
            await Task.Delay(500);
        }
        return false;
    }

    async Task WaitForNavigation(string url)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void H(string v, string _) { if (v == _view) tcs.TrySetResult(); }
        _b.Loaded += H;
        _b.Navigate(_view, url);
        await Task.WhenAny(tcs.Task, Task.Delay(30000));
        _b.Loaded -= H;
        await Task.Delay(SettleMs);   // let the single-page app render
    }

    /// <summary>Returns true when <see cref="StreamKey"/> was read.</summary>
    public async Task<bool> RunAsync(string audience)
    {
        if (Manual) return await ReadKey();
        await EnsureHelper();
        var li = await Js("window.__epik.loggedIn()");
        if (li.ValueKind == JsonValueKind.False) { Status("You don't seem to be signed in yet. Sign in on the page first.", true); return false; }
        if (!await ConfirmSession()) return false;

        Status("Opening EpikChat chat…", false);
        await WaitForNavigation(ChatUrl);
        await TryClick(new[] { "move chat here", "use here", "continue here" }, 3);   // EpikChat's own "chatting elsewhere" button
        Status("Dismissing popups…", false);
        for (int i = 0; i < 4; i++) { if (!await TryClick(EpikChatAutomation.DismissPatterns, 2)) break; await Task.Delay(400); }

        var steps = EpikChatAutomation.Steps(audience);
        for (int i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            string tag = $"Step {i + 1}/{steps.Length}: {s.Name}";
            Status(tag + "…", false);
            var end = DateTime.UtcNow.AddSeconds(s.TimeoutSec);
            string state = "", info = "", dlg = "", lastShown = "";
            while (DateTime.UtcNow < end)
            {
                await EnsureHelper();
                var r = await Js($"window.__epik.step({JsonSerializer.Serialize(s.Id)}, {JsonSerializer.Serialize(s.Arg)})");
                if (r.ValueKind != JsonValueKind.Object) { await Task.Delay(500); continue; }
                state = r.GetProperty("state").GetString() ?? ""; info = r.GetProperty("info").GetString() ?? ""; dlg = r.GetProperty("dialog").GetString() ?? "";
                var line = $"{tag}: {state}{(info.Length > 0 ? " (" + info + ")" : "")} · dialog: {(dlg.Length > 0 ? "\"" + dlg + "\"" : "none")}";
                if (line != lastShown) { Status(line, false); lastShown = line; }
                if (state == "done") break;
                await Task.Delay(500);
            }
            if (state != "done")
            {
                if (s.Optional) continue;
                Status($"{tag} failed: {state} {info} (dialog seen: {(dlg.Length > 0 ? dlg : "none")}). Finish the remaining steps by hand in the EpikChat window, then click \"Read key from page\". " +
                       "If EpikChat changed its page, update EpikChatAutomation.cs.", true);
                Manual = true;
                return false;
            }
        }
        return await ReadKey();
    }

    async Task<bool> ReadKey()
    {
        Status("Reading server URL and stream key…", false);
        for (int i = 0; i < 20; i++)
        {
            await EnsureHelper();
            var r = await Js("window.__epik.read()");
            var server = r.ValueKind == JsonValueKind.Object ? r.GetProperty("server").GetString() ?? "" : "";
            var key = r.ValueKind == JsonValueKind.Object ? r.GetProperty("key").GetString() ?? "" : "";
            if (key.Length > 0)
            {
                ServerUrl = string.IsNullOrEmpty(server) ? EpikChatAutomation.ExpectedServer : server;
                StreamKey = key;
                Status($"Got it. Server: {ServerUrl}  Key: ••••{(key.Length > 4 ? key[^4..] : "")}. Filled into the current profile.", false);
                return true;
            }
            if (i % 4 == 3) await Js("window.__epik.step('showkey', '')");
            await Task.Delay(1000);
        }
        Status("Couldn't find the stream key on the page. Make sure the RTMP stream was created and the key is shown (click \"Show stream key\"), then try again.", true);
        return false;
    }
}
