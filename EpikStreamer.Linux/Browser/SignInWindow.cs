using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EpikStreamer.Browser;
namespace EpikStreamer;

/// <summary>
/// Linux sign-in: EpikChat's real login page opens in a WebKitGTK window (the user signs in there; the app never sees the
/// password). This small window shows progress and runs the same Go-live automation as Windows to read the server + key.
/// Without WebKitGTK it falls back to the system browser and a manual "paste your key" form.
/// </summary>
public class SignInWindow : Window
{
    const string View = "signin";
    public string? ServerUrl { get; private set; }
    public string? StreamKey { get; private set; }
    readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button _getKey = new() { Content = "I'm signed in – get stream key", IsEnabled = false };
    readonly Button _showWeb = new() { Content = "Show EpikChat window" };
    readonly Button _signOut = new() { Content = "Sign out" };
    readonly StackPanel _manualPanel = new() { IsVisible = false, Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBox _mServer = new() { Text = EpikChatAutomation.ExpectedServer };
    readonly TextBox _mKey = new() { PasswordChar = '•', Watermark = "paste the stream key from EpikChat's Go-live → RTMP dialog" };
    readonly string _audience;
    BrowserClient? _b; KeyFetcher? _f; bool _done;
    public static string UserDataDir => WebViewHost.UserDataDir;

    public SignInWindow(Window owner, string audience)
    {
        _audience = audience; Title = "Sign in to EpikChat"; Width = 640; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _owner = owner;
        foreach (var b in new[] { _showWeb, _signOut }) b.Classes.Add("secondary");
        var close = new Button { Content = "Close", IsCancel = true }; close.Classes.Add("secondary");
        _getKey.Click += async (_, _) => await RunAutomation();
        _showWeb.Click += (_, _) => _b?.Show(View);
        _signOut.Click += async (_, _) => await SignOut();
        close.Click += (_, _) => Close();
        var btns = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), Children = { _getKey, _showWeb, _signOut, close } };
        var open = new Button { Content = "Open EpikChat in my browser" }; open.Classes.Add("secondary");
        open.Click += (_, _) => WebViewHost.OpenExternal(EpikChatAutomation.LoginUrl);
        var use = new Button { Content = "Use this key" };
        use.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_mKey.Text)) { Status("Paste the stream key first.", true); return; }
            ServerUrl = string.IsNullOrWhiteSpace(_mServer.Text) ? EpikChatAutomation.ExpectedServer : _mServer.Text.Trim();
            StreamKey = _mKey.Text.Trim(); _done = true; Close(true);
        };
        _manualPanel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Manual mode: sign in to EpikChat in your normal browser, open Go live → RTMP encoder → Create stream → Show stream key, then paste the values here." });
        _manualPanel.Children.Add(new TextBlock { Text = "Server URL", Margin = new Thickness(0, 8, 0, 0) }); _manualPanel.Children.Add(_mServer);
        _manualPanel.Children.Add(new TextBlock { Text = "Stream key" }); _manualPanel.Children.Add(_mKey);
        _manualPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { open, use } });
        Content = new StackPanel { Margin = new Thickness(14), Children = { _status, _manualPanel, btns } };
        WebViewHost.SetSignIn(true);   // only one EpikChat page at a time: the chat window unloads meanwhile
        Closed += (_, _) => { WebViewHost.SetSignIn(false); try { _b?.Close(View); } catch { } };
        Opened += async (_, _) => await Init();
    }
    readonly Window _owner;

    /// <summary>Modal, synchronous (WPF-style) for the ported main-window code.</summary>
    public bool? ShowDialog() => Sync.Wait(ShowDialog<object?>(_owner)) is true || _done;

    void Status(string s, bool error = false)
    {
        _status.Text = s;
        try   // step trace for debugging the page automation (never contains the key: it's masked)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpikStreamer");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "signin.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(error ? "ERR " : "")}{s}{Environment.NewLine}");
        }
        catch { }
        _status.Foreground = error ? Brushes.OrangeRed : (IBrush?)this.FindResource("Fg") ?? Brushes.White;
    }

    async Task Init()
    {
        Status("Starting the EpikChat browser window…");
        _b = await BrowserClient.GetAsync();
        if (_b == null)
        {
            Status("The built-in browser (WebKitGTK 4.1) isn't available: " + BrowserClient.Unavailable + ". Use manual mode below.", true);
            _manualPanel.IsVisible = true; _getKey.IsVisible = _showWeb.IsVisible = _signOut.IsVisible = false;
            return;
        }
        _f = new KeyFetcher(_b, View)
        {
            Status = (s, e) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status(s, e)),
            ConfirmSession = async () => await MessageBox.ShowAsync(this, "EpikChat allows one chat session per account and limits logins per IP address.\n\n" +
                "Opening the chat here may take over (disconnect) a chat session you have open in another browser or device. Continue?",
                "EpikChat session", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK,
        };
        _b.Loaded += (v, _) => { if (v == View) Avalonia.Threading.Dispatcher.UIThread.Post(() => _getKey.IsEnabled = true); };
        _b.Closed += v => { if (v == View) Avalonia.Threading.Dispatcher.UIThread.Post(() => Status("The EpikChat window was hidden. Click \"Show EpikChat window\" to bring it back.")); };
        _b.Open(View, "Sign in to EpikChat (EpikStreamer)", 1100, 800);
        _b.Navigate(View, EpikChatAutomation.LoginUrl);
        Status($"Sign in on the EpikChat window (WebKitGTK {_b.EngineVersion}). If you're already signed in it may skip ahead. Then click \"get stream key\".");
    }

    async Task RunAutomation()
    {
        if (_f == null) return;
        _getKey.IsEnabled = false;
        try
        {
            if (await _f.RunAsync(_audience)) { ServerUrl = _f.ServerUrl; StreamKey = _f.StreamKey; _done = true; Close(true); return; }
            if (_f.Manual) _getKey.Content = "Read key from page";
        }
        catch (Exception ex) { Status("Automation error: " + ex.Message, true); }
        finally { _getKey.IsEnabled = true; }
    }

    async Task SignOut()
    {
        if (await MessageBox.ShowAsync(this, "Sign out clears EpikStreamer's saved EpikChat browser session (cookies, storage). Continue?", "Sign out", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        try
        {
            if (_b != null) { await _b.ClearDataAsync(); _b.Navigate(View, EpikChatAutomation.LoginUrl); }
            Status("Signed out. EpikStreamer's EpikChat session data was cleared.");
        }
        catch (Exception ex) { Status("Sign out failed: " + ex.Message, true); }
    }

    /// <summary>Remove WebKit's stored cookies/site data (used when no browser window is open).</summary>
    public static bool DeleteProfileFolder()
    {
        try
        {
            BrowserClient.Shutdown();
            foreach (var d in new[] { UserDataDir, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "epikstreamer") })
                if (Directory.Exists(d)) Directory.Delete(d, true);
            return true;
        }
        catch { return false; }
    }
}
