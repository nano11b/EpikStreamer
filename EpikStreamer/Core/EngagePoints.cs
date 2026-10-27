using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace EpikStreamer.Core;

public class PointsUser
{
    public string Id { get; set; } = "";          // EpikChat userId ("" until learned)
    public string Name { get; set; } = "";
    public long Points { get; set; }
    public long Lifetime { get; set; }            // total ever earned
    public int WatchMinutes { get; set; }
    public int Messages { get; set; }
    public string LastDay { get; set; } = "";     // yyyy-MM-dd of the last stream they were credited in
    public int Streak { get; set; }
    public string LastStream { get; set; } = "";
    public string Key => PointsLedger.KeyOf(Id, Name);
    public PointsUser Clone() => (PointsUser)MemberwiseClone();
}

/// <summary>v2.8 viewer points, keyed by EpikChat userId (name until the id is known). JSON in the app data folder, atomic
/// writes, daily rotating backups. Thread-safe.</summary>
public sealed class PointsLedger
{
    public const int SchemaVersion = 2;
    readonly object _lk = new();
    readonly Dictionary<string, PointsUser> _u = new(StringComparer.Ordinal);
    public string? Path { get; }
    public int KeepBackups { get; set; } = 14;
    public bool Dirty { get; private set; }
    public event Action? Changed;
    public PointsLedger(string? path = null) { Path = path; if (path != null) Load(); }

    public static string KeyOf(string id, string name) => id.Length > 0 ? "id:" + id : "name:" + Norm(name);
    public static string Norm(string n) => (n ?? "").Trim().TrimStart('@').ToLowerInvariant();

