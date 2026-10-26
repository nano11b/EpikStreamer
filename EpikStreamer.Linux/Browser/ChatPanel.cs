using System.Text.Json;
using EpikStreamer.Browser;
namespace EpikStreamer;

/// <summary>
/// EpikChat's own /chat page in a WebKitGTK window (the "chat" view of the shared browser host, same session as sign-in).
/// Same contract as the Windows panel: one chat page only, unloaded while the sign-in window is open, popups go to the system
/// browser, and the observer scripts post epik-status / epik-chat / epik-viewers messages back.
/// </summary>
public class ChatPanel
{
    const string View = "chat";
    public event Action<int?, bool>? StatusChanged;
    public event Action<Core.ChatMessage>? MessageReceived;
    public event Action<string>? ObserverStatus;
    public event Action<string, List<string>>? ViewersReceived;
    /// <summary>The user closed the chat window (the main window un-toggles 💬).</summary>
    public event Action? UserClosed;
    /// <summary>Informational note for the UI ("paused while signing in", "WebKitGTK missing", …).</summary>
    public event Action<string>? Note;
    public bool DebugMessages { get; set; }
    public string SelfName { get; set; } = "";
    public int ReplyMinIntervalSeconds { get; set; } = 5;
    public string ChatUrl { get; set; } = EpikChatAutomation.ChatUrl;
    DateTime _lastSend = DateTime.MinValue; readonly SemaphoreSlim _sendLock = new(1, 1);
    BrowserClient? _b; bool _initialized, _wantVisible, _compact; string _url = "";
    public bool IsInitialized2 => _initialized;
    public bool IsLoaded2 => _initialized && _wantVisible && !WebViewHost.IsSignInOpen && _b is { HasExited: false };
    public bool Compact { get => _compact; set { if (_compact == value) return; _compact = value; _ = ApplyCompact(); } }

    public ChatPanel() { WebViewHost.SignInActive += OnSignIn; }

    public async Task SetActive(bool on)
    {
        _wantVisible = on;
        if (!on) { if (_initialized) { _b!.Navigate(View, "about:blank"); _b.Hide(View); } StatusChanged?.Invoke(null, false); return; }
        if (WebViewHost.IsSignInOpen) { Note?.Invoke("Chat is paused while the sign-in window is open (EpikChat allows one session)."); return; }
        try
        {
            if (!_initialized || _b is not { HasExited: false })
            {
                _b = await BrowserClient.GetAsync();
                if (_b == null) { Note?.Invoke("Chat needs WebKitGTK 4.1 (libwebkit2gtk-4.1-0): " + BrowserClient.Unavailable + ". Chat commands, viewer list and alerts are disabled."); return; }
                _b.Message += OnMessage;
                _b.Loaded += (v, u) => { if (v == View) _url = u; };
                _b.Closed += v => { if (v == View && _wantVisible) { _wantVisible = false; _b.Navigate(View, "about:blank"); StatusChanged?.Invoke(null, false); UserClosed?.Invoke(); } };
                _b.Open(View, "EpikChat — chat (EpikStreamer)", 440, 820);
                RegisterScript();
                _initialized = true;
            }
            else _b.Show(View);
            Note?.Invoke("");
            if (!WebViewHost.IsEpikChatUrl(_url)) { _url = ChatUrl; _b.Navigate(View, ChatUrl); }
        }
        catch (Exception ex) { Note?.Invoke("Chat failed to load: " + ex.Message); }
    }

    void RegisterScript()
    {
        _b!.ClearScripts(View);
        _b.AddScript(View, EpikChatAutomation.ObserverJs(Compact));
        _b.AddScript(View, EpikChatAutomation.ChatObserverJs(DebugMessages, SelfName));
    }

    async Task ApplyCompact()
    {
        if (!_initialized) return;
        RegisterScript();
        try { await _b!.EvalAsync(View, $"window.__epikSetCompact && window.__epikSetCompact({(Compact ? "true" : "false")})"); } catch { }
    }

