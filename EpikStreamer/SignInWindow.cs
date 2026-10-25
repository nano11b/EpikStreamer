using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EpikStreamer.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace EpikStreamer;

/// <summary>
/// Shows EpikChat's real login page in WebView2. The user signs in themselves (the app never sees the password).
/// Then it drives EpikChat's own "Go live" UI to create an RTMP stream and reads the server URL + key from the page.
/// </summary>
public class SignInWindow : Window
{
    public static string UserDataDir => WebViewHost.UserDataDir;
    public string? ServerUrl { get; private set; }
    public string? StreamKey { get; private set; }

    readonly WebView2 _web = new();
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
    readonly Button _getKey = new() { Content = "I'm signed in – get stream key" };
    readonly Button _signOut;
    readonly string _audience;

    public SignInWindow(Window owner, string audience)
    {
        _audience = audience;
        Owner = owner; Title = "Sign in to EpikChat"; Width = 1100; Height = 800; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bg");
        _signOut = new Button { Content = "Sign out", Style = (Style)owner.FindResource("Secondary") };
        var close = new Button { Content = "Close", Style = (Style)owner.FindResource("Secondary"), IsCancel = true };
        _getKey.Click += async (_, _) => await RunAutomation();
        _signOut.Click += async (_, _) => await SignOut();
        close.Click += (_, _) => Close();
        _getKey.IsEnabled = false;
        var bar = new DockPanel { Margin = new Thickness(10) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Children = { _getKey, _signOut, close } };
        DockPanel.SetDock(btns, Dock.Right);
        bar.Children.Add(btns); bar.Children.Add(_status);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar); root.Children.Add(_web);
        Content = root;
        // only one EpikChat page at a time: the chat panel unloads while this window is open
        WebViewHost.SetSignIn(true);
        Closed += (_, _) => WebViewHost.SetSignIn(false);
        Loaded += async (_, _) => await Init();
    }

    void Status(string s, bool error = false)
    {
        _status.Text = s;
        try   // step-by-step trace for debugging the page automation (never contains the key: it is masked below)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpikStreamer");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "signin.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(error ? "ERR " : "")}{s}{Environment.NewLine}");
        }
        catch { }
        _status.Foreground = error ? Brushes.OrangeRed : (Brush)FindResource("Fg");
    }

    async Task Init()
    {
        try
        {
            var env = await WebViewHost.GetEnvironmentAsync();
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.NavigationCompleted += (_, _) => _getKey.IsEnabled = true;
            _web.CoreWebView2.Navigate(EpikChatAutomation.LoginUrl);
            Status("Sign in on the EpikChat page below (if you're already signed in, it may skip ahead). Then click \"get stream key\".");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Status("Microsoft Edge WebView2 Runtime not found. It ships with Windows 10/11; install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703", true);
        }
        catch (Exception ex) { Status("Could not start the browser: " + ex.Message, true); }
    }

    async Task<JsonElement> Js(string script)
    {
        var raw = await _web.CoreWebView2.ExecuteScriptAsync(script);
        return JsonDocument.Parse(string.IsNullOrEmpty(raw) ? "null" : raw).RootElement.Clone();
    }
    async Task EnsureHelper() => await Js(EpikChatAutomation.HelperJs);
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
        var tcs = new TaskCompletionSource();
        void H(object? s, CoreWebView2NavigationCompletedEventArgs e) => tcs.TrySetResult();
        _web.CoreWebView2.NavigationCompleted += H;
        _web.CoreWebView2.Navigate(url);
        await Task.WhenAny(tcs.Task, Task.Delay(30000));
        _web.CoreWebView2.NavigationCompleted -= H;
        await Task.Delay(2500); // let the single-page app render
    }

    async Task RunAutomation()
    {
        _getKey.IsEnabled = false;
        try
        {
            if (_manual) { await ReadKey(); return; }
            await EnsureHelper();
            var li = await Js("window.__epik.loggedIn()");
            if (li.ValueKind == JsonValueKind.False) { Status("You don't seem to be signed in yet. Sign in on the page first.", true); return; }

            if (MessageBox.Show(this, "EpikChat allows one chat session per account and limits logins per IP address.\n\n" +
                "Opening the chat here may take over (disconnect) a chat session you have open in another browser or device. Continue?",
                "EpikChat session", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;

            Status("Opening EpikChat chat…");
            await WaitForNavigation(EpikChatAutomation.ChatUrl);

            // If EpikChat says the account is chatting elsewhere, use its own "move chat here" button.
            await TryClick(new[] { "move chat here", "use here", "continue here" }, 3);

            Status("Dismissing popups…");
            for (int i = 0; i < 4; i++) { if (!await TryClick(EpikChatAutomation.DismissPatterns, 2)) break; await Task.Delay(400); }

            var steps = EpikChatAutomation.Steps(_audience);
            for (int i = 0; i < steps.Length; i++)
            {
                var s = steps[i];
                string tag = $"Step {i + 1}/{steps.Length}: {s.Name}";
                Status(tag + "…");
                var end = DateTime.UtcNow.AddSeconds(s.TimeoutSec);
                string state = "", info = "", dlg = "", lastShown = "";
                while (DateTime.UtcNow < end)
                {
                    await EnsureHelper();
                    var r = await Js($"window.__epik.step({JsonSerializer.Serialize(s.Id)}, {JsonSerializer.Serialize(s.Arg)})");
                    if (r.ValueKind != JsonValueKind.Object) { await Task.Delay(500); continue; }
                    state = r.GetProperty("state").GetString() ?? ""; info = r.GetProperty("info").GetString() ?? ""; dlg = r.GetProperty("dialog").GetString() ?? "";
                    var line = $"{tag}: {state}{(info.Length > 0 ? " (" + info + ")" : "")} · dialog: {(dlg.Length > 0 ? "\"" + dlg + "\"" : "none")}";
                    if (line != lastShown) { Status(line); lastShown = line; }
                    if (state == "done") break;
                    await Task.Delay(500);
                }
                if (state != "done")
                {
                    if (s.Optional) continue;
                    Status($"{tag} failed: {state} {info} (dialog seen: {(dlg.Length > 0 ? dlg : "none")}). Finish the remaining steps by hand in the page, then click \"Read key from page\". " +
                           "If EpikChat changed its page, update EpikChatAutomation.cs.", true);
                    _getKey.Content = "Read key from page"; _manual = true;
                    return;
                }
            }
            await ReadKey();
        }
        catch (Exception ex) { Status("Automation error: " + ex.Message, true); }
        finally { _getKey.IsEnabled = true; }
    }

    bool _manual; // after a failed step: the button just reads the key from the page

    async Task ReadKey()
    {
        Status("Reading server URL and stream key…");
        for (int i = 0; i < 20; i++)
        {
            await EnsureHelper();
            var r = await Js("window.__epik.read()");
            var server = r.GetProperty("server").GetString() ?? "";
            var key = r.GetProperty("key").GetString() ?? "";
            if (key.Length > 0)
            {
                ServerUrl = string.IsNullOrEmpty(server) ? EpikChatAutomation.ExpectedServer : server;
                StreamKey = key;
                Status($"Got it. Server: {ServerUrl}  Key: ••••{(key.Length > 4 ? key[^4..] : "")}. Filled into the current profile.");
                DialogResult = true;
                return;
            }
            if (i % 4 == 3) await Js("window.__epik.step('showkey', '')");
            await Task.Delay(1000);
        }
        Status("Couldn't find the stream key on the page. Make sure the RTMP stream was created and the key is shown (click \"Show stream key\"), then try again.", true);
    }

    async Task SignOut()
    {
        if (MessageBox.Show(this, "Sign out clears EpikStreamer's saved EpikChat browser session (cookies, storage). Continue?", "Sign out", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try
        {
            if (_web.CoreWebView2 != null)
            {
                await _web.CoreWebView2.Profile.ClearBrowsingDataAsync();
                _web.CoreWebView2.Navigate(EpikChatAutomation.LoginUrl);
            }
            Status("Signed out. EpikStreamer's EpikChat session data was cleared.");
        }
        catch (Exception ex) { Status("Sign out failed: " + ex.Message, true); }
    }

    /// <summary>Remove the whole WebView2 profile folder (used when no browser window is open).</summary>
    public static bool DeleteProfileFolder()
    {
        try { if (Directory.Exists(UserDataDir)) Directory.Delete(UserDataDir, true); return true; } catch { return false; }
    }
}
