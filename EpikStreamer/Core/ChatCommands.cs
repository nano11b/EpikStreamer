namespace EpikStreamer.Core;

/// <summary>A chat line read from the page. IsBot/Self: never answered (no loops, no bot ping-pong).</summary>
public record ChatMessage(string User, string Text, string? Id = null, bool IsBot = false, bool Self = false, string Room = "", string Time = "", string UserId = "");

/// <summary>Chat command engine (pure; unit-tested). The host supplies live values via <see cref="Context"/>.</summary>
public sealed class ChatCommands
{
    public sealed class Context
    {
        public string Song = ""; public TimeSpan Uptime; public int? Viewers; public string Music = "";
        /// <summary>v2.6: set while the radio is on (current/last radio track, station name, the radio's !song / !lastsong templates).</summary>
        public string Radio = "", RadioLast = "", Station = "", RadioSongTemplate = "", RadioLastTemplate = "";
    }
    readonly ChatSettings _s; readonly Dictionary<string, DateTime> _last = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _seen = new(); readonly Queue<string> _seenQ = new();
    readonly Queue<string> _ownReplies = new();
    public ChatCommands(ChatSettings s) => _s = s;

    /// <summary>v2.5 Ball Pit hook: (user, command, argument, isHost) → handled. Runs with a per-user cooldown and never replies in chat.</summary>
    public Func<string, string, string, bool, bool>? BallPit { get; set; }
    public bool BallPitListed { get; set; } = true;
    readonly Dictionary<string, DateTime> _pitLast = new(StringComparer.OrdinalIgnoreCase);
    bool TryBallPit(ChatMessage m, bool host, DateTime now)
    {
        var text = m.Text.Trim();
        if (!text.StartsWith('!')) return false;
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        if (!BallPitScene.CommandNames.Contains(cmd)) return false;
        if (m.Id != null && _seen.Contains(m.Id)) return true;
        if (m.Id != null) { _seen.Add(m.Id); _seenQ.Enqueue(m.Id); if (_seenQ.Count > 500) _seen.Remove(_seenQ.Dequeue()); }
        var key = m.User.Trim() + " " + cmd;
        if (!host && _pitLast.TryGetValue(key, out var t) && (now - t).TotalSeconds < _s.CooldownSeconds) return true;
        if (!BallPit!(m.User.Trim(), cmd, parts.Length > 1 ? parts[1].Trim() : "", host)) return false;   // ball pit off → normal handling
        _pitLast[key] = now; if (_pitLast.Count > 2000) _pitLast.Clear();
        return true;
    }

    /// <summary>v2.6: list !np / !lastsong (and !request when radio requests are on) in !help.</summary>
    public bool RadioListed { get; set; }
    public bool RadioRequestsListed { get; set; }
    public IEnumerable<string> Triggers => new[] { "!song", "!uptime", "!help" }
        .Concat(RadioListed ? new[] { "!np", "!lastsong" } : Array.Empty<string>()).Concat(RadioRequestsListed ? new[] { "!request" } : Array.Empty<string>()).Concat(BallPit != null && BallPitListed ? BallPitScene.CommandNames : Array.Empty<string>()).Concat(_s.Custom.Where(c => c.Enabled).Select(c => c.Trigger.Trim().ToLowerInvariant()));

