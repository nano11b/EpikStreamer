using System.Globalization;
using System.Text.RegularExpressions;
namespace EpikStreamer.Core;

/// <summary>What the Engage engine needs from the app (the request queue, the Ball Pit, alerts/sounds).</summary>
public interface IEngageHost
{
    (bool Ok, string Reply) RequestSong(string user, string query);
    bool BallPit(string user, string command);      // false: the pit is off / the effect didn't apply (→ refund)
    bool PitActive { get; }
    IReadOnlyList<string> PitNames();
    void Alert(string text);
    void Sound(string path);
    void Race(RaceGame? race);                      // a race starts (null: race over / cancelled)
}

public sealed class EngageOutput
{
    public bool Handled;                            // an Engage command (the rest of the chat pipeline skips it)
    public readonly List<(string Room, string Text)> Replies = new();
    public void Reply(string room, string text) => Replies.Add((room, text));
}

public sealed class PendingRedemption
{
    public int Id { get; init; }
    public string User { get; init; } = "";
    public string UserKey { get; init; } = "";
    public string Arg { get; init; } = "";
    public string Room { get; init; } = "";
    public Reward Reward { get; init; } = new();
    public DateTime Time { get; init; }
    public override string ToString() => $"#{Id} {User}: {Reward.Title}" + (Arg.Length > 0 ? $" — {Arg}" : "") + $" ({Reward.Cost})";
}

/// <summary>Live state for the overlay (poll bars, prediction, leaderboard, game toasts, race HUD).</summary>
public sealed record EngageView(
    Poll? Poll, bool PollFinal, Prediction? Prediction,
    List<PointsUser>? Leaderboard, List<string> Toasts,
    (string Q, DateTime Ends)? Trivia, (DateTime Ends, int Crew, long Pot)? Heist,
    RaceGame? Race, (DateTime Ends, List<string> Racers, long Pot)? RaceBetting, string Currency, DateTime Now)
{
    public bool Empty => Poll == null && Prediction == null && Leaderboard == null && Toasts.Count == 0 && Trivia == null && Heist == null && Race == null && RaceBetting == null;
}

/// <summary>v2.8 Engage: points commands, rewards, mini-games, polls and predictions. Pure apart from <see cref="IEngageHost"/>;
/// time is passed in, randomness comes from the injected Random (seeded in tests). Thread-safe (one lock).</summary>
public sealed class EngageEngine
{
    readonly object _lk = new();
    public EngageSettings S { get; set; }
    public PointsLedger Ledger { get; }
    public PointsAccrual Accrual { get; }
    public IEngageHost? Host { get; set; }
    readonly Random _rnd;
    public string SelfName { get; set; } = "";
    public string SelfId { get; set; } = "";
    public event Action? Changed;
    public event Action<string>? Log;
    /// <summary>Room for engine-initiated posts (scheduled trivia): the visible chat room.</summary>
    public Func<string>? DefaultRoom { get; set; }

    public EngageEngine(EngageSettings s, PointsLedger ledger, Random? rnd = null)
    {
        S = s; Ledger = ledger; _rnd = rnd ?? new Random();
        Accrual = new PointsAccrual(ledger, s) { Excluded = (id, name) => IsSelf(id, name) || IsExcluded(id, name) };
    }
    public void Apply(EngageSettings s) { S = s; Accrual.S = s; _trivia = null; }
    string Cur => S.CurrencyName.Trim().Length > 0 ? S.CurrencyName.Trim() : "points";
    string Pts(long n) => n.ToString("N0", CultureInfo.InvariantCulture) + " " + Cur;

    // ---------- identity ----------
    public bool IsSelf(string id, string name) => (SelfId.Length > 0 && id == SelfId) || (SelfName.Length > 0 && string.Equals(name.Trim().TrimStart('@'), SelfName.Trim(), StringComparison.OrdinalIgnoreCase));
    public bool IsHost(ChatMessage m) => m.Self || IsSelf(m.UserId, m.User);
    public bool IsMod(ChatMessage m) { var l = EngageSettings.List(S.Moderators); return l.Contains(PointsLedger.Norm(m.User)) || (m.UserId.Length > 0 && l.Contains(m.UserId.ToLowerInvariant())); }
    public bool IsHostOrMod(ChatMessage m) => IsHost(m) || IsMod(m);
    bool IsExcluded(string id, string name) { var l = EngageSettings.List(S.Excluded); return l.Contains(PointsLedger.Norm(name)) || (id.Length > 0 && l.Contains(id.ToLowerInvariant())); }
    bool CanPlay(ChatMessage m) => !m.IsBot && !IsHost(m) && !IsExcluded(m.UserId, m.User);

    // ---------- stream lifecycle / earning ----------
    public void StartStream(DateTime now) { lock (_lk) Accrual.StartStream(now); _nextTrivia = now.AddMinutes(Math.Max(1, S.Games.TriviaEveryMinutes)); }
    public void EndStream() { lock (_lk) Accrual.EndStream(); }
    public void OnViewers(IEnumerable<(string Id, string Name)> vs, DateTime now) { lock (_lk) Accrual.Viewers(vs, now); }

