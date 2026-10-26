using System.Diagnostics;
namespace EpikStreamer;

/// <summary>Shared EpikChat browser state (same API as the Windows WebViewHost).</summary>
public static class WebViewHost
{
    /// <summary>WebKit stores cookies here (see BrowserHost); "Sign out" clears it.</summary>
    public static string UserDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "epikstreamer");
    public static event Action<bool>? SignInActive;
    public static bool IsSignInOpen { get; private set; }
    public static void SetSignIn(bool open) { IsSignInOpen = open; SignInActive?.Invoke(open); }

    /// <summary>Test hook: EPIK_TEST_ALLOW_FILE=1 also trusts file:// pages (the mock EpikChat pages). Never set in normal use.</summary>
    public static bool AllowFileUrls => Environment.GetEnvironmentVariable("EPIK_TEST_ALLOW_FILE") == "1";

    public static bool IsEpikChatUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && ((u.Scheme == "https" &&
        (u.Host.Equals("epikchat.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".epikchat.com", StringComparison.OrdinalIgnoreCase)))
        || (AllowFileUrls && u.Scheme == "file"));

    public static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            try { Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false }); } catch { }
    }
}
