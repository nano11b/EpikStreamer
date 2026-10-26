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
    string? _chatScriptId; DateTime _lastSend = DateTime.MinValue; readonly SemaphoreSlim _sendLock = new(1, 1);
    public bool IsLoaded2 => _initialized && _wantVisible && !WebViewHost.IsSignInOpen;
    readonly WebView2 _web = new();
    readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), Foreground = Brushes.Gray };
    // note: the panel inherits theme brushes from the window
    readonly CheckBox _compact = new() { Content = "Compact", Margin = new Thickness(6, 4, 6, 4) };
    string? _scriptId;
    bool _initialized, _wantVisible;
    public bool Compact { get => _compact.IsChecked == true; set => _compact.IsChecked = value; }
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
        DockPanel.SetDock(bar, Dock.Top); DockPanel.SetDock(_note, Dock.Top);
        Children.Add(bar); Children.Add(_note); Children.Add(_web);
        WebViewHost.SignInActive += OnSignIn;
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
        if (_scriptId != null) core.RemoveScriptToExecuteOnDocumentCreated(_scriptId);
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

    /// <summary>Sanitized structure of the chat DOM + observer state (no message text, no names), for debugging selectors.</summary>
    public async Task<string?> Diagnostics()
    {
        if (!IsLoaded2) return null;
        var r = await _web.CoreWebView2.ExecuteScriptAsync("window.__epikDiag ? window.__epikDiag() : 'null'");
        return JsonSerializer.Deserialize<string>(r);
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
}