    // ---------- anti-abuse ----------
    readonly Dictionary<string, DateTime> _userLast = new();
    readonly Queue<DateTime> _global = new();
    readonly Dictionary<string, DateTime> _gameLast = new();
    readonly HashSet<string> _seen = new(); readonly Queue<string> _seenQ = new();
    bool Guard(ChatMessage m, DateTime now)
    {
        if (IsHostOrMod(m)) return true;
        while (_global.Count > 0 && now - _global.Peek() > TimeSpan.FromMinutes(1)) _global.Dequeue();
        if (S.GlobalCommandsPerMinute > 0 && _global.Count >= S.GlobalCommandsPerMinute) return false;
        var k = Key(m);
        if (_userLast.TryGetValue(k, out var t) && (now - t).TotalSeconds < S.UserCooldownSeconds) return false;
        _userLast[k] = now; _global.Enqueue(now); if (_userLast.Count > 5000) _userLast.Clear();
        return true;
    }
    string? GameCooldown(ChatMessage m, string game, DateTime now)
    {
        var k = Key(m) + " " + game;
        if (_gameLast.TryGetValue(k, out var t) && (now - t).TotalSeconds < S.Games.GameCooldownSeconds) return $"@{m.User} {game} again in {Math.Ceiling(S.Games.GameCooldownSeconds - (now - t).TotalSeconds)}s";
        _gameLast[k] = now; return null;
    }
    static string Key(ChatMessage m) => PointsLedger.KeyOf(m.UserId, m.User);
    PointsUser U(ChatMessage m) => Ledger.Get(m.UserId, m.User);

    public static readonly string[] CommandNames = { "!points", "!top", "!give", "!addpoints", "!removepoints", "!redeem", "!rewards", "!approve", "!deny",
        "!gamble", "!slots", "!duel", "!accept", "!decline", "!trivia", "!heist", "!race", "!joinrace", "!bet", "!poll", "!vote", "!endpoll",
        "!prediction", "!predict", "!lock", "!resolve", "!cancelprediction" };
    /// <summary>Commands for !help (viewer-facing, enabled only).</summary>
    public IEnumerable<string> Triggers()
    {
        if (!S.Enabled) yield break;
        yield return "!points"; yield return "!top"; if (S.GiveEnabled) yield return "!give";
        if (S.Rewards.Any(r => r.Enabled)) { yield return "!rewards"; yield return "!redeem"; }
        var g = S.Games; if (g.GambleEnabled) yield return "!gamble"; if (g.SlotsEnabled) yield return "!slots"; if (g.DuelEnabled) yield return "!duel"; if (g.HeistEnabled) yield return "!heist";
        if (g.RaceEnabled) { yield return "!joinrace"; yield return "!bet"; }
        if (S.Polls.Enabled) yield return "!vote"; if (S.Polls.PredictionsEnabled) yield return "!predict";
    }

    // ---------- state ----------
    Poll? _poll; bool _pollFinal; DateTime _pollShowUntil;
    public List<string> PollHistory { get; } = new();
    Prediction? _pred;
    List<PointsUser>? _board; DateTime _boardUntil;
    readonly List<(string Text, DateTime Until)> _toasts = new();
    record DuelState(PointsUser A, PointsUser B, long Stake, DateTime Expires, string Room);
    readonly Dictionary<string, DuelState> _duels = new();   // by target key
    (TriviaQuestion Q, DateTime Ends, string Room)? _trivia; DateTime _nextTrivia = DateTime.MaxValue; List<TriviaQuestion>? _pack; readonly HashSet<int> _asked = new();
    (DateTime Ends, string Room, List<(PointsUser U, long Stake)> Crew)? _heist;
    (DateTime Ends, string Room, List<string> Racers, List<(PointsUser U, int Pick, long Stake)> Bets)? _raceBet;
    (RaceGame G, string Room, List<(PointsUser U, int Pick, long Stake)> Bets)? _race;
    readonly Dictionary<string, DateTime> _rewardLast = new(StringComparer.OrdinalIgnoreCase);
    readonly List<PendingRedemption> _pending = new(); int _pendingId;
    public List<PendingRedemption> Pending() { lock (_lk) return _pending.ToList(); }
    public Poll? CurrentPoll { get { lock (_lk) return _poll is { Closed: false } ? _poll : null; } }
    public Prediction? CurrentPrediction { get { lock (_lk) return _pred; } }
    public bool RaceRunning { get { lock (_lk) return _race != null || _raceBet != null; } }

    void Toast(string text, DateTime now) { _toasts.Add((text, now.AddSeconds(Math.Max(2, S.Overlay.ToastSeconds)))); if (_toasts.Count > 6) _toasts.RemoveAt(0); }

    public EngageView View(DateTime now)
    {
        lock (_lk)
        {
            _toasts.RemoveAll(t => t.Until < now);
            var poll = _poll != null && (!_poll.Closed || now < _pollShowUntil) ? _poll : null;
            return new EngageView(poll, _pollFinal && poll != null, _pred, now < _boardUntil ? _board : null, _toasts.Select(t => t.Text).ToList(),
                _trivia is { } t ? (t.Q.Q, t.Ends) : null, _heist is { } h ? (h.Ends, h.Crew.Count, h.Crew.Sum(c => c.Stake)) : null,
                _race?.G, _raceBet is { } rb ? (rb.Ends, rb.Racers.ToList(), rb.Bets.Sum(b => b.Stake)) : null, Cur, now);
        }
    }