    public int Count { get { lock (_lk) return _u.Count; } }
    public List<PointsUser> Snapshot() { lock (_lk) return _u.Values.Select(Copy).OrderByDescending(x => x.Points).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
    static PointsUser Copy(PointsUser x) => x.Clone();

    /// <summary>Finds (or creates) a user. A name-keyed record is migrated to the userId key the first time the id is known.</summary>
    public PointsUser Get(string id, string name, bool create = true)
    {
        id = (id ?? "").Trim(); name = (name ?? "").Trim().TrimStart('@');
        lock (_lk)
        {
            if (id.Length > 0 && _u.TryGetValue("id:" + id, out var u)) { if (name.Length > 0 && u.Name != name) { u.Name = name; Dirty = true; } return u; }
            var nk = "name:" + Norm(name);
            if (name.Length > 0 && _u.TryGetValue(nk, out var byName))
            {
                if (id.Length > 0) { _u.Remove(nk); byName.Id = id; _u["id:" + id] = byName; Dirty = true; }
                return byName;
            }
            if (id.Length == 0 && name.Length > 0)   // known only by id records: match the display name
            {
                var hit = _u.Values.FirstOrDefault(x => x.Id.Length > 0 && Norm(x.Name) == Norm(name));
                if (hit != null) return hit;
            }
            if (!create) return null!;
            var n = new PointsUser { Id = id, Name = name.Length > 0 ? name : id };
            _u[n.Key] = n; Dirty = true; return n;
        }
    }
    public PointsUser? Find(string nameOrId)
    {
        var k = Norm(nameOrId); if (k.Length == 0) return null;
        lock (_lk) return _u.Values.FirstOrDefault(x => Norm(x.Name) == k) ?? _u.Values.FirstOrDefault(x => x.Id.ToLowerInvariant() == k);
    }
    public long Balance(string id, string name) { lock (_lk) return Get(id, name, false)?.Points ?? 0; }

    /// <summary>Adds (or with a negative amount removes, never below 0) points. Earned=true counts toward Lifetime.</summary>
    public long Add(PointsUser u, long amount, bool earned = false)
    {
        lock (_lk)
        {
            u.Points = Math.Max(0, u.Points + amount);
            if (earned && amount > 0) u.Lifetime += amount;
            Dirty = true;
        }
        Changed?.Invoke(); return u.Points;
    }
    /// <summary>Takes points if the user has enough.</summary>
    public bool Spend(PointsUser u, long amount)
    {
        lock (_lk) { if (amount < 0 || u.Points < amount) return false; u.Points -= amount; Dirty = true; }
        Changed?.Invoke(); return true;
    }
    public int Rank(PointsUser u) { lock (_lk) return 1 + _u.Values.Count(x => x.Points > u.Points); }
    public List<PointsUser> Top(int n, ISet<string>? exclude = null)
    { lock (_lk) return _u.Values.Where(x => x.Points > 0 && (exclude == null || (!exclude.Contains(Norm(x.Name)) && !exclude.Contains(x.Id.ToLowerInvariant())))).OrderByDescending(x => x.Points).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Take(n).Select(Copy).ToList(); }

    // ---- editing (Points tab) ----
    public void Set(string key, long points) { lock (_lk) if (_u.TryGetValue(key, out var u)) { u.Points = Math.Max(0, points); Dirty = true; } Changed?.Invoke(); }
    public void Remove(string key) { lock (_lk) if (_u.Remove(key)) Dirty = true; Changed?.Invoke(); }
    public void ResetAll() { lock (_lk) { foreach (var u in _u.Values) { u.Points = 0; u.Streak = 0; } Dirty = true; } Changed?.Invoke(); }
    public void Mutate(Action a) { lock (_lk) { a(); Dirty = true; } }

    // ---- persistence ----
    static readonly JsonSerializerOptions Js = new() { WriteIndented = true };
    sealed class FileModel { public int Version { get; set; } = SchemaVersion; public DateTime Saved { get; set; } public List<PointsUser> Users { get; set; } = new(); }

    /// <summary>Loads the file. Accepts v2 ({Version, Users}), and migrates v1 ({"name": points} or [{name, points}]).</summary>
    public void Load()
    {
        if (Path == null || !File.Exists(Path)) return;
        string txt;
        try { txt = File.ReadAllText(Path); } catch { return; }
        List<PointsUser>? list = null;
        try { list = Parse(txt); } catch { }
        if (list == null)
        {   // corrupt: keep it aside and fall back to the newest good backup
            try { File.Copy(Path, Path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
            foreach (var b in Backups()) { try { list = Parse(File.ReadAllText(b)); if (list != null) break; } catch { } }
        }
        lock (_lk) { _u.Clear(); foreach (var u in list ?? new()) { if (u.Name.Length == 0 && u.Id.Length == 0) continue; _u[u.Key] = u; } Dirty = false; }
    }
    public static List<PointsUser>? Parse(string txt)
    {
        var n = JsonNode.Parse(txt);
        if (n is JsonObject o && o["Users"] is JsonArray) return JsonSerializer.Deserialize<FileModel>(txt)!.Users;
        if (n is JsonObject legacy)   // v1: { "alice": 120, ... }
            return legacy.Where(kv => kv.Value is JsonValue).Select(kv => new PointsUser { Name = kv.Key, Points = (long)kv.Value!.GetValue<double>(), Lifetime = (long)kv.Value!.GetValue<double>() }).ToList();
        if (n is JsonArray arr)       // v1: [ { "name": "...", "points": 1 } ]
            return arr.OfType<JsonObject>().Select(x => new PointsUser { Name = x["name"]?.GetValue<string>() ?? "", Id = x["userId"]?.GetValue<string>() ?? "", Points = (long)(x["points"]?.GetValue<double>() ?? 0) }).ToList();
        return null;
    }
    public IEnumerable<string> Backups()
    {
        if (Path == null) return Array.Empty<string>();
        var dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "backups");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "points-*.json").OrderByDescending(x => x).ToArray() : Array.Empty<string>();
    }
    /// <summary>Atomic save; before the first save of each day the previous file is copied to backups/points-yyyyMMdd.json.</summary>
    public void Save(DateTime? now = null)
    {
        if (Path == null) return;
        var t = now ?? DateTime.Now;
        string json;
        lock (_lk) { json = JsonSerializer.Serialize(new FileModel { Saved = t, Users = _u.Values.OrderBy(x => x.Key).ToList() }, Js); Dirty = false; }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (File.Exists(Path))
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "backups"); Directory.CreateDirectory(dir);
            var b = System.IO.Path.Combine(dir, $"points-{t:yyyyMMdd}.json");
            if (!File.Exists(b)) { File.Copy(Path, b); foreach (var old in Backups().Skip(Math.Max(1, KeepBackups))) try { File.Delete(old); } catch { } }
        }
        var tmp = Path + ".tmp"; File.WriteAllText(tmp, json); File.Move(tmp, Path, true);
    }
    public void SaveIfDirty() { if (Dirty) try { Save(); } catch { } }

    // ---- CSV (Points tab export/import) ----
    public string ExportCsv()
    {
        var sb = new StringBuilder("userId,name,points,lifetime,watchMinutes,messages,streak,lastDay\n");
        foreach (var u in Snapshot()) sb.Append(string.Join(",", Csv(u.Id), Csv(u.Name), u.Points, u.Lifetime, u.WatchMinutes, u.Messages, u.Streak, Csv(u.LastDay))).Append('\n');
        return sb.ToString();
    }
    static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@')
        ? "\"" + (s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@') ? "'" : "") + s.Replace("\"", "\"\"") + "\"" : s;
    public static List<string> SplitCsv(string line)
    {
        var r = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = false; } else sb.Append(c); }
            else if (c == '"') q = true; else if (c == ',') { r.Add(sb.ToString()); sb.Clear(); } else sb.Append(c);
        }
        r.Add(sb.ToString()); return r;
    }
    /// <summary>Imports a CSV (header required: userId/name/points…). Replace=true overwrites balances; false adds. Returns rows applied.</summary>
    public int ImportCsv(string csv, bool replace)
    {
        var lines = csv.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return 0;
        var h = SplitCsv(lines[0]).Select(x => x.Trim().ToLowerInvariant()).ToList();
        int iId = h.IndexOf("userid"), iName = h.IndexOf("name"), iPts = h.IndexOf("points"), iLife = h.IndexOf("lifetime"), iW = h.IndexOf("watchminutes"), iM = h.IndexOf("messages"), iS = h.IndexOf("streak"), iD = h.IndexOf("lastday");
        if (iPts < 0 || (iName < 0 && iId < 0)) throw new FormatException("CSV needs a points column and a name or userId column.");
        int n = 0;
        foreach (var line in lines.Skip(1))
        {
            var c = SplitCsv(line); string F(int i) => i >= 0 && i < c.Count ? c[i].Trim().TrimStart('\'') : "";
            if (!long.TryParse(F(iPts), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pts)) continue;
            var u = Get(F(iId), F(iName)); if (u == null) continue;
            lock (_lk)
            {
                u.Points = Math.Max(0, replace ? pts : u.Points + pts);
                if (long.TryParse(F(iLife), out var l)) u.Lifetime = replace ? l : u.Lifetime + l;
                if (int.TryParse(F(iW), out var w)) u.WatchMinutes = replace ? w : u.WatchMinutes + w;
                if (int.TryParse(F(iM), out var m)) u.Messages = replace ? m : u.Messages + m;
                if (replace && int.TryParse(F(iS), out var s)) u.Streak = s;
                if (replace && F(iD).Length > 0) u.LastDay = F(iD);
                Dirty = true;
            }
            n++;
        }
        Changed?.Invoke(); return n;
    }
}

