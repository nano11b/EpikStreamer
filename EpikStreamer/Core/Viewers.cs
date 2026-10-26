namespace EpikStreamer.Core;

/// <summary>v2.3 viewer list settings.</summary>
public class ViewerSettings
{
    public bool Enabled { get; set; } = true;
    public string Source { get; set; } = "Both";       // LiveViewers | RoomMembers | Both
    public int LeaveGraceSeconds { get; set; } = 20;
    public bool JoinAlerts { get; set; } = false;       // on-stream welcome alert (opt-in; the New-viewer count alert stays separate)
    public string JoinText { get; set; } = "👋 Welcome {user}!";    // virtualized lists and reconnects flicker: only count a leave after this long
}

public class ViewerSession
{
    public string Name { get; init; } = "";
    public DateTime FirstSeen { get; init; }
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }
    public DateTime LastSeen { get; set; }
    public TimeSpan Total { get; set; }                 // closed visits
    public int Visits { get; set; } = 1;
    public string Source { get; set; } = "";
    public bool Present => LeftAt == null;
    public TimeSpan TotalNow(DateTime now) => Total + (Present ? now - JoinedAt : TimeSpan.Zero);
}

/// <summary>Turns periodic name snapshots (from the page) into join/leave events and per-session totals.</summary>
public sealed class ViewerTracker
{
    readonly object _lk = new();
    readonly Dictionary<string, ViewerSession> _v = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> _bySource = new();
    public ViewerSettings S { get; set; }
    public string Self { get; set; } = "";
    public event Action<ViewerSession>? Joined;
    public event Action<ViewerSession>? Left;
    public ViewerTracker(ViewerSettings s) => S = s;

    public List<ViewerSession> Snapshot() { lock (_lk) return _v.Values.OrderByDescending(x => x.Present).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
    public int PresentCount { get { lock (_lk) return _v.Values.Count(x => x.Present); } }
    public void Reset() { lock (_lk) { _v.Clear(); _bySource.Clear(); } }

    bool Wanted(string source) => S.Source == "Both" || string.Equals(S.Source, source, StringComparison.OrdinalIgnoreCase);

    /// <summary>A full list from one source ("LiveViewers" or "RoomMembers"). Names absent from every source for LeaveGraceSeconds leave.</summary>
    public void Update(string source, IEnumerable<string> names, DateTime now)
    {
        if (!S.Enabled || !Wanted(source)) return;
        var joined = new List<ViewerSession>(); var left = new List<ViewerSession>();
        lock (_lk)
        {
            var set = names.Select(n => n.Trim().TrimStart('@')).Where(n => n.Length > 0 && !n.Equals(Self, StringComparison.OrdinalIgnoreCase))
                           .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _bySource[source] = set;
            foreach (var n in set)
            {
                if (_v.TryGetValue(n, out var s))
                {
                    s.LastSeen = now;
                    if (!s.Present) { s.LeftAt = null; s.JoinedAt = now; s.Visits++; s.Source = source; joined.Add(s); }
                }
                else { s = new ViewerSession { Name = n, FirstSeen = now, JoinedAt = now, LastSeen = now, Source = source }; _v[n] = s; joined.Add(s); }
            }
            Sweep(now, left);
        }
        foreach (var s in joined) Joined?.Invoke(s);
        foreach (var s in left) Left?.Invoke(s);
    }

    /// <summary>Call periodically: closes visits not seen in any source for the grace period.</summary>
    public void Tick(DateTime now)
    {
        var left = new List<ViewerSession>();
        lock (_lk) Sweep(now, left);
        foreach (var s in left) Left?.Invoke(s);
    }

    void Sweep(DateTime now, List<ViewerSession> left)
    {
        foreach (var s in _v.Values)
        {
            if (!s.Present) continue;
            if (_bySource.Values.Any(x => x.Contains(s.Name))) { s.LastSeen = now; continue; }
            if ((now - s.LastSeen).TotalSeconds >= S.LeaveGraceSeconds)
            { s.LeftAt = s.LastSeen; s.Total += s.LastSeen - s.JoinedAt; left.Add(s); }
        }
    }
}