    // ---------- chat ----------
    /// <summary>Every chat message (all rooms). Commands are answered in the room they came from.</summary>
    public EngageOutput Handle(ChatMessage m, DateTime now)
    {
        var o = new EngageOutput();
        if (!S.Enabled || string.IsNullOrWhiteSpace(m.Text) || m.IsBot) return o;
        lock (_lk)
        {
            if (m.Id != null) { if (!_seen.Add(m.Id)) return o; _seenQ.Enqueue(m.Id); if (_seenQ.Count > 1000) _seen.Remove(_seenQ.Dequeue()); }
            var text = m.Text.Trim();
            bool host = IsHost(m), play = CanPlay(m);
            if (!text.StartsWith('!'))
            {
                if (play)
                {
                    var (pts, first) = Accrual.Chat(m.UserId, m.User, text, now);
                    if (first) { o.Reply(m.Room, $"🥇 {m.User} is the first chatter this stream" + (S.FirstChatterBonus > 0 ? $" (+{Pts(S.FirstChatterBonus)})" : "") + "!"); Toast($"🥇 First chatter: {m.User}", now); }
                    if (_trivia is { } tq && now <= tq.Ends && TriviaPack.IsCorrect(tq.Q, text)) TriviaWin(m, tq.Room, now, o);
                }
                if (_poll is { Closed: false } p && S.Polls.BareVotes && !host && p.Match(text, true) is var idx and >= 0) { p.Vote(Key(m), idx); Changed?.Invoke(); }
                return o;
            }
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cmd = parts[0].ToLowerInvariant(); var args = parts.Skip(1).ToArray();
            if (!CommandNames.Contains(cmd)) return o;
            o.Handled = true;
            if (!host && !play && !IsMod(m)) return o;
            if (!Guard(m, now)) return o;
            try { Command(m, cmd, args, text, now, o); }
            catch (Exception ex) { Log?.Invoke($"engage {cmd}: {ex.Message}"); }
        }
        if (o.Replies.Count > 0) Changed?.Invoke();
        return o;
    }

