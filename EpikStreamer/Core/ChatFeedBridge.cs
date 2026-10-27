using System.Text.Json;
namespace EpikStreamer.Core;

/// <summary>v2.7: the page → app message handling shared by the Windows and Linux chat panels for the chat feed
/// (DOM observer + network tap). Validates and bounds everything coming from the page.</summary>
public static class ChatFeedBridge
{
    static string Str(JsonElement r, string k, int max = 500) { var s = r.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : ""; return s.Length > max ? s[..max] : s; }
    static bool Bool(JsonElement r, string k) => r.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.True;
    static List<string> Names(JsonElement r)
    {
        var names = new List<string>();
        if (r.TryGetProperty("names", out var na) && na.ValueKind == JsonValueKind.Array)
            foreach (var n in na.EnumerateArray()) if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 and <= 64 } nm && names.Count < 2000) names.Add(nm);
        return names;
    }
    static readonly System.Text.RegularExpressions.Regex Attached = new(@"^attached to (?<room>.*?), \d+ rows(?:, you are (?<me>.+))?", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Handles epik-chat / epik-viewers / epik-chat-status / epik-tap-*; returns false for other message types.
    /// <paramref name="status"/> receives observer/tap status lines for the log.</summary>
    public static bool Handle(JsonElement r, string type, ChatFeedRouter router, DateTime now, Action<string>? status = null)
    {
        switch (type)
        {
            case "epik-chat":
            {
                var text = Str(r, "text"); if (text.Length == 0) return true;
                var id = Str(r, "id", 120);
                var m = new ChatMessage(Str(r, "user", 60), text, id.Length > 0 ? id : null, Bool(r, "isBot"), Bool(r, "self"), Str(r, "room", 100), Str(r, "time", 40));
                if (Str(r, "source", 10) == "tap") { if (router.S.LiveFeed) router.FromTapMessage(m, Str(r, "roomId", 100), now); }
                else router.FromDomMessage(m, now);
                return true;
            }
            case "epik-chat-status":
            {
                var t = Str(r, "text", 300);
                var a = Attached.Match(t);
                if (a.Success) { router.SetVisibleRoom(a.Groups["room"].Value); if (a.Groups["me"].Success && router.Self.Length == 0) router.Self = a.Groups["me"].Value.Trim(); }
                status?.Invoke(t);
                return true;
            }
            case "epik-viewers":
                router.FromDomViewers(Str(r, "source", 30), Names(r), now);
                return true;
            case "epik-tap-viewers":
            {
                if (!router.S.LiveFeed) return true;
                var op = Str(r, "op", 10); if (op is not ("join" or "leave" or "snapshot")) return true;
                if (Str(r, "via", 10) == "rest" && !router.S.ViewersRestFallback) return true;
                router.FromTapViewers(op, Names(r), now);
                return true;
            }
            case "epik-tap-status":
            {
                var s = Str(r, "state", 10);
                if (s == "open") { var wasOpen = router.TapSocketOpen; router.TapSocketOpen = true; if (r.TryGetProperty("sockets", out var n) && n.ValueKind == JsonValueKind.Number && n.GetInt32() > 1 && !wasOpen) { router.SocketReconnected(); status?.Invoke("live feed reconnected"); } else if (!wasOpen) status?.Invoke("live feed connected"); }
                else if (s == "closed") { router.TapSocketOpen = false; status?.Invoke("live feed socket closed (the page reconnects by itself)"); }
                return true;
            }
            case "epik-tap-rooms":
            {
                if (r.TryGetProperty("rooms", out var rs) && rs.ValueKind == JsonValueKind.Array)
                    foreach (var x in rs.EnumerateArray().Take(2000)) if (x.ValueKind == JsonValueKind.Object) router.LearnRoom(Str(x, "id", 40), Str(x, "name", 100));
                return true;
            }
            case "epik-tap-self":
            {
                var n = Str(r, "name", 60); var d = Str(r, "display", 60);
                if (n.Length > 0) { router.Self = n; router.SelfDisplay = d; }
                return true;
            }
            case "epik-tap-members": case "epik-tap-stats":
                return true;
        }
        return false;
    }
}
