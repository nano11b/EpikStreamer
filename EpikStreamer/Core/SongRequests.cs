namespace EpikStreamer.Core;

/// <summary>v2.3 song request settings (Requests tab).</summary>
public class RequestSettings
{
    public bool Enabled { get; set; } = true;
    public string Placement { get; set; } = "Next";        // Next = after the current item; End = when the current playlist pass ends
    public int PerUserLimit { get; set; } = 2;             // pending requests per user
    public int UserCooldownSeconds { get; set; } = 60;
    public int MaxQueue { get; set; } = 20;
    public bool RequireApproval { get; set; }
    public string AllowedUsers { get; set; } = "";         // blank = everyone; comma/space separated
    public string BlockedUsers { get; set; } = "";
    public double MinMatchScore { get; set; } = 0.55;
    public int MaxDurationMinutes { get; set; } = 0;       // 0 = no limit
    public bool VoteSkipEnabled { get; set; } = true;
    public int SkipVotes { get; set; } = 3;                 // votes needed…
    public int SkipPercent { get; set; } = 0;               // …or this % of current viewers (0 = use SkipVotes)
    public bool WrongSongEnabled { get; set; } = true;
    public static HashSet<string> Names(string s) => s.Split(new[] { ',', ' ', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim().TrimStart('@').ToLowerInvariant()).ToHashSet();
}

public enum RequestStatus { Pending, Approved }

public class SongRequest
{
    public int Id { get; init; }
    public string Path { get; init; } = "";
    public string Title { get; init; } = "";
    public string User { get; init; } = "";
    public DateTime Time { get; init; }
    public RequestStatus Status { get; set; }
    public double Duration { get; init; }
    public override string ToString() => $"{Title} — {User}" + (Status == RequestStatus.Pending ? " (awaiting approval)" : "");
}

/// <summary>Thread-safe request queue + chat command handling for !request/!sr, !queue, !skip, !wrongsong.</summary>
public sealed class SongRequestQueue
{
    readonly object _lk = new();
    readonly List<SongRequest> _q = new();
    readonly Dictionary<string, DateTime> _lastByUser = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _votes = new(StringComparer.OrdinalIgnoreCase);
    string _voteTrack = "";
    int _nextId = 1;
    public RequestSettings S { get; set; }
    public event Action? Changed;
    /// <summary>The request that is on air right now (for "Requested by" and !wrongsong), or null.</summary>
    public SongRequest? Playing { get; private set; }
    public SongRequestQueue(RequestSettings s) => S = s;

    public List<SongRequest> Snapshot() { lock (_lk) return _q.ToList(); }
    public int Count { get { lock (_lk) return _q.Count; } }
    void Raise() => Changed?.Invoke();

    public record Result(string? Reply, bool Skip = false);

    static string Cmd(string text) => text.Trim().Split(' ', 2)[0].ToLowerInvariant();
    public static bool IsRequestCommand(string text) => Cmd(text) is "!request" or "!sr" or "!queue" or "!skip" or "!wrongsong";

    /// <summary>Handles a request-related chat command, or returns null if it isn't one / requests are off.</summary>
    public Result? Handle(ChatMessage m, IReadOnlyList<LibraryItem> library, string currentPath, string trackKey, int viewers, DateTime now)
    {
        if (!S.Enabled || m.IsBot || m.Self || string.IsNullOrWhiteSpace(m.Text)) return null;
        var parts = m.Text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant(); var arg = parts.Length > 1 ? parts[1].Trim() : "";
        return cmd switch
        {
            "!request" or "!sr" => new(Request(m.User, arg, library, currentPath, now)),
            "!queue" => new(QueueText()),
            "!skip" => S.VoteSkipEnabled ? VoteSkip(m.User, trackKey, viewers) : null,
            "!wrongsong" => S.WrongSongEnabled ? WrongSong(m.User) : null,
            _ => null,
        };
    }

    public string Request(string user, string query, IReadOnlyList<LibraryItem> library, string currentPath, DateTime now)
    {
        var u = user.Trim().TrimStart('@'); var lu = u.ToLowerInvariant();
        if (RequestSettings.Names(S.BlockedUsers).Contains(lu)) return $"@{u} song requests aren't available to you.";
        var allow = RequestSettings.Names(S.AllowedUsers);
        if (allow.Count > 0 && !allow.Contains(lu)) return $"@{u} song requests are limited to approved viewers.";
        if (query.Length == 0) return "Usage: !request <song title>  (also !sr). See !queue.";
        if (library.Count == 0) return "The song library is empty.";
        lock (_lk)
        {
            if (_q.Count >= Math.Max(1, S.MaxQueue)) return $"@{u} the request queue is full ({_q.Count}). Try again later.";
            if (_q.Count(r => r.User.Equals(u, StringComparison.OrdinalIgnoreCase)) >= Math.Max(1, S.PerUserLimit))
                return $"@{u} you already have {S.PerUserLimit} request(s) queued.";
            if (_lastByUser.TryGetValue(u, out var last) && (now - last).TotalSeconds < S.UserCooldownSeconds)
                return $"@{u} please wait {Math.Ceiling(S.UserCooldownSeconds - (now - last).TotalSeconds)}s before requesting again.";
        }
        var ranked = Fuzzy.Rank(query, library);
        if (ranked.Count == 0 || ranked[0].Score < S.MinMatchScore)
            return $"@{u} no match for \"{Trim(query, 40)}\"" + (ranked.Count > 0 ? $". Did you mean: {string.Join(" | ", ranked.Take(3).Select(r => Trim(r.Item.Display, 40)))}?" : ".");
        var it = ranked[0].Item;
        if (S.MaxDurationMinutes > 0 && it.Duration > S.MaxDurationMinutes * 60) return $"@{u} \"{Trim(it.Display, 50)}\" is longer than {S.MaxDurationMinutes} min.";
        if (string.Equals(it.Path, currentPath, StringComparison.OrdinalIgnoreCase)) return $"@{u} \"{Trim(it.Display, 50)}\" is playing right now.";
        SongRequest req; int pos;
        lock (_lk)
        {
            var dup = _q.FindIndex(r => string.Equals(r.Path, it.Path, StringComparison.OrdinalIgnoreCase));
            if (dup >= 0) return $"@{u} \"{Trim(it.Display, 50)}\" is already in the queue (#{dup + 1}).";
            req = new SongRequest { Id = _nextId++, Path = it.Path, Title = it.Display, User = u, Time = now, Duration = it.Duration,
                Status = S.RequireApproval ? RequestStatus.Pending : RequestStatus.Approved };
            _q.Add(req); pos = _q.Count; _lastByUser[u] = now;
        }
        Raise();
        return S.RequireApproval ? $"@{u} requested \"{Trim(req.Title, 60)}\" — waiting for the host's approval."
            : $"@{u} added \"{Trim(req.Title, 60)}\" (#{pos}" + (S.Placement == "End" ? ", after this playlist pass)" : ")");
    }