    void Command(ChatMessage m, string cmd, string[] a, string text, DateTime now, EngageOutput o)
    {
        string room = m.Room, at = "@" + m.User;
        bool hm = IsHostOrMod(m);
        var g = S.Games;
        switch (cmd)
        {
            case "!points":
            {
                if (a.Length > 0 && Ledger.Find(a[0].TrimStart('@')) is { } other) { o.Reply(room, $"{other.Name} has {Pts(other.Points)} (#{Ledger.Rank(other)})"); return; }
                if (IsHost(m)) { o.Reply(room, $"{at} the host doesn't collect {Cur} 🙂"); return; }
                var u = U(m); o.Reply(room, $"{at} you have {Pts(u.Points)} (#{Ledger.Rank(u)})" + (u.Streak >= 2 ? $" · {u.Streak}-day streak 🔥" : ""));
                return;
            }
            case "!top":
            {
                var top = Ledger.Top(Math.Clamp(S.TopCount, 1, 25), Excludes());
                _board = top; _boardUntil = now.AddSeconds(Math.Max(3, S.Overlay.LeaderboardSeconds));
                o.Reply(room, top.Count == 0 ? $"No {Cur} yet — watch and chat to earn some!" : $"🏆 Top {Cur}: " + string.Join(" · ", top.Select((u, i) => $"{i + 1}. {u.Name} {u.Points:N0}")));
                return;
            }
            case "!give":
            {
                if (!S.GiveEnabled || IsHost(m)) return;
                if (a.Length < 2 || EngageGames.ParseBet(a[1], U(m).Points, 0) is not long n) { o.Reply(room, $"{at} usage: !give <user> <amount>"); return; }
                var to = Ledger.Find(a[0].TrimStart('@'));
                if (to == null) { o.Reply(room, $"{at} I don't know {a[0]} yet"); return; }
                var me = U(m); if (to.Key == me.Key) return;
                if (!Ledger.Spend(me, n)) { o.Reply(room, $"{at} you only have {Pts(me.Points)}"); return; }
                Ledger.Add(to, n); o.Reply(room, $"{at} gave {Pts(n)} to {to.Name} 🎁");
                return;
            }
            case "!addpoints": case "!removepoints":
            {
                if (!hm) return;
                if (a.Length < 2 || !long.TryParse(a[1], out var n) || n <= 0) { o.Reply(room, $"{at} usage: {cmd} <user> <amount>"); return; }
                var u = Ledger.Find(a[0].TrimStart('@')) ?? Ledger.Get("", a[0]);
                Ledger.Add(u, cmd == "!addpoints" ? n : -n);
                o.Reply(room, $"{u.Name} now has {Pts(u.Points)}"); Log?.Invoke($"{m.User}: {cmd} {u.Name} {n}");
                return;
            }
            case "!rewards":
            {
                var rs = S.Rewards.Where(r => r.Enabled).ToList();
                o.Reply(room, rs.Count == 0 ? "No rewards right now." : "Rewards (!redeem <name>): " + string.Join(" · ", rs.Select(r => $"{r.Name} {r.Cost:N0}")));
                return;
            }
            case "!redeem": Redeem(m, a, now, o); return;
            case "!approve": case "!deny":
            {
                if (!hm) return;
                if (a.Length < 1 || !int.TryParse(a[0].TrimStart('#'), out var id)) { o.Reply(room, $"{at} usage: {cmd} <#>"); return; }
                var r = cmd == "!approve" ? ApproveLocked(id, now) : DenyLocked(id);
                foreach (var x in r.Replies) o.Replies.Add(x);
                return;
            }
            case "!gamble":
            {
                if (!g.GambleEnabled || !CanPlay(m)) return;
                var u = U(m);
                if (a.Length < 1 || EngageGames.ParseBet(a[0], u.Points, g.MaxBet) is not long bet) { o.Reply(room, $"{at} usage: !gamble <amount|all> (max {g.MaxBet:N0})"); return; }
                if (bet < g.MinBet) { o.Reply(room, $"{at} minimum bet is {g.MinBet:N0}"); return; }
                if (u.Points < bet) { o.Reply(room, $"{at} you only have {Pts(u.Points)}"); return; }
                if (GameCooldown(m, "!gamble", now) is { } cd) { o.Reply(room, cd); return; }
                var (won, d) = EngageGames.Gamble(bet, g, _rnd);
                Ledger.Add(u, d);
                var msg = won ? $"🎲 {m.User} won {Pts(d)}! Now {u.Points:N0}" : $"🎲 {m.User} lost {Pts(-d)}. Now {u.Points:N0}";
                o.Reply(room, msg); Toast(msg, now);
                return;
            }
            case "!slots":
            {
                if (!g.SlotsEnabled || !CanPlay(m)) return;
                var u = U(m);
                long bet = g.SlotsCost;
                if (a.Length > 0) { if (EngageGames.ParseBet(a[0], u.Points, g.MaxBet) is not long b) { o.Reply(room, $"{at} usage: !slots [amount]"); return; } bet = b; }
                if (bet < 1 || u.Points < bet) { o.Reply(room, $"{at} slots cost {Pts(bet)}; you have {u.Points:N0}"); return; }
                if (GameCooldown(m, "!slots", now) is { } cd) { o.Reply(room, cd); return; }
                var (reels, mult, d) = EngageGames.Slots(bet, g, _rnd);
                Ledger.Add(u, d);
                var msg = $"🎰 {m.User}: [ {string.Join(" | ", reels)} ] " + (mult >= g.SlotsJackpot && mult > 0 ? $"JACKPOT +{d:N0}!" : d > 0 ? $"+{d:N0}" : d == 0 ? "break even" : $"{d:N0}");
                o.Reply(room, msg); Toast(msg, now);
                return;
            }
            case "!duel":
            {
                if (!g.DuelEnabled || !CanPlay(m)) return;
                var u = U(m);
                if (a.Length < 2 || EngageGames.ParseBet(a[1], u.Points, g.MaxBet) is not long bet) { o.Reply(room, $"{at} usage: !duel <user> <amount>"); return; }
                var t = Ledger.Find(a[0].TrimStart('@'));
                if (t == null || t.Key == u.Key || IsSelf(t.Id, t.Name)) { o.Reply(room, $"{at} can't duel {a[0]}"); return; }
                if (_duels.ContainsKey(t.Key)) { o.Reply(room, $"{at} {t.Name} already has a duel waiting"); return; }
                if (_duels.Values.Any(d => d.A.Key == u.Key)) { o.Reply(room, $"{at} you already have a duel waiting"); return; }
                if (t.Points < bet) { o.Reply(room, $"{at} {t.Name} only has {Pts(t.Points)}"); return; }
                if (!Ledger.Spend(u, bet)) { o.Reply(room, $"{at} you only have {Pts(u.Points)}"); return; }
                _duels[t.Key] = new DuelState(u, t, bet, now.AddSeconds(Math.Max(10, g.DuelAcceptSeconds)), room);
                o.Reply(room, $"⚔ {m.User} challenges {t.Name} for {Pts(bet)}! {t.Name}: type !accept within {g.DuelAcceptSeconds}s (or !decline)");
                return;
            }
            case "!accept": case "!decline":
            {
                var me = U(m);
                if (!_duels.Remove(me.Key, out var d)) return;
                if (cmd == "!decline" || now > d.Expires) { Ledger.Add(d.A, d.Stake); o.Reply(room, $"⚔ {me.Name} declined {d.A.Name}'s duel; stake refunded"); return; }
                if (!Ledger.Spend(me, d.Stake)) { Ledger.Add(d.A, d.Stake); o.Reply(room, $"⚔ {me.Name} can't cover {Pts(d.Stake)}; duel off, stake refunded"); return; }
                var aWins = EngageGames.Duel(_rnd);
                var (w, l) = aWins ? (d.A, me) : (me, d.A);
                Ledger.Add(w, d.Stake * 2);
                var msg = $"⚔ {w.Name} beats {l.Name} and wins {Pts(d.Stake)}!";
                o.Reply(d.Room, msg); Toast(msg, now);
                return;
            }
            case "!trivia":
            {
                if (!g.TriviaEnabled || !hm) return;
                StartTriviaLocked(room, now, o);
                return;
            }
            case "!heist":
            {
                if (!g.HeistEnabled || !CanPlay(m)) return;
                var u = U(m);
                if (a.Length < 1 || EngageGames.ParseBet(a[0], u.Points, g.MaxBet) is not long bet || bet < g.MinBet) { o.Reply(room, $"{at} usage: !heist <amount> (min {g.MinBet:N0}, max {g.MaxBet:N0})"); return; }
                if (_heist is { } h0 && h0.Crew.Any(c => c.U.Key == u.Key)) { o.Reply(room, $"{at} you're already in the crew"); return; }
                if (_heist == null && GameCooldown(m, "!heist", now) is { } cd) { o.Reply(room, cd); return; }
                if (!Ledger.Spend(u, bet)) { o.Reply(room, $"{at} you only have {Pts(u.Points)}"); return; }
                if (_heist == null)
                {
                    _heist = (now.AddSeconds(Math.Max(10, g.HeistJoinSeconds)), room, new() { (u, bet) });
                    var msg = $"💰 {m.User} is planning a heist! Join with !heist <amount> in the next {g.HeistJoinSeconds}s — bigger crew, better odds";
                    o.Reply(room, msg); Toast("💰 Heist forming — !heist <amount>", now);
                }
                else { _heist.Value.Crew.Add((u, bet)); o.Reply(room, $"💰 {m.User} joins the crew ({_heist.Value.Crew.Count}, {EngageGames.HeistChance(_heist.Value.Crew.Count, g):P0} chance)"); }
                return;
            }
            case "!race":
            {
                if (!g.RaceEnabled || !hm) return;
                if (a.Length > 0 && a[0].ToLowerInvariant() is "cancel" or "stop") { CancelRaceLocked(o); return; }
                StartRaceLocked(room, now, o);
                return;
            }
            case "!joinrace":
            {
                if (_raceBet is not { } rb || !CanPlay(m)) return;
                if (rb.Racers.Count >= Math.Max(2, g.RaceMaxRacers) || rb.Racers.Contains(m.User, StringComparer.OrdinalIgnoreCase)) return;
                rb.Racers.Add(m.User); o.Reply(room, $"🏁 {m.User} is racing! ({rb.Racers.Count}/{g.RaceMaxRacers})");
                return;
            }
            case "!bet":
            {
                if (!CanPlay(m)) return;
                if (_raceBet is not { } rb) { o.Reply(room, $"{at} no race is taking bets"); return; }
                var u = U(m);
                if (a.Length < 2 || EngageGames.ParseBet(a[1], u.Points, g.MaxBet) is not long bet) { o.Reply(room, $"{at} usage: !bet <racer|number> <amount>"); return; }
                int pick = int.TryParse(a[0].TrimStart('#'), out var n) && n >= 1 && n <= rb.Racers.Count ? n - 1 : rb.Racers.FindIndex(r => string.Equals(r, a[0].TrimStart('@'), StringComparison.OrdinalIgnoreCase));
                if (pick < 0) { o.Reply(room, $"{at} racers: " + string.Join(", ", rb.Racers.Select((r, i) => $"{i + 1}. {r}"))); return; }
                var prev = rb.Bets.FindIndex(b => b.U.Key == u.Key);
                if (prev >= 0 && rb.Bets[prev].Pick != pick) { o.Reply(room, $"{at} you already backed {rb.Racers[rb.Bets[prev].Pick]}"); return; }
                if (!Ledger.Spend(u, bet)) { o.Reply(room, $"{at} you only have {Pts(u.Points)}"); return; }
                if (prev >= 0) rb.Bets[prev] = (u, pick, rb.Bets[prev].Stake + bet); else rb.Bets.Add((u, pick, bet));
                o.Reply(room, $"🏁 {m.User} bets {Pts(bet)} on {rb.Racers[pick]}");
                return;
            }
            case "!poll":
            {
                if (!S.Polls.Enabled || !(IsHost(m) || (S.Polls.ModsCanCreate && IsMod(m)))) return;
                var p = Poll.Parse(text, S.Polls.DefaultSeconds);
                if (p == null) { o.Reply(room, $"{at} usage: !poll \"Question\" a | b | c [60s]"); return; }
                StartPollLocked(p.Value.Q, p.Value.Opts, p.Value.Secs, room, now, o);
                return;
            }
            case "!vote":
            {
                if (_poll is not { Closed: false } p) return;
                var i = p.Match(text, false);
                if (i < 0) { o.Reply(room, $"{at} vote 1-{p.Options.Count}"); return; }
                p.Vote(Key(m), i);
                return;
            }
            case "!endpoll": if (hm && _poll is { Closed: false }) EndPollLocked(now, o); return;
            case "!prediction":
            {
                if (!S.Polls.PredictionsEnabled || !IsHost(m) && !IsMod(m)) return;
                var p = Poll.Parse(Regex.Replace(text, @"^!prediction", "!poll", RegexOptions.IgnoreCase), 60);
                if (p == null) { o.Reply(room, $"{at} usage: !prediction \"Question\" outcome1 | outcome2"); return; }
                OpenPredictionLocked(p.Value.Q, p.Value.Opts, room, now, o);
                return;
            }
            case "!predict":
            {
                if (_pred is not { Locked: false } pr || !CanPlay(m)) { if (_pred?.Locked == true) o.Reply(room, $"{at} predictions are locked"); return; }
                var u = U(m);
                if (a.Length < 2 || !int.TryParse(a[0], out var pick) || EngageGames.ParseBet(a[1], u.Points, S.Polls.MaxStake) is not long st) { o.Reply(room, $"{at} usage: !predict <1-{pr.Outcomes.Count}> <amount>"); return; }
                var cur = pr.StakeOf(u.Key); if (cur is { } c && c.Pick != pick - 1) { o.Reply(room, $"{at} you're already on {c.Pick + 1}"); return; }
                if (pick < 1 || pick > pr.Outcomes.Count) { o.Reply(room, $"{at} pick 1-{pr.Outcomes.Count}"); return; }
                if (!Ledger.Spend(u, st)) { o.Reply(room, $"{at} you only have {Pts(u.Points)}"); return; }
                pr.Add(u, pick - 1, st);
                return;
            }
            case "!lock": if (hm && _pred is { Locked: false }) { _pred.Locked = true; o.Reply(_pred.Room, $"🔒 Predictions locked: {_pred.Question} — pool {Pts(_pred.Pool)}"); } return;
            case "!resolve":
            {
                if (!hm || _pred == null || _pred.Result != null) return;
                if (a.Length < 1 || !int.TryParse(a[0], out var w) || w < 1 || w > _pred.Outcomes.Count) { o.Reply(room, $"{at} usage: !resolve <1-{_pred.Outcomes.Count}>"); return; }
                ResolvePredictionLocked(w - 1, now, o);
                return;
            }
            case "!cancelprediction": if (hm && _pred != null && _pred.Result == null) CancelPredictionLocked(o); return;
        }
    }

