using System.Text.RegularExpressions;
namespace EpikStreamer.Core;

/// <summary>v2.8 chat poll: one vote per user (changeable), live counts, timed.</summary>
public sealed class Poll
{
    public string Question { get; init; } = "";
    public List<string> Options { get; init; } = new();
    public DateTime Started { get; init; }
    public DateTime Ends { get; set; }
    public string Room { get; init; } = "";
    public bool Closed { get; set; }
    readonly Dictionary<string, int> _votes = new();
    readonly object _lk = new();
    public int Total { get { lock (_lk) return _votes.Count; } }
    public int[] Counts() { lock (_lk) { var c = new int[Options.Count]; foreach (var v in _votes.Values) c[v]++; return c; } }
    /// <summary>0-based option. Returns "new", "changed", "same" or null (closed / out of range).</summary>
    public string? Vote(string voterKey, int option)
    {
        if (Closed || option < 0 || option >= Options.Count) return null;
        lock (_lk)
        {
            if (_votes.TryGetValue(voterKey, out var o)) { if (o == option) return "same"; _votes[voterKey] = option; return "changed"; }
            _votes[voterKey] = option; return "new";
        }
    }
    /// <summary>Whole-number percentages that add up to 100 (largest remainder).</summary>
    public static int[] Percents(int[] counts)
    {
        int total = counts.Sum(); var r = new int[counts.Length]; if (total == 0) return r;
        var exact = counts.Select(c => c * 100.0 / total).ToArray();
        for (int i = 0; i < r.Length; i++) r[i] = (int)Math.Floor(exact[i]);
        foreach (var i in Enumerable.Range(0, r.Length).OrderByDescending(i => exact[i] - r[i]).ThenBy(i => i).Take(100 - r.Sum())) r[i]++;
        return r;
    }
    public List<int> Leaders() { var c = Counts(); var m = c.DefaultIfEmpty(0).Max(); return m == 0 ? new() : Enumerable.Range(0, c.Length).Where(i => c[i] == m).ToList(); }
    /// <summary>Option index from "!vote 2" / "2" / exact option text, or -1.</summary>
    public int Match(string text, bool bare)
    {
        var t = (text ?? "").Trim();
        var m = Regex.Match(t, @"^!vote\s+(.+)$", RegexOptions.IgnoreCase);
        if (m.Success) t = m.Groups[1].Value.Trim(); else if (!bare) return -1;
        if (int.TryParse(t.TrimStart('#'), out var n)) return n >= 1 && n <= Options.Count ? n - 1 : -1;
        var nt = TriviaPack.Normalize(t);
        for (int i = 0; i < Options.Count; i++) if (TriviaPack.Normalize(Options[i]) == nt && nt.Length > 0) return i;
        return -1;
    }
    public string ResultText()
    {
        var c = Counts(); var p = Percents(c); var lead = Leaders();
        var head = lead.Count == 0 ? "no votes" : lead.Count == 1 ? $"winner: {Options[lead[0]]}" : "tie: " + string.Join(" / ", lead.Select(i => Options[i]));
        return $"📊 Poll closed: {Question} — {head} · " + string.Join(" · ", Options.Select((o, i) => $"{i + 1}. {o} {p[i]}% ({c[i]})"));
    }
    /// <summary>!poll [secs] "Question" a | b | c [secs]  — or  !poll Question | a | b. Null if not parseable.</summary>
    public static (string Q, List<string> Opts, int Secs)? Parse(string text, int defSecs)
    {
        var t = Regex.Replace(text.Trim(), @"^!poll\s*", "", RegexOptions.IgnoreCase);
        int secs = defSecs;
        var lead = Regex.Match(t, @"^(\d{1,4})\s*(s|m)?\s+"); if (lead.Success) { secs = int.Parse(lead.Groups[1].Value) * (lead.Groups[2].Value == "m" ? 60 : 1); t = t[lead.Length..]; }
        // trailing duration: "60s"/"2m", or a bare number after the last option's text ("… | blue 60"); "| 60" stays an option
        var trail = Regex.Match(t, @"(?<=[^|\s])\s+(\d{1,4})\s*(s|m)?$"); if (trail.Success && t[..trail.Index].Contains('|')) { secs = int.Parse(trail.Groups[1].Value) * (trail.Groups[2].Value == "m" ? 60 : 1); t = t[..trail.Index]; }
        string q; List<string> opts;
        var quoted = Regex.Match(t, "^[\"“](.+?)[\"”]\\s*(.*)$");
        if (quoted.Success) { q = quoted.Groups[1].Value.Trim(); opts = quoted.Groups[2].Value.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); }
        else { var parts = t.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); if (parts.Count < 3) return null; q = parts[0]; opts = parts.Skip(1).ToList(); }
        if (q.Length == 0 || opts.Count < 2 || opts.Count > 10) return null;
        return (q.Length > 150 ? q[..150] : q, opts.Select(o => o.Length > 60 ? o[..60] : o).ToList(), Math.Clamp(secs, 10, 3600));
    }
}

/// <summary>v2.8 prediction: viewers stake points on an outcome; the host locks and resolves; winners share the pool.</summary>
public sealed class Prediction
{
    public string Question { get; init; } = "";
    public List<string> Outcomes { get; init; } = new();
    public string Room { get; init; } = "";
    public DateTime Opened { get; init; }
    public bool Locked { get; set; }
    public int? Result { get; private set; }
    readonly object _lk = new();
    readonly Dictionary<string, (PointsUser U, int Pick, long Stake)> _s = new();
    public long Pool { get { lock (_lk) return _s.Values.Sum(x => x.Stake); } }
    public long[] PerOutcome() { lock (_lk) return Enumerable.Range(0, Outcomes.Count).Select(i => _s.Values.Where(x => x.Pick == i).Sum(x => x.Stake)).ToArray(); }
    public int[] Bettors() { lock (_lk) return Enumerable.Range(0, Outcomes.Count).Select(i => _s.Values.Count(x => x.Pick == i)).ToArray(); }
    public (int Pick, long Stake)? StakeOf(string key) { lock (_lk) return _s.TryGetValue(key, out var v) ? (v.Pick, v.Stake) : null; }
    /// <summary>Adds to a stake (same outcome only). The caller has already taken the points.</summary>
    public string? Add(PointsUser u, int pick, long stake)
    {
        if (Locked || Result != null) return "locked";
        if (pick < 0 || pick >= Outcomes.Count) return "bad outcome";
        lock (_lk)
        {
            if (_s.TryGetValue(u.Key, out var v)) { if (v.Pick != pick) return "already on " + (v.Pick + 1); _s[u.Key] = (u, pick, v.Stake + stake); }
            else _s[u.Key] = (u, pick, stake);
        }
        return null;
    }
    /// <summary>Resolves and returns the payouts per user (stake back × pool share).</summary>
    public List<(PointsUser U, long Payout, long Stake)> Resolve(int winner)
    {
        lock (_lk)
        {
            Locked = true; Result = winner;
            var pay = EngageGames.Parimutuel(_s.Select(kv => (kv.Key, kv.Value.Pick, kv.Value.Stake)), winner);
            return _s.Select(kv => (kv.Value.U, pay.GetValueOrDefault(kv.Key), kv.Value.Stake)).ToList();
        }
    }
    public List<(PointsUser U, long Stake)> Refund() { lock (_lk) { Locked = true; Result = -1; return _s.Values.Select(v => (v.U, v.Stake)).ToList(); } }
}