    public string QueueText()
    {
        var q = Snapshot();
        if (q.Count == 0) return "The request queue is empty. Use !request <song>.";
        return "Queue: " + string.Join(" · ", q.Take(5).Select((r, i) => $"{i + 1}. {Trim(r.Title, 30)} ({r.User}{(r.Status == RequestStatus.Pending ? ", pending" : "")})")) + (q.Count > 5 ? $" +{q.Count - 5} more" : "");
    }

    public int SkipThreshold(int viewers) => S.SkipPercent > 0 && viewers > 0 ? Math.Max(1, (int)Math.Ceiling(viewers * S.SkipPercent / 100.0)) : Math.Max(1, S.SkipVotes);

    public Result VoteSkip(string user, string trackKey, int viewers)
    {
        int n, need = SkipThreshold(viewers);
        lock (_lk)
        {
            if (_voteTrack != trackKey) { _voteTrack = trackKey; _votes.Clear(); }
            if (!_votes.Add(user.Trim().TrimStart('@'))) return new($"@{user} you already voted to skip ({_votes.Count}/{need}).");
            n = _votes.Count;
            if (n >= need) { _votes.Clear(); _voteTrack = trackKey + "#skipped"; }
        }
        return n >= need ? new($"Vote skip passed ({n}/{need}) — skipping.", true) : new($"Skip vote {n}/{need}.");
    }

    /// <summary>!wrongsong: removes the user's most recent queued request, or skips it if it's on air.</summary>
    public Result WrongSong(string user)
    {
        var u = user.Trim().TrimStart('@');
        lock (_lk)
        {
            var i = _q.FindLastIndex(r => r.User.Equals(u, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { var r = _q[i]; _q.RemoveAt(i); _lastByUser.Remove(u); Raise(); return new($"@{u} removed \"{Trim(r.Title, 50)}\" from the queue."); }
            if (Playing != null && Playing.User.Equals(u, StringComparison.OrdinalIgnoreCase)) return new($"@{u} skipping your request \"{Trim(Playing.Title, 50)}\".", true);
        }
        return new($"@{u} you have no queued request.");
    }

    // ---- host actions ----
    public bool Approve(int id) { lock (_lk) { var r = _q.FirstOrDefault(x => x.Id == id); if (r == null) return false; r.Status = RequestStatus.Approved; } Raise(); return true; }
    public bool Remove(int id) { bool ok; lock (_lk) ok = _q.RemoveAll(x => x.Id == id) > 0; if (ok) Raise(); return ok; }
    public bool Move(int id, int delta)
    {
        lock (_lk)
        {
            var i = _q.FindIndex(x => x.Id == id); var j = i + delta;
            if (i < 0 || j < 0 || j >= _q.Count) return false;
            var r = _q[i]; _q.RemoveAt(i); _q.Insert(j, r);
        }
        Raise(); return true;
    }
    public void Clear() { lock (_lk) _q.Clear(); Raise(); }
    /// <summary>Host adds a library item (no limits apply).</summary>
    public SongRequest Add(LibraryItem it, string by = "host")
    {
        SongRequest r;
        lock (_lk) { r = new SongRequest { Id = _nextId++, Path = it.Path, Title = it.Display, User = by, Time = DateTime.Now, Duration = it.Duration, Status = RequestStatus.Approved }; _q.Add(r); }
        Raise(); return r;
    }

    // ---- engine side ----
    /// <summary>Called by the engine at a track boundary: the first approved request whose file exists, or null.</summary>
    public SongRequest? TakeNext(bool atPlaylistEnd)
    {
        if (!S.Enabled) return null;
        if (S.Placement == "End" && !atPlaylistEnd) return null;
        SongRequest? r;
        lock (_lk) { r = _q.FirstOrDefault(x => x.Status == RequestStatus.Approved); if (r != null) _q.Remove(r); }
        if (r != null) Raise();
        return r;
    }
    readonly Dictionary<string, SongRequest> _scheduled = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The engine scheduled this request; it becomes Playing when that file goes on air.</summary>
    public void Scheduled(SongRequest r) { lock (_lk) _scheduled[r.Path] = r; }
    public void OnAir(string path)
    {
        lock (_lk) { Playing = _scheduled.Remove(path, out var r) ? r : null; _votes.Clear(); }
        Raise();
    }
    /// <summary>The next approved request (for "Up next").</summary>
    public SongRequest? PeekNext() { lock (_lk) return _scheduled.Values.FirstOrDefault() ?? _q.FirstOrDefault(x => x.Status == RequestStatus.Approved); }

    static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