/// <summary>v2.8 earning: watch time from the viewers list (acks), chat bonus with anti-spam, first chatter, streak days.</summary>
public sealed class PointsAccrual
{
    readonly PointsLedger _l; public EngageSettings S { get; set; }
    readonly Dictionary<string, (string Id, string Name, DateTime Seen)> _present = new();
    readonly Dictionary<string, (DateTime Last, string LastText, string PrevText, DateTime MinuteStart, int MinutePts)> _chat = new();
    readonly HashSet<string> _streakDone = new();
    DateTime _nextMinute = DateTime.MinValue;
    public bool Live { get; private set; }
    public string StreamId { get; private set; } = "";
    public string? FirstChatter { get; private set; }
    public Func<string, string, bool> Excluded = (_, _) => false;   // (id, name) → never earns (self, bots, Excluded list)
    public PointsAccrual(PointsLedger l, EngageSettings s) { _l = l; S = s; }

    public void StartStream(DateTime now) { Live = true; StreamId = now.ToString("yyyyMMddHHmmss"); FirstChatter = null; _streakDone.Clear(); _nextMinute = now.AddMinutes(1); _chat.Clear(); }
    public void EndStream() { Live = false; _present.Clear(); }

    /// <summary>A viewers snapshot (getBroadcastViewers ack or DOM list). Ids may be blank.</summary>
    public void Viewers(IEnumerable<(string Id, string Name)> vs, DateTime now)
    {
        var keep = new HashSet<string>();
        foreach (var (id, name) in vs)
        {
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id)) continue;
            if (Excluded(id ?? "", name ?? "")) continue;
            var k = PointsLedger.KeyOf(id ?? "", name ?? ""); keep.Add(k);
            _present[k] = (id ?? "", name ?? "", now);
        }
        foreach (var k in _present.Keys.Where(k => !keep.Contains(k)).ToList()) _present.Remove(k);
    }
    public int PresentCount => _present.Count;

    /// <summary>Call ≥ once a minute. Credits PointsPerMinute to everyone present (seen within 3 min) for each whole live minute.</summary>
    public int Tick(DateTime now)
    {
        if (!Live || S.PointsPerMinute <= 0) { if (Live && now >= _nextMinute) _nextMinute = now.AddMinutes(1); return 0; }
        int credited = 0;
        while (now >= _nextMinute)
        {
            foreach (var p in _present.Values.ToList())
            {
                if ((now - p.Seen).TotalMinutes > 3) continue;
                var u = _l.Get(p.Id, p.Name);
                Streak(u, now);
                _l.Mutate(() => u.WatchMinutes++);
                _l.Add(u, S.PointsPerMinute, earned: true); credited++;
            }
            _nextMinute = _nextMinute.AddMinutes(1);
            if (now - _nextMinute > TimeSpan.FromMinutes(5)) _nextMinute = now.AddMinutes(1);   // slept: don't back-pay hours
        }
        return credited;
    }

    /// <summary>Once per stream: consecutive stream days build a streak; from day 2 it pays StreakBonus × days (capped). Returns bonus.</summary>
    public int Streak(PointsUser u, DateTime now)
    {
        if (!Live || !_streakDone.Add(u.Key)) return 0;
        string today = now.ToString("yyyy-MM-dd"), yesterday = now.AddDays(-1).ToString("yyyy-MM-dd");
        if (u.LastDay == today) return 0;   // already counted today (an earlier stream)
        _l.Mutate(() => { u.Streak = u.LastDay == yesterday ? u.Streak + 1 : 1; u.LastDay = today; u.LastStream = StreamId; });
        if (!S.StreakEnabled || u.Streak < 2 || S.StreakBonus <= 0) return 0;
        int bonus = S.StreakBonus * Math.Min(u.Streak, Math.Max(2, S.StreakMaxDays));
        _l.Add(u, bonus, true); return bonus;
    }

    /// <summary>Chat bonus. Returns (points, firstChatter).</summary>
    public (int Points, bool First) Chat(string id, string name, string text, DateTime now)
    {
        if (!Live || Excluded(id, name)) return (0, false);
        var u = _l.Get(id, name);
        _l.Mutate(() => u.Messages++);
        int pts = 0; bool first = false;
        Streak(u, now);
        if (FirstChatter == null) { FirstChatter = u.Name; first = true; if (S.FirstChatterBonus > 0) { _l.Add(u, S.FirstChatterBonus, true); pts += S.FirstChatterBonus; } }
        var norm = System.Text.RegularExpressions.Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");
        if (norm.StartsWith('!') || S.ChatBonus <= 0) return (pts, first);
        _chat.TryGetValue(u.Key, out var c);
        bool repeat = norm.Length == 0 || norm == c.LastText || norm == c.PrevText;
        var st = c; st.PrevText = c.LastText; st.LastText = norm;
        if (now - c.MinuteStart >= TimeSpan.FromMinutes(1)) { st.MinuteStart = now; st.MinutePts = 0; }
        if (!repeat && (now - c.Last).TotalSeconds >= S.ChatCooldownSeconds && st.MinutePts + S.ChatBonus <= Math.Max(S.ChatBonus, S.ChatMaxPerMinute))
        { st.Last = now; st.MinutePts += S.ChatBonus; _l.Add(u, S.ChatBonus, true); pts += S.ChatBonus; }
        _chat[u.Key] = st;
        return (pts, first);
    }
}