    HashSet<string> Excludes() { var x = EngageSettings.List(S.Excluded); if (SelfName.Length > 0) x.Add(PointsLedger.Norm(SelfName)); if (SelfId.Length > 0) x.Add(SelfId.ToLowerInvariant()); return x; }

    // ---------- rewards ----------
    void Redeem(ChatMessage m, string[] a, DateTime now, EngageOutput o)
    {
        string at = "@" + m.User;
        if (!CanPlay(m)) return;
        if (a.Length < 1) { o.Reply(m.Room, $"{at} usage: !redeem <reward> — see !rewards"); return; }
        var r = S.Rewards.FirstOrDefault(x => x.Enabled && string.Equals(x.Name, a[0], StringComparison.OrdinalIgnoreCase));
        if (r == null) { o.Reply(m.Room, $"{at} no reward \"{a[0]}\" — see !rewards"); return; }
        var arg = string.Join(' ', a.Skip(1)).Trim(); if (arg.Length > 120) arg = arg[..120];
        if (r.Kind == "Song" && arg.Length == 0) { o.Reply(m.Room, $"{at} usage: !redeem {r.Name} <song title>"); return; }
        if (r.Kind == "BallPit" && Host?.PitActive != true) { o.Reply(m.Room, $"{at} the Ball Pit isn't on right now"); return; }
        if (_rewardLast.TryGetValue(r.Name, out var t) && (now - t).TotalSeconds < r.CooldownSeconds) { o.Reply(m.Room, $"{at} {r.Name} is cooling down ({Math.Ceiling(r.CooldownSeconds - (now - t).TotalSeconds)}s)"); return; }
        var u = U(m);
        if (!Ledger.Spend(u, r.Cost)) { o.Reply(m.Room, $"{at} {r.Name} costs {Pts(r.Cost)}; you have {u.Points:N0}"); return; }
        _rewardLast[r.Name] = now;
        var p = new PendingRedemption { Id = ++_pendingId, User = m.User, UserKey = u.Key, Arg = arg, Room = m.Room, Reward = r, Time = now };
        if (S.RequireApproval || r.RequireApproval)
        {
            _pending.Add(p); o.Reply(m.Room, $"{at} {r.Title} is waiting for the host's approval (#{p.Id})"); Log?.Invoke($"redeem #{p.Id} pending: {p}");
            return;
        }
        Execute(p, now, o);
    }
    void Execute(PendingRedemption p, DateTime now, EngageOutput o)
    {
        var r = p.Reward; var u = Ledger.Get(p.UserKey.StartsWith("id:") ? p.UserKey[3..] : "", p.User);
        void Refund(string why) { Ledger.Add(u, r.Cost); _rewardLast.Remove(r.Name); o.Reply(p.Room, $"@{p.User} {why} — {Pts(r.Cost)} refunded"); }
        switch (r.Kind)
        {
            case "Song":
            {
                var (ok, reply) = Host?.RequestSong(p.User, p.Arg) ?? (false, "song requests aren't available");
                if (!ok) { Refund(reply.Length > 0 ? reply.TrimStart('@').Split(' ', 2).Last() : "couldn't queue that song"); return; }
                o.Reply(p.Room, $"🎵 {p.User} redeemed a song: {reply}"); break;
            }
            case "BallPit":
                if (Host?.BallPit(p.User, r.Arg) != true) { Refund("the Ball Pit isn't on"); return; }
                o.Reply(p.Room, $"⚽ {p.User} redeemed {r.Title}!"); break;
            case "Sound":
                if (r.Arg.Length > 0) Host?.Sound(r.Arg);
                Host?.Alert($"🔊 {p.User}: {r.Title}"); o.Reply(p.Room, $"🔊 {p.User} redeemed {r.Title}!"); break;
            default:
            {
                var txt = (r.Arg.Length > 0 ? r.Arg : "⭐ {user} redeemed " + r.Title).Replace("{user}", p.User).Replace("{arg}", p.Arg.Length > 0 ? p.Arg : r.Title);
                Host?.Alert(txt); o.Reply(p.Room, $"⭐ {p.User} redeemed {r.Title}!"); break;
            }
        }
        Toast($"⭐ {p.User} redeemed {r.Title}", now);
        Log?.Invoke($"redeemed: {p}");
    }
    public EngageOutput Approve(int id, DateTime now) { lock (_lk) return ApproveLocked(id, now); }
    public EngageOutput Deny(int id) { lock (_lk) return DenyLocked(id); }
    EngageOutput ApproveLocked(int id, DateTime now)
    {
        var o = new EngageOutput(); var p = _pending.FirstOrDefault(x => x.Id == id); if (p == null) return o;
        _pending.Remove(p); Execute(p, now, o); Changed?.Invoke(); return o;
    }
    EngageOutput DenyLocked(int id)
    {
        var o = new EngageOutput(); var p = _pending.FirstOrDefault(x => x.Id == id); if (p == null) return o;
        _pending.Remove(p); Ledger.Add(Ledger.Get(p.UserKey.StartsWith("id:") ? p.UserKey[3..] : "", p.User), p.Reward.Cost);
        o.Reply(p.Room, $"@{p.User} {p.Reward.Title} wasn't approved — {Pts(p.Reward.Cost)} refunded"); Changed?.Invoke(); return o;
    }

