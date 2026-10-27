using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace EpikStreamer;

/// <summary>
/// Side panel showing EpikChat's own /chat page in WebView2, using the same browser profile as "Sign in to EpikChat"
/// (so no second login). Guarantees a single chat page: there is only one panel, popups are opened in the system
/// browser instead of new WebView windows, and the page is unloaded while the sign-in window is open.
/// Viewer count / live status are read from the page by a MutationObserver (selectors in EpikChatAutomation, UNTESTED).
/// </summary>
public class ChatPanel : DockPanel
{
    public event Action<int?, bool>? StatusChanged;   // viewers (null = unknown), live
    public event Action<Core.ChatMessage>? MessageReceived;  // v2.2.2: new chat messages (real EpikChat DOM)
    public event Action<string>? ObserverStatus;              // "attached to Lobby, 44 rows", …
    /// <summary>Observer config, applied on the next page load (call <see cref="ReloadObserver"/>).</summary>
    public bool DebugMessages { get; set; }
    public string SelfName { get; set; } = "";
    public int ReplyMinIntervalSeconds { get; set; } = 5;

    // ---- v2.7 chat feed: DOM observer + passive network tap, merged by the router ----
    public Core.ChatFeedRouter Feed { get; } = new(new());
    Core.RoomReplyDispatcher? _replyDisp;
    /// <summary>2.8.1: every reply goes through one serialized dispatcher (switch to the origin room → verify → post → confirm via
    /// the feed → restore the user's tab), independent of the active tab and of whether this panel is on screen.</summary>
    Core.RoomReplyDispatcher Replies => _replyDisp ??= new Core.RoomReplyDispatcher(new PageOps(this), Feed) { AllowSwitch = () => Feed.S.AllowRoomSwitchToReply };
    sealed class PageOps : Core.RoomReplyDispatcher.IPage
    {
        readonly ChatPanel _p; public PageOps(ChatPanel p) => _p = p;
        public bool Ready => _p.IsLoaded2;
        public string VisibleRoom => _p.Feed.VisibleRoom;
        public Task<bool> ClickRoom(string room) => _p.Ui(() => _p.ClickRoom(room));
        public Task<string> SendNow(string text) => _p.Ui(() => _p.SendChatNow(text));
    }
    System.Threading.Timer? _feedTimer;
    void InitFeed()
    {
        Feed.Message += m => MessageReceived?.Invoke(m);
        Feed.Viewers += (s, n) => ViewersReceived?.Invoke(s, n);
        Replies.Status += s => ObserverStatus?.Invoke(s);
        _feedTimer = new(_ => { try { Feed.Flush(DateTime.Now); if (Replies.Count > 0) _ = Task.Run(Replies.Pump); } catch { } }, null, 1000, 1000);
    }
    void OnFeedStatus(string t)
    {
        ObserverStatus?.Invoke(t);
        if (t.StartsWith("attached to ") && Replies.Count > 0) _ = Task.Run(Replies.Pump);
    }
    public int QueuedReplies => Replies.Count;
    Task<T> Ui<T>(Func<Task<T>> f) => Dispatcher.CheckAccess() ? f() : Dispatcher.InvokeAsync(f).Task.Unwrap();
    /// <summary>Opt-in replies go to the command room (or the room the command came from in all-rooms mode). If that room isn't
    /// the one visible in the panel, the reply is queued until it is — or, with "allow switching room to reply", EpikChat briefly
    /// switches to it (clicks the room in its own room list) and back.</summary>
    /// <summary>Set by the main window: true while the 💬 Chat panel is actually on screen (switching rooms then would be visible).</summary>
    public Func<bool>? OnScreen { get; set; }
    /// <summary>Kept for callers; 2.8.1 no longer gates room switching on it.</summary>
    /// <summary>Posts <paramref name="text"/> in <paramref name="room"/> (the room the command came from); "" = the stream's room
    /// (else the visible one). Completes when delivered (and confirmed on the feed), failed, or expired.</summary>
    public async Task<string> SendChat(string text, string room = "")
    {
        if (room == Core.ChatFeedSettings.AllRooms) room = "";
        if (room.Length == 0) room = Feed.HomeRoom;
        var t = Replies.Enqueue(text, room);
        _ = Task.Run(Replies.Pump);
        return await t;
    }
    async Task<bool> ClickRoom(string name)
    {
        try
        {
            for (int i = 0; i < 3; i++)
            {   // 2.8.2: "revealed" = the page was narrow and we opened its collapsed room list; give it a moment and look again
                var r = JsonSerializer.Deserialize<string>(await EvalJs("window.__epikSwitchRoom ? window.__epikSwitchRoom(" + JsonSerializer.Serialize(name) + ") : 'no-helper'"));
                if (r != "revealed") return r == "clicked";
                await Task.Delay(700);
            }
            return false;
        }
        catch { return false; }
    }
    /// <summary>Sanitized tap diagnostics: event names + field names/types only (no text, names, tokens or cookies).</summary>
    public async Task<string?> TapDiagnostics()
    {
        if (!IsLoaded2) return null;
        try { return JsonSerializer.Deserialize<string>(await EvalJs("window.__epikTapDiag ? JSON.stringify(window.__epikTapDiag()) : 'null'")); } catch { return null; }
    }
    Task<string> EvalJs(string js) => _web.CoreWebView2.ExecuteScriptAsync(js);
    /// <summary>2.8.2: zoom out a narrow docked panel so EpikChat still sees ≥ 984 CSS px (its desktop layout with the room list).</summary>
    void FitZoom() { try { var z = Core.ChatLayout.Zoom(_web.ActualWidth); if (Math.Abs(_web.ZoomFactor - z) > 0.005) _web.ZoomFactor = z; } catch { } }
    string? _tapScriptId;
    string? _chatScriptId; DateTime _lastSend = DateTime.MinValue; readonly SemaphoreSlim _sendLock = new(1, 1);
    public bool IsLoaded2 => _initialized && _wantVisible && !WebViewHost.IsSignInOpen;
    readonly WebView2 _web = new();
    readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), Foreground = Brushes.Gray };
    // note: the panel inherits theme brushes from the window
    readonly CheckBox _compact = new() { Content = "Compact", Margin = new Thickness(6, 4, 6, 4) };
    string? _scriptId;
    bool _initialized, _wantVisible;
    public bool Compact { get => _compact.IsChecked == true; set => _compact.IsChecked = value; }
    readonly CheckBox _reply = new() { Content = "↩ Reply in chat", Margin = new Thickness(6, 4, 6, 4), ToolTip = "Reply to chat commands in chat (posts as you, in the room the command came from)" };
    public event Action<bool>? ReplyToggled;
    public bool ReplyInChat { get => _reply.IsChecked == true; set => _reply.IsChecked = value; }
    public bool IsInitialized2 => _initialized;

    public ChatPanel()
    {
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var reload = new Button { Content = "⟳", ToolTip = "Reload chat", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(2) };
        reload.Click += (_, _) => { if (_initialized && !WebViewHost.IsSignInOpen) _web.CoreWebView2.Navigate(EpikChatAutomation.ChatUrl); };
        _compact.Checked += async (_, _) => await ApplyCompact();
        _compact.Unchecked += async (_, _) => await ApplyCompact();
        DockPanel.SetDock(reload, Dock.Right);
        bar.Children.Add(reload);
        bar.Children.Add(new TextBlock { Text = "EpikChat", FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 4, 6, 4) });
        bar.Children.Add(_compact);
        _reply.Click += (_, _) => ReplyToggled?.Invoke(_reply.IsChecked == true);
        bar.Children.Add(_reply);
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(_note, Dock.Top);
        Children.Add(bar); Children.Add(_note); Children.Add(_web);
        WebViewHost.SignInActive += OnSignIn;
        InitFeed();
    }

    /// <summary>Show/hide. Hiding navigates to about:blank so the chat session is actually closed, not just invisible.</summary>
    public async Task SetActive(bool on)
    {
        _wantVisible = on;
        if (!on) { if (_initialized) _web.CoreWebView2.Navigate("about:blank"); StatusChanged?.Invoke(null, false); return; }
        if (WebViewHost.IsSignInOpen) { _note.Text = "Chat is paused while the sign-in window is open (EpikChat allows one session)."; return; }
        try
        {
            if (!_initialized)
            {
                await _web.EnsureCoreWebView2Async(await WebViewHost.GetEnvironmentAsync());
                FitZoom(); _web.SizeChanged += (_, _) => FitZoom();
                var core = _web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.NewWindowRequested += (_, e) => { e.Handled = true; WebViewHost.OpenExternal(e.Uri); };   // never a second WebView window
                core.NavigationStarting += (_, e) =>
                {
                    if (e.Uri == "about:blank" || WebViewHost.IsEpikChatUrl(e.Uri)) return;
                    e.Cancel = true; WebViewHost.OpenExternal(e.Uri);
                };
                core.WebMessageReceived += OnMessage;
                await RegisterScript();
                _initialized = true;
            }
            _note.Text = "";
            if (_web.Source == null || !WebViewHost.IsEpikChatUrl(_web.Source.ToString())) _web.CoreWebView2.Navigate(EpikChatAutomation.ChatUrl);
        }
        catch (WebView2RuntimeNotFoundException) { _note.Text = "Microsoft Edge WebView2 Runtime is not installed."; }
        catch (Exception ex) { _note.Text = "Chat failed to load: " + ex.Message; }
    }

    async Task RegisterScript()
    {
        var core = _web.CoreWebView2;
        // the tap must be registered first so it wraps WebSocket before EpikChat's own code runs
        if (_tapScriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(_tapScriptId);
        if (_scriptId != null) { core.RemoveScriptToExecuteOnDocumentCreated(_scriptId); _scriptId = null; }
        if (_chatScriptId != null) { core.RemoveScriptToExecuteOnDocumentCreated(_chatScriptId); _chatScriptId = null; }
        _tapScriptId = Feed.S.LiveFeed ? await core.AddScriptToExecuteOnDocumentCreatedAsync(EpikChatAutomation.ChatTapJs(Feed.S)) : null;
        _scriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(EpikChatAutomation.ObserverJs(Compact));
        if (_chatScriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(_chatScriptId);
        _chatScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(EpikChatAutomation.ChatObserverJs(DebugMessages, SelfName));
    }

    async Task ApplyCompact()
    {
        if (!_initialized) return;
        await RegisterScript();   // for future loads
        try { await _web.CoreWebView2.ExecuteScriptAsync($"window.__epikSetCompact && window.__epikSetCompact({(Compact ? "true" : "false")})"); } catch { }
    }

    void OnMessage(object? s, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!WebViewHost.IsEpikChatUrl(e.Source)) return;     // only trust messages from EpikChat pages
        try
        {
            using var d = JsonDocument.Parse(e.WebMessageAsJson);
            var r = d.RootElement;
            var type = r.GetProperty("type").GetString();
            if (Core.ChatFeedBridge.Handle(r, type ?? "", Feed, DateTime.Now, OnFeedStatus)) return;
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
            if (type == "epik-members")   // v2.5 Ball Pit roster
            {
                var list = new List<Core.BallPitRoster.Member>();
                if (r.TryGetProperty("members", out var ma) && ma.ValueKind == JsonValueKind.Array)
                    foreach (var m in ma.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;
                        string S2(string k) => m.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
                        var nm = S2("name").Trim(); if (nm.Length is 0 or > 64) continue;
                        var av = S2("avatar"); if (!(av.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || av.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) || av.Length > 2000) av = "";
                        var col = S2("color"); if (col.Length > 40) col = "";
                        list.Add(new(nm, av.Length > 0 ? av : null, col.Length > 0 ? col : null));
                    }
                int total = r.TryGetProperty("total", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : 0;
                MembersReceived?.Invoke(list, Bool("complete"), total, Bool("found"));
                return;
            }
            if (type == "epik-avatar")
            {
                var aurl = Str("url"); var adata = Str("data");
                lock (_avWait) if (_avWait.Remove(aurl, out var atcs)) atcs.TrySetResult(adata.StartsWith("data:image/", StringComparison.Ordinal) ? adata : null);
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
        if (open) { _web.CoreWebView2.Navigate("about:blank"); _note.Text = "Chat is paused while the sign-in window is open (EpikChat allows one session)."; StatusChanged?.Invoke(null, false); }
        else if (_wantVisible) _ = SetActive(true);
    }

    /// <summary>Re-inject the observer with the current config (debug / username) and reload the chat page.</summary>
    public async Task ReloadObserver()
    {
        if (!_initialized) return;
        await RegisterScript();
        if (IsLoaded2) _web.CoreWebView2.Navigate(EpikChatAutomation.ChatUrl);
    }

    /// <summary>Opt-in ("Reply in chat", off by default): types <paramref name="text"/> into EpikChat's normal composer as the
    /// signed-in user and presses Enter. Rate-limited, max 250 chars. Prefers DevTools Input.insertText + Enter (real input
    /// events React accepts), falling back to the page-side helper (native value setter + input event + form.requestSubmit).</summary>
    /// <summary>v2.3: (source, names) from the LIVE Viewers panel or the Room members list.</summary>
    public event Action<string, List<string>>? ViewersReceived;

    /// <summary>v2.3: open a user's profile by clicking their name in EpikChat's own list (normal UI only, no moderation).</summary>
    public async Task<string> OpenProfile(string name)
    {
        if (!IsLoaded2) return "chat not open";
        try
        {
            var r = await _web.CoreWebView2.ExecuteScriptAsync($"window.__epikOpenProfile ? window.__epikOpenProfile({JsonSerializer.Serialize(name)}) : 'no-helper'");
            return JsonSerializer.Deserialize<string>(r) ?? "?";
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    public async Task<string> SendChatNow(string text)
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
            var core = _web.CoreWebView2; var js = JsonSerializer.Serialize(text);
            try
            {
                var f = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("window.__epikFocusComposer ? window.__epikFocusComposer() : 'no-helper'"));
                if (f == "ok")
                {
                    await core.ExecuteScriptAsync($"window.__epikNoteSent({js})");
                    await core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text }));
                    var v = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("window.__epikComposerValue()"));
                    if (v == text)
                    {
                        // like Puppeteer's press('Enter'): keyDown carrying "\r" (fires keypress + implicit form submit), then keyUp
                        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type = "keyDown", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, text = "\r", unmodifiedText = "\r" }));
                        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13 }));
                        await Task.Delay(400);
                        var after = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("window.__epikComposerValue()"));
                        if (after != text) return "sent (devtools)";
                    }
                }
            }
            catch { /* fall through to the page helper */ }
            var r = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("window.__epikSend ? window.__epikSend(" + js + ") : 'no-helper'"));
            return r is "submitted" or "enter" ? "sent (" + r + ")" : "not sent: " + r;
        }
        catch (Exception ex) { return "not sent: " + ex.Message; }
        finally { _sendLock.Release(); }
    }

    /// <summary>v2.4.1: a Go-live automation bound to this panel's page (UI thread only).</summary>
    public LiveSession NewLiveSession() => new(js => IsLoaded2 ? _web.CoreWebView2.ExecuteScriptAsync(js) : Task.FromResult("null"));

    /// <summary>v2.4.1: waits until the chat page has loaded and rendered (or the timeout).</summary>
    public async Task<bool> WaitPageReady(int timeoutSec)
    {
        var end = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < end)
        {
            if (IsLoaded2 && _web.Source != null && WebViewHost.IsEpikChatUrl(_web.Source.ToString()))
            {
                try { if (await _web.CoreWebView2.ExecuteScriptAsync("document.readyState") == "\"complete\"") { await Task.Delay(1500); return true; } } catch { }
            }
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>Sanitized structure of the chat DOM + observer state (no message text, no names), for debugging selectors.</summary>
    public async Task<string?> Diagnostics()
    {
        if (!IsLoaded2) return null;
        var r = await _web.CoreWebView2.ExecuteScriptAsync("window.__epikDiag ? window.__epikDiag() : 'null'");
        string gl; try { await _web.CoreWebView2.ExecuteScriptAsync(EpikChatAutomation.HelperJs + "\n;0"); gl = await _web.CoreWebView2.ExecuteScriptAsync("window.__epik.goLiveDiag()"); } catch (Exception ex) { gl = JsonSerializer.Serialize(new { error = ex.Message }); }
        return EpikChatAutomation.MergeGoLiveDiag(JsonSerializer.Deserialize<string>(r), gl);
    }

    /// <summary>Sign out: clears cookies/storage of the shared profile (works while the environment is in use).</summary>
    public async Task<bool> ClearBrowsingData()
    {
        if (!_initialized) return false;
        _web.CoreWebView2.Navigate("about:blank");
        await _web.CoreWebView2.Profile.ClearBrowsingDataAsync();
        if (_wantVisible) _web.CoreWebView2.Navigate(EpikChatAutomation.ChatUrl);
        return true;
    }

    // ---- v2.5 Ball Pit ----
    /// <summary>(members, complete, total shown by the page, list found)</summary>
    public event Action<List<Core.BallPitRoster.Member>, bool, int, bool>? MembersReceived;
    readonly Dictionary<string, TaskCompletionSource<string?>> _avWait = new();
    /// <summary>Ask the observer to read the Room members list (full = scroll the virtualized list top→bottom, then restore).</summary>
    public async Task ScanMembers(bool full)
    {
        if (!IsLoaded2) return;
        try { await _web.CoreWebView2.ExecuteScriptAsync($"window.__epikMembers ? (window.__epikMembers({(full ? "true" : "false")}), 'started') : 'no-helper'"); } catch { }
    }
    /// <summary>Avatar fallback: the page re-fetches an image it already shows and returns it as a data: URL (no other API calls).</summary>
    public async Task<byte[]?> FetchAvatarViaPage(string url)
    {
        if (!IsLoaded2) return null;
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_avWait) { if (_avWait.Count > 50) return null; _avWait[url] = tcs; }
        try { await _web.CoreWebView2.ExecuteScriptAsync("window.__epikFetchAvatar ? (window.__epikFetchAvatar(" + JsonSerializer.Serialize(url) + "), 'started') : 'no-helper'"); } catch { lock (_avWait) _avWait.Remove(url); return null; }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(10000));
        lock (_avWait) _avWait.Remove(url);
        if (done != tcs.Task || tcs.Task.Result is not { } d) return null;
        var i = d.IndexOf(";base64,", StringComparison.Ordinal);
        try { return i > 0 ? Convert.FromBase64String(d[(i + 8)..]) : null; } catch { return null; }
    }
}
