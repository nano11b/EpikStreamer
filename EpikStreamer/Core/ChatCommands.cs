namespace EpikStreamer.Core;

/// <summary>A chat line read from the page. IsBot/Self: never answered (no loops, no bot ping-pong).</summary>
public record ChatMessage(string User, string Text, string? Id = null, bool IsBot = false, bool Self = false, string Room = "", string Time = "");

/// <summary>Chat command engine (pure; unit-tested). The host supplies live values via <see cref="Context"/>.</summary>
public sealed class ChatCommands
{
    public sealed class Context
    {
        public string Song = ""; public TimeSpan Uptime; public int? Viewers; public string Music = "";
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

    public IEnumerable<string> Triggers => new[] { "!song", "!uptime", "!help" }.Concat(BallPit != null && BallPitListed ? BallPitScene.CommandNames : Array.Empty<string>()).Concat(_s.Custom.Where(c => c.Enabled).Select(c => c.Trigger.Trim().ToLowerInvariant()));

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
            "!song" => _s.SongTemplate,
            "!uptime" => _s.UptimeTemplate,
            "!help" => "Commands: " + string.Join(" ", Triggers.Distinct()),
            _ => _s.Custom.FirstOrDefault(c => c.Enabled && string.Equals(c.Trigger.Trim(), cmd, StringComparison.OrdinalIgnoreCase))?.Response,
        };
        if (tpl == null) return null;
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
        .Replace("{user}", user);

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