    // ---------- trivia ----------
    public EngageOutput StartTrivia(string room, DateTime now) { var o = new EngageOutput(); lock (_lk) StartTriviaLocked(room, now, o); return o; }
    void StartTriviaLocked(string room, DateTime now, EngageOutput o)
    {
        if (_trivia != null) return;
        _pack ??= TriviaPack.Load(S.Games, s => Log?.Invoke(s));
        if (_pack.Count == 0) return;
        if (_asked.Count >= _pack.Count) _asked.Clear();
        int i; do i = _rnd.Next(_pack.Count); while (_asked.Contains(i));
        _asked.Add(i);
        var q = _pack[i];
        _trivia = (q, now.AddSeconds(Math.Max(10, S.Games.TriviaSeconds)), room);
        o.Reply(room, $"❓ Trivia ({S.Games.TriviaSeconds}s, {Pts(S.Games.TriviaReward)}): {q.Q}");
    }
    public void ReloadTrivia() { lock (_lk) _pack = null; }
    void TriviaWin(ChatMessage m, string room, DateTime now, EngageOutput o)
    {
        var q = _trivia!.Value.Q; _trivia = null;
        Ledger.Add(U(m), S.Games.TriviaReward, true);
        var msg = $"✅ {m.User} got it: {q.A[0]} (+{Pts(S.Games.TriviaReward)})";
        o.Reply(room, msg); Toast(msg, now);
    }