    /// <summary>Returns the response for a message or null (not a command, cooldown, duplicate, or our own reply).</summary>
    public string? Handle(ChatMessage m, Context ctx, DateTime now)
    {
        if (!_s.CommandsEnabled || string.IsNullOrWhiteSpace(m.Text) || m.IsBot) return null;
        bool host = m.Self || (_s.MyUsername.Length > 0 && string.Equals(m.User.Trim(), _s.MyUsername.Trim(), StringComparison.OrdinalIgnoreCase));
        if (BallPit != null && TryBallPit(m, host, now)) return null;   // Ball Pit commands never post a chat reply
        if (m.Self) return null;
        if (_s.MyUsername.Length > 0 && string.Equals(m.User.Trim(), _s.MyUsername.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        if (m.Id != null) { if (!_seen.Add(m.Id)) return null; _seenQ.Enqueue(m.Id); if (_seenQ.Count > 500) _seen.Remove(_seenQ.Dequeue()); }
        var text = m.Text.Trim();
        if (_ownReplies.Contains(text)) return null;   // never react to our own posted replies
        if (!text.StartsWith('!')) return null;
        var cmd = text.Split(' ', 2)[0].ToLowerInvariant();
        string? tpl = cmd switch
        {
            "!song" or "!np" when ctx.Radio.Length > 0 => ctx.RadioSongTemplate.Length > 0 ? ctx.RadioSongTemplate : "Now playing: {radio}",
            "!lastsong" when ctx.RadioLast.Length > 0 => ctx.RadioLastTemplate.Length > 0 ? ctx.RadioLastTemplate : "Last song: {last}",
            "!song" or "!np" => _s.SongTemplate,
            "!uptime" => _s.UptimeTemplate,
            "!help" => "Commands: " + string.Join(" ", Triggers.Distinct()),
            _ => _s.Custom.FirstOrDefault(c => c.Enabled && string.Equals(c.Trigger.Trim(), cmd, StringComparison.OrdinalIgnoreCase))?.Response,
        };
        if (tpl == null) return null;
        if (cmd == "!np") cmd = "!song";   // shared cooldown
        if (_last.TryGetValue(cmd, out var t) && (now - t).TotalSeconds < _s.CooldownSeconds) return null;
        _last[cmd] = now;
        var r = Expand(tpl, ctx, m.User);
        _ownReplies.Enqueue(r.Trim()); if (_ownReplies.Count > 20) _ownReplies.Dequeue();
        return r;
    }

    public static string Expand(string tpl, Context c, string user = "") => tpl
        .Replace("{song}", string.IsNullOrEmpty(c.Song) ? "(nothing)" : c.Song)
        .Replace("{music}", string.IsNullOrEmpty(c.Music) ? "(no music)" : c.Music)
        .Replace("{uptime}", FormatUptime(c.Uptime))
        .Replace("{viewers}", c.Viewers?.ToString() ?? "?")
        .Replace("{user}", user)
        .Replace("{station}", string.IsNullOrEmpty(c.Station) ? "the radio" : c.Station)
        .Replace("{radio}", string.IsNullOrEmpty(c.Radio) ? "(radio off)" : c.Radio)
        .Replace("{last}", string.IsNullOrEmpty(c.RadioLast) ? "(unknown)" : c.RadioLast);

    public static string FormatUptime(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{t.Seconds}s";
}

/// <summary>Viewer-count alerts: new viewer (count went up) and milestones (each fires once per session). Throttled.</summary>
public sealed class ViewerAlerts
{
    readonly ChatSettings _s; int? _prev; int _peak; readonly HashSet<int> _hit = new(); DateTime _lastNew = DateTime.MinValue;
    public TimeSpan NewViewerThrottle { get; set; } = TimeSpan.FromSeconds(20);
    public ViewerAlerts(ChatSettings s) => _s = s;
    public void Reset() { _prev = null; _peak = 0; _hit.Clear(); _lastNew = DateTime.MinValue; }

    public string? Update(int viewers, DateTime now)
    {
        if (!_s.AlertsEnabled) { _prev = viewers; return null; }
        var prev = _prev; _prev = viewers;
        if (prev == null) { _peak = viewers; foreach (var m in _s.Milestones) if (viewers >= m) _hit.Add(m); return null; } // baseline
        var ctx = new ChatCommands.Context { Viewers = viewers };
        var ms = _s.Milestones.Where(m => viewers >= m && !_hit.Contains(m)).DefaultIfEmpty(0).Max();
        if (ms > 0) { foreach (var m in _s.Milestones) if (viewers >= m) _hit.Add(m); _peak = Math.Max(_peak, viewers); return ChatCommands.Expand(_s.MilestoneText, ctx); }
        if (_s.NewViewerAlert && viewers > prev && now - _lastNew >= NewViewerThrottle)
        { _lastNew = now; _peak = Math.Max(_peak, viewers); return ChatCommands.Expand(_s.NewViewerText, ctx); }
        _peak = Math.Max(_peak, viewers);
        return null;
    }
}
