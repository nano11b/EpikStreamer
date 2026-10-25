using System.IO;
using EpikStreamer.Core;
using Microsoft.Web.WebView2.Core;
namespace EpikStreamer;

/// <summary>
/// One WebView2 environment (one browser profile, one cookie jar) for the whole app. The sign-in window and the chat
/// panel both use it, and they coordinate so that only ONE EpikChat page is ever open: EpikChat allows a single chat
/// session per account and a limited number of logins per IP.
/// </summary>
public static class WebViewHost
{
    public static string UserDataDir => Path.Combine(FfmpegLocator.LocalAppDataDir, "WebView2");
    static Task<CoreWebView2Environment>? _env;
    public static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        Directory.CreateDirectory(UserDataDir);
        return _env ??= CoreWebView2Environment.CreateAsync(null, UserDataDir);
    }

    /// <summary>Raised (UI thread) when the sign-in window opens/closes; the chat panel unloads itself meanwhile.</summary>
    public static event Action<bool>? SignInActive;
    public static bool IsSignInOpen { get; private set; }
    public static void SetSignIn(bool open) { IsSignInOpen = open; SignInActive?.Invoke(open); }

    public static bool IsEpikChatUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" &&
        (u.Host.Equals("epikchat.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".epikchat.com", StringComparison.OrdinalIgnoreCase));

    public static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