    void OnMessage(string view, string url, string data)
    {
        if (view != View || !WebViewHost.IsEpikChatUrl(url)) return;   // only trust messages from EpikChat pages
        try
        {
            using var d = JsonDocument.Parse(data);
            var r = d.RootElement;
            var type = r.GetProperty("type").GetString();
            string Str(string k) => r.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
            bool Bool(string k) => r.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.True;
            if (type == "epik-chat")
            {
                var text = Str("text"); var id = Str("id");
                if (text.Length > 0) MessageReceived?.Invoke(new Core.ChatMessage(Str("user"), text, id.Length > 0 ? id : null, Bool("isBot"), Bool("self"), Str("room"), Str("time")));
                return;
            }
            if (type == "epik-chat-status") { ObserverStatus?.Invoke(Str("text")); return; }
            if (type == "epik-viewers")
            {
                var names = new List<string>();
                if (r.TryGetProperty("names", out var na) && na.ValueKind == JsonValueKind.Array)
                    foreach (var n in na.EnumerateArray()) if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 and <= 64 } nm) names.Add(nm);
                ViewersReceived?.Invoke(Str("source"), names);
                return;
            }
            if (type != "epik-status") return;
            int? viewers = r.TryGetProperty("viewers", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            bool live = r.TryGetProperty("live", out var l) && l.ValueKind == JsonValueKind.True;
            StatusChanged?.Invoke(viewers, live);
        }
        catch { }
    }

    void OnSignIn(bool open)
    {
        if (!_initialized) return;
        if (open) { _b!.Navigate(View, "about:blank"); _url = ""; _b.Hide(View); Note?.Invoke("Chat is paused while the sign-in window is open (EpikChat allows one session)."); StatusChanged?.Invoke(null, false); }
        else if (_wantVisible) _ = SetActive(true);
    }

    public async Task ReloadObserver()
    {
        if (!_initialized) return;
        RegisterScript();
        if (IsLoaded2) { _b!.Navigate(View, ChatUrl); _url = ChatUrl; }
        await Task.CompletedTask;
    }

    public async Task<string> OpenProfile(string name)
    {
        if (!IsLoaded2) return "chat not open";
        try
        {
            var r = await _b!.EvalAsync(View, $"window.__epikOpenProfile ? window.__epikOpenProfile({JsonSerializer.Serialize(name)}) : 'no-helper'");
            _b.Show(View);
            return JsonSerializer.Deserialize<string>(r) ?? "?";
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    /// <summary>Opt-in ("Reply in chat", off by default): types into EpikChat's own composer as the signed-in user via the page-side
    /// helper (native value setter + input event + form.requestSubmit). Rate-limited, max 250 chars. (WebKitGTK has no DevTools
    /// input API, so the Windows insertText path isn't available here.)</summary>
    public async Task<string> SendChat(string text)
    {
        if (!IsLoaded2) return "chat not open";
        text = text.Replace('\n', ' ').Trim(); if (text.Length > 250) text = text[..250];
        if (text.Length == 0) return "empty";
        await _sendLock.WaitAsync();
        try
        {
            var wait = _lastSend.AddSeconds(Math.Max(1, ReplyMinIntervalSeconds)) - DateTime.Now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            _lastSend = DateTime.Now;
            var r = JsonSerializer.Deserialize<string>(await _b!.EvalAsync(View, "window.__epikSend ? window.__epikSend(" + JsonSerializer.Serialize(text) + ") : 'no-helper'"));
            return r is "submitted" or "enter" ? "sent (" + r + ")" : "not sent: " + r;
        }
        catch (Exception ex) { return "not sent: " + ex.Message; }
        finally { _sendLock.Release(); }
    }

    /// <summary>v2.4.1: a Go-live automation bound to this panel's page.</summary>
    public LiveSession NewLiveSession() => new(js => IsLoaded2 ? _b!.EvalAsync(View, js) : Task.FromResult("null"));

    /// <summary>v2.4.1: waits until the chat page has loaded and rendered (or the timeout).</summary>
    public async Task<bool> WaitPageReady(int timeoutSec)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < end)
        {
            if (IsLoaded2 && WebViewHost.IsEpikChatUrl(_url))
            {
                try { if (await _b!.EvalAsync(View, "document.readyState", 5000) == "\"complete\"") { await Task.Delay(1500); return true; } } catch { }
            }
            await Task.Delay(500);
        }
        return false;
    }

    public async Task<string?> Diagnostics()
    {
        if (!IsLoaded2) return null;
        var r = await _b!.EvalAsync(View, "window.__epikDiag ? window.__epikDiag() : 'null'");
        string gl; try { await _b.EvalAsync(View, EpikChatAutomation.HelperJs + "\n;0"); gl = await _b.EvalAsync(View, "window.__epik.goLiveDiag()"); } catch (Exception ex) { gl = JsonSerializer.Serialize(new { error = ex.Message }); }
        return EpikChatAutomation.MergeGoLiveDiag(JsonSerializer.Deserialize<string>(r), gl);
    }

    public async Task<bool> ClearBrowsingData()
    {
        if (!_initialized) return false;
        _b!.Navigate(View, "about:blank");
        await _b.ClearDataAsync();
        if (_wantVisible) _b.Navigate(View, ChatUrl);
        return true;
    }
}
