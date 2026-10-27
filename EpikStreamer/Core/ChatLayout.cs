namespace EpikStreamer.Core;

/// <summary>2.8.2: EpikChat's responsive breakpoints (from the site's CSS, checked 2026-10-27):
/// <c>.chat-workspace-rail</c> (the room list) is <c>display:none</c> below <c>min-width:968px</c>, and
/// <c>.chat-workspace-sidebar</c> (members / live viewers) below <c>min-width:80rem</c> (1280 px). Narrower than 968 px the
/// page is the mobile layout: message column only. The chat view is sized/zoomed so the page sees at least these widths.</summary>
public static class ChatLayout
{
    public const int RailMin = 968, SidebarMin = 1280;
    public const int DefaultW = 1300, DefaultH = 860, MinW = RailMin + 16, MinH = 560;

    /// <summary>Linux chat window size to open with: the remembered size, never below the minimum (old 440×820 → default).</summary>
    public static (int W, int H) Window(int savedW, int savedH)
    {
        int w = savedW < MinW ? DefaultW : savedW, h = savedH < MinH ? DefaultH : savedH;
        return (Math.Min(w, 4000), Math.Min(h, 3000));
    }

    /// <summary>Windows: the chat is a docked column. Width to restore (remembered, default 1000, at least 480).</summary>
    public const int DefaultColumn = 1000, MinColumn = 480;
    public static int Column(int saved) => saved <= 0 ? DefaultColumn : Math.Clamp(saved, MinColumn, 4000);

    /// <summary>Zoom for a docked view so its CSS viewport is ≥ <see cref="MinW"/> (= the desktop layout with the room list):
    /// 1.0 when it's wide enough; never below 0.5 (then the page's own collapsed menus are used).</summary>
    public static double Zoom(double viewWidth)
    {
        if (viewWidth <= 0 || viewWidth >= MinW) return 1.0;
        return Math.Max(0.5, Math.Floor(viewWidth / MinW * 100) / 100);
    }

    /// <summary>CSS width the page will see.</summary>
    public static double CssWidth(double viewWidth, double zoom) => zoom <= 0 ? viewWidth : viewWidth / zoom;
}
