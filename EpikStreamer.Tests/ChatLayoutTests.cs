using EpikStreamer;
using EpikStreamer.Core;
/// <summary>2.8.2: dotnet T layout — chat view sizing vs EpikChat's breakpoints, Compact CSS never hides navigation,
/// and "Publish failed / Operation not permitted" detection (stale stream key → fresh Go live).</summary>
static class ChatLayoutTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    public static int Run()
    {
        fails = 0;
        Check(ChatLayout.Window(0, 0) == (ChatLayout.DefaultW, ChatLayout.DefaultH) && ChatLayout.DefaultW >= ChatLayout.SidebarMin, "default chat window ≥ 1280 px: rooms + member list");
        Check(ChatLayout.Window(440, 820) == (ChatLayout.DefaultW, 820), "the old 440 px window is replaced by the default width");
        Check(ChatLayout.Window(1100, 700) == (1100, 700), "a remembered size ≥ the minimum is kept");
        Check(ChatLayout.MinW > ChatLayout.RailMin, "minimum window keeps the room rail (968 px breakpoint)");
        Check(ChatLayout.Column(0) == 1000 && ChatLayout.Column(200) == ChatLayout.MinColumn && ChatLayout.Column(1400) == 1400, "Windows chat column: default 1000, clamped, remembered");
        foreach (var w in new[] { 400.0, 600, 800, 983, 984, 1400 })
        {
            var z = ChatLayout.Zoom(w); var css = ChatLayout.CssWidth(w, z);
            Check(w >= ChatLayout.MinW ? z == 1.0 : (css >= ChatLayout.RailMin || z == 0.5), $"docked {w} px → zoom {z:0.00} → page sees {css:0} CSS px{(css >= ChatLayout.RailMin ? " (desktop layout)" : " (min zoom; page menus used)")}");
        }
        var css2 = EpikChatAutomation.CompactCss;
        Check(!css2.Contains("sidebar", StringComparison.OrdinalIgnoreCase) && !css2.Contains("header") && !css2.Contains("nav") && !css2.Contains("rail"), "Compact CSS never touches header/nav/sidebar/rail");
        // lines from Sam's 2.8.1 log (key masked as the app logs it) and other servers' wording
        foreach (var l in new[] { "[flv @ 0x55d] rtmp://media.epikchat.com:1935/x/****: Operation not permitted", "[rtmp @ 0x5a] Server error: Publish failed", "NetStream.Publish.BadName", "[rtmp @ 0x1] Server error: NetStream.Publish.Rejected" })
            Check(StreamEngine.IsPublishRejected(l), "publish refused: " + l);
        foreach (var l in new[] { "Connection to tcp://media.epikchat.com:1935 failed: Connection refused", "Encoder exited unexpectedly (code 1).", "frame= 100 fps=30 speed=1.0x", "av_interleaved_write_frame(): Broken pipe" })
            Check(!StreamEngine.IsPublishRejected(l), "not a key problem (plain retry): " + l);
        Console.WriteLine(fails == 0 ? "LAYOUT PASSED" : $"{fails} FAILED");
        return fails;
    }
}