    // ---------- polls / predictions ----------
    public EngageOutput StartPoll(string q, List<string> opts, int secs, string room, DateTime now) { var o = new EngageOutput(); lock (_lk) StartPollLocked(q, opts, secs, room, now, o); Changed?.Invoke(); return o; }
    void StartPollLocked(string q, List<string> opts, int secs, string room, DateTime now, EngageOutput o)
    {
        if (_poll is { Closed: false }) EndPollLocked(now, o);
        _poll = new Poll { Question = q, Options = opts, Started = now, Ends = now.AddSeconds(secs), Room = room }; _pollFinal = false;
        o.Reply(room, $"📊 Poll ({secs}s): {q} — " + string.Join(" · ", opts.Select((x, i) => $"{i + 1}. {x}")) + " — vote with !vote <number>" + (S.Polls.BareVotes ? " or just the number" : ""));
    }
    public EngageOutput EndPoll(DateTime now) { var o = new EngageOutput(); lock (_lk) if (_poll is { Closed: false }) EndPollLocked(now, o); Changed?.Invoke(); return o; }
    void EndPollLocked(DateTime now, EngageOutput o)
    {
        var p = _poll!; p.Closed = true; p.Ends = now; _pollFinal = true; _pollShowUntil = now.AddSeconds(Math.Max(3, S.Overlay.ResultSeconds));
        var r = p.ResultText(); PollHistory.Insert(0, $"{now:yyyy-MM-dd HH:mm} {r}"); if (PollHistory.Count > 200) PollHistory.RemoveAt(PollHistory.Count - 1);
        o.Reply(p.Room, r);
    }
    public EngageOutput OpenPrediction(string q, List<string> outcomes, string room, DateTime now) { var o = new EngageOutput(); lock (_lk) OpenPredictionLocked(q, outcomes, room, now, o); Changed?.Invoke(); return o; }
    void OpenPredictionLocked(string q, List<string> outcomes, string room, DateTime now, EngageOutput o)
    {
        if (_pred != null && _pred.Result == null) { o.Reply(room, "Resolve or cancel the current prediction first."); return; }
        _pred = new Prediction { Question = q, Outcomes = outcomes, Room = room, Opened = now };
        o.Reply(room, $"🔮 Prediction: {q} — " + string.Join(" · ", outcomes.Select((x, i) => $"{i + 1}. {x}")) + $" — stake {Cur} with !predict <number> <amount>");
    }
    public EngageOutput LockPrediction() { var o = new EngageOutput(); lock (_lk) if (_pred is { Locked: false }) { _pred.Locked = true; o.Reply(_pred.Room, $"🔒 Predictions locked: {_pred.Question} — pool {Pts(_pred.Pool)}"); } Changed?.Invoke(); return o; }
    public EngageOutput ResolvePrediction(int winner, DateTime now) { var o = new EngageOutput(); lock (_lk) if (_pred != null && _pred.Result == null) ResolvePredictionLocked(winner, now, o); Changed?.Invoke(); return o; }
    void ResolvePredictionLocked(int w, DateTime now, EngageOutput o)
    {
        var pr = _pred!; var pool = pr.Pool; var pays = pr.Resolve(w);
        foreach (var (u, pay, _) in pays) if (pay > 0) Ledger.Add(u, pay);
        var winners = pays.Where(x => x.Payout > 0 && pr.PerOutcome()[w] > 0).OrderByDescending(x => x.Payout).ToList();
        var msg = pr.PerOutcome()[w] == 0 ? $"🔮 {pr.Question}: {pr.Outcomes[w]}! Nobody picked it — all stakes refunded"
            : $"🔮 {pr.Question}: {pr.Outcomes[w]}! {winners.Count} winner(s) share {Pts(pool)}" + (winners.Count > 0 ? " — top: " + string.Join(", ", winners.Take(3).Select(x => $"{x.U.Name} +{x.Payout - x.Stake:N0}")) : "");
        o.Reply(pr.Room, msg); Toast($"🔮 {pr.Outcomes[w]} wins!", now);
        PollHistory.Insert(0, $"{now:yyyy-MM-dd HH:mm} {msg}");
    }
    public EngageOutput CancelPrediction() { var o = new EngageOutput(); lock (_lk) if (_pred != null && _pred.Result == null) CancelPredictionLocked(o); Changed?.Invoke(); return o; }
    void CancelPredictionLocked(EngageOutput o) { foreach (var (u, st) in _pred!.Refund()) Ledger.Add(u, st); o.Reply(_pred.Room, $"🔮 Prediction cancelled — all stakes refunded"); _pred = null; }

    // ---------- race ----------
    public EngageOutput StartRace(string room, DateTime now) { var o = new EngageOutput(); lock (_lk) StartRaceLocked(room, now, o); Changed?.Invoke(); return o; }
    void StartRaceLocked(string room, DateTime now, EngageOutput o)
    {
        if (_race != null || _raceBet != null) return;
        if (Host?.PitActive != true) { o.Reply(room, "The Ball Pit race needs the Ball Pit on."); return; }
        _raceBet = (now.AddSeconds(Math.Max(10, S.Games.RaceBetSeconds)), room, new List<string>(), new());
        o.Reply(room, $"🏁 Ball Pit race in {S.Games.RaceBetSeconds}s! !joinrace to race, !bet <racer> <amount> to back one — winners share the pot");
        Toast("🏁 Race! !joinrace · !bet <racer> <amount>", now);
    }
    void CancelRaceLocked(EngageOutput o)
    {
        var bets = _raceBet?.Bets ?? _race?.Bets; var room = _raceBet?.Room ?? _race?.Room ?? "";
        if (bets == null) return;
        foreach (var b in bets) Ledger.Add(b.U, b.Stake);
        _raceBet = null; if (_race != null) { _race = null; Host?.Race(null); }
        o.Reply(room, "🏁 Race cancelled — bets refunded");
    }
    /// <summary>Fills the field from the Ball Pit (random, excluding the host) up to RaceMaxRacers.</summary>
    List<string> FillRacers(List<string> joined)
    {
        int max = Math.Clamp(S.Games.RaceMaxRacers, 2, 12);
        var r = joined.Take(max).ToList();
        var pool = (Host?.PitNames() ?? Array.Empty<string>()).Where(n => !r.Contains(n, StringComparer.OrdinalIgnoreCase) && !IsSelf("", n)).ToList();
        while (r.Count < max && pool.Count > 0) { var i = _rnd.Next(pool.Count); r.Add(pool[i]); pool.RemoveAt(i); }
        return r;
    }

