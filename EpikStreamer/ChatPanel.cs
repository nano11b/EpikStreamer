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
    readonly WebView2 _web = new();
    readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), Foreground = Brushes.Gray };
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
            if (r.GetProperty("type").GetString() != "epik-status") return;
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