/// <summary>Batches chat replies per room and chunks them to EpikChat's 250-character message limit.</summary>
public sealed class ReplyBatcher
{
    public const int Limit = 250;
    readonly List<(string Room, string Text)> _q = new(); readonly object _lk = new();
    public void Add(string room, string text) { if (!string.IsNullOrWhiteSpace(text)) lock (_lk) _q.Add((room ?? "", text.Trim())); }
    public int Pending { get { lock (_lk) return _q.Count; } }
    /// <summary>Joins queued replies for the same room with " · " while they fit, then splits anything still too long.</summary>
    public List<(string Room, string Text)> Flush(int limit = Limit)
    {
        List<(string Room, string Text)> q; lock (_lk) { q = _q.ToList(); _q.Clear(); }
        var outp = new List<(string, string)>();
        foreach (var g in q.GroupBy(x => x.Room))
        {
            var cur = "";
            foreach (var part in g.SelectMany(x => Chunk(x.Text, limit)))
            {
                if (cur.Length == 0) cur = part;
                else if (cur.Length + 3 + part.Length <= limit) cur += " · " + part;
                else { outp.Add((g.Key, cur)); cur = part; }
            }
            if (cur.Length > 0) outp.Add((g.Key, cur));
        }
        return outp;
    }
    /// <summary>Splits at " · " separators, then word boundaries, never mid-surrogate; continuation chunks start with "…".</summary>
    public static List<string> Chunk(string text, int limit = Limit)
    {
        var r = new List<string>(); text = text.Trim();
        while (text.Length > limit)
        {
            int cut = text.LastIndexOf(" · ", limit - 1, StringComparison.Ordinal);
            int sep = 3;
            if (cut < limit / 3) { cut = text.LastIndexOf(' ', limit - 1); sep = 1; }
            if (cut < limit / 3) { cut = limit - 1; sep = 0; if (char.IsHighSurrogate(text[cut - 1])) cut--; }
            r.Add(text[..cut].TrimEnd() + (sep == 0 ? "…" : ""));
            text = text[(cut + sep)..].TrimStart();
            if (text.Length > 0 && sep != 3) text = "…" + text;
        }
        if (text.Length > 0) r.Add(text);
        return r;
    }
}