    // ---------- timers ----------
    /// <summary>Call about once a second: watch-time points, poll/duel/heist/trivia/race timeouts, scheduled trivia.</summary>
    public EngageOutput Tick(DateTime now)
    {
        var o = new EngageOutput(); bool changed = false;
        lock (_lk)
        {
            if (!S.Enabled) return o;
            if (Accrual.Tick(now) > 0) changed = true;
            if (_poll is { Closed: false } p && now >= p.Ends) { EndPollLocked(now, o); changed = true; }
            foreach (var (k, d) in _duels.Where(x => now > x.Value.Expires).ToList()) { _duels.Remove(k); Ledger.Add(d.A, d.Stake); o.Reply(d.Room, $"⚔ {d.B.Name} didn't accept {d.A.Name}'s duel; stake refunded"); }
            if (_trivia is { } t && now > t.Ends) { _trivia = null; o.Reply(t.Room, $"⌛ Time's up! The answer was: {t.Q.A[0]}"); changed = true; }
            if (S.Games.TriviaEnabled && S.Games.TriviaEveryMinutes > 0 && Accrual.Live && now >= _nextTrivia)
            { _nextTrivia = now.AddMinutes(S.Games.TriviaEveryMinutes); StartTriviaLocked(DefaultRoom?.Invoke() ?? "", now, o); changed = true; }
            if (_heist is { } h && now >= h.Ends)
            {
                _heist = null; changed = true;
                var chance = EngageGames.HeistChance(h.Crew.Count, S.Games);
                if (EngageGames.Heist(h.Crew.Count, S.Games, _rnd))
                {
                    foreach (var (u, st) in h.Crew) Ledger.Add(u, (long)Math.Floor(st * S.Games.HeistMultiplier));
                    var msg = $"💰 Heist success ({h.Crew.Count} crew, {chance:P0})! Everyone gets ×{S.Games.HeistMultiplier:0.##}: " + string.Join(", ", h.Crew.Select(c => $"{c.U.Name} +{(long)Math.Floor(c.Stake * S.Games.HeistMultiplier) - c.Stake:N0}"));
                    o.Reply(h.Room, msg); Toast($"💰 Heist success! ({h.Crew.Count} crew)", now);
                }
                else { o.Reply(h.Room, $"🚨 The heist failed ({h.Crew.Count} crew, {chance:P0}) — the crew lost {Pts(h.Crew.Sum(c => c.Stake))}"); Toast("🚨 Heist failed!", now); }
            }
            if (_raceBet is { } rb && now >= rb.Ends)
            {
                _raceBet = null; changed = true;
                var racers = FillRacers(rb.Racers);
                if (racers.Count < 2) { foreach (var b in rb.Bets) Ledger.Add(b.U, b.Stake); o.Reply(rb.Room, "🏁 Not enough racers — bets refunded"); }
                else
                {
                    // bets placed by name on racers who were filled in later still count (index by name)
                    var bets = rb.Bets.Select(b => (b.U, Pick: racers.FindIndex(r => string.Equals(r, rb.Racers.ElementAtOrDefault(b.Pick), StringComparison.OrdinalIgnoreCase)), b.Stake)).ToList();
                    var g = new RaceGame(racers, _rnd, now.ToUniversalTime().AddSeconds(3), Math.Clamp(S.Games.RaceSeconds, 4, 60));
                    _race = (g, rb.Room, bets);
                    Host?.Race(g);
                    o.Reply(rb.Room, $"🏁 And they're off! " + string.Join(" · ", racers.Select((r, i) => $"{i + 1}. {r}")) + $" — pot {Pts(bets.Sum(b => b.Stake))}");
                }
            }
            if (_race is { } race && race.G.Finished(now.ToUniversalTime()))
            {
                _race = null; changed = true; Host?.Race(null);
                var w = race.G.Winner; var winner = race.G.Racers[w];
                var pay = EngageGames.Parimutuel(race.Bets.Select(b => (b.U.Key, b.Pick, b.Stake)), w);
                foreach (var b in race.Bets.GroupBy(b => b.U.Key)) if (pay.TryGetValue(b.Key, out var v) && v > 0) Ledger.Add(b.First().U, v);
                var wu = Ledger.Find(winner); if (wu != null && S.Games.RaceWinnerBonus > 0 && !IsSelf(wu.Id, wu.Name)) Ledger.Add(wu, S.Games.RaceWinnerBonus, true);
                var backers = race.Bets.Where(b => b.Pick == w).Select(b => b.U.Name).Distinct().ToList();
                o.Reply(race.Room, $"🏆 {winner} wins the race!" + (backers.Count > 0 ? $" Backers ({string.Join(", ", backers.Take(5))}) share {Pts(race.Bets.Sum(b => b.Stake))}" : race.Bets.Count > 0 ? " Nobody backed them — bets refunded" : ""));
                Toast($"🏆 {winner} wins the race!", now);
            }
        }
        if (changed || o.Replies.Count > 0) Changed?.Invoke();
        return o;
    }
}
