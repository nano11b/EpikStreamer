using EpikStreamer.Core;
using SkiaSharp;
/// <summary>v2.8 Engage unit tests (dotnet T engage): points, anti-spam, rewards, games (seeded), polls, predictions, persistence.</summary>
static class EngageTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    sealed class FakeHost : IEngageHost
    {
        public bool Pit = true; public List<string> Log = new(); public RaceGame? RaceNow; public List<string> Names = new() { "pitA", "pitB", "pitC" };
        public (bool Ok, string Reply) RequestSong(string user, string q) { Log.Add($"song {user} {q}"); return q.Contains("nosuch") ? (false, $"@{user} no match for \"{q}\"") : (true, $"\"{q}\" is up next"); }
        public bool BallPit(string user, string cmd) { Log.Add($"pit {user} {cmd}"); return Pit; }
        public bool PitActive => Pit;
        public IReadOnlyList<string> PitNames() => Names;
        public void Alert(string t) => Log.Add("alert " + t);
        public void Sound(string p) => Log.Add("sound " + p);
        public void Race(RaceGame? r) { RaceNow = r; Log.Add(r == null ? "race end" : "race start " + string.Join(",", r.Racers)); }
    }
    static int _id;
    static ChatMessage M(string user, string text, string uid = "", string room = "Lobby", bool self = false, bool bot = false) => new(user, text, "m" + (++_id), bot, self, room, "", uid);

    public static int Run()
    {
        var dir = "/tmp/epiktest/engage"; if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
        var t0 = new DateTime(2026, 10, 27, 20, 0, 0);

        // ---------- accrual from the viewers acks ----------
        {
            var s = new EngageSettings(); var l = new PointsLedger(); var e = new EngageEngine(s, l, new Random(1)) { SelfName = "sam", SelfId = "self0" };
            e.StartStream(t0);
            e.OnViewers(new[] { ("u1", "alice"), ("u2", "bob"), ("self0", "sam") }, t0.AddSeconds(5));
            e.Tick(t0.AddSeconds(30)); Check(l.Count == 0, "no watch points before a full minute");
            e.Tick(t0.AddSeconds(61));
            Check(l.Balance("u1", "alice") == 10 && l.Balance("u2", "bob") == 10, "10/min for each viewer from the ack list");
            Check(l.Find("sam") == null, "the host (self) never earns");
            e.OnViewers(new[] { ("u1", "alice") }, t0.AddSeconds(90));
            e.Tick(t0.AddSeconds(121));
            Check(l.Balance("u1", "alice") == 20 && l.Balance("u2", "bob") == 10, "a viewer who left stops earning");
            e.Tick(t0.AddHours(3));
            Check(l.Balance("u1", "alice") < 60, "no back-pay after the app slept for hours (stale presence ignored)");
            e.EndStream(); var before = l.Balance("u1", "alice"); e.Tick(t0.AddHours(4)); Check(l.Balance("u1", "alice") == before, "no watch points while offline");
            Check(l.Get("u1", "alice").WatchMinutes >= 2, "watch minutes counted");
        }
        // ---------- chat bonus + anti-spam + first chatter ----------
        {
            var s = new EngageSettings { ChatBonus = 5, ChatCooldownSeconds = 30, ChatMaxPerMinute = 10, FirstChatterBonus = 100 };
            var l = new PointsLedger(); var e = new EngageEngine(s, l, new Random(2)) { SelfName = "sam" };
            var o0 = e.Handle(M("alice", "hi before live", "u1"), t0); Check(l.Count == 0 || l.Balance("u1", "alice") == 0, "no chat points while offline");
            e.StartStream(t0);
            var o = e.Handle(M("sam", "hello chat", "self0", self: true), t0.AddSeconds(1));
            Check(o.Replies.Count == 0, "the host isn't first chatter");
            o = e.Handle(M("alice", "hello!", "u1"), t0.AddSeconds(2));
            Check(l.Balance("u1", "alice") == 105 && o.Replies.Any(r => r.Text.Contains("first chatter") && r.Room == "Lobby"), "first chatter bonus + chat bonus, announced in the source room");
            e.Handle(M("alice", "second line", "u1"), t0.AddSeconds(10)); Check(l.Balance("u1", "alice") == 105, "chat cooldown");
            e.Handle(M("alice", "hello!", "u1"), t0.AddSeconds(40)); Check(l.Balance("u1", "alice") == 105, "repeated message earns nothing");
            e.Handle(M("alice", "new thought", "u1"), t0.AddSeconds(41)); Check(l.Balance("u1", "alice") == 110, "new message after the cooldown earns");
            s.ChatCooldownSeconds = 0;
            e.Handle(M("alice", "a", "u1"), t0.AddSeconds(42)); e.Handle(M("alice", "b", "u1"), t0.AddSeconds(43)); e.Handle(M("alice", "c", "u1"), t0.AddSeconds(44));
            Check(l.Balance("u1", "alice") == 110, "max chat points per minute (10/min reached at 0:41)");
            e.Handle(M("alice", "d", "u1"), t0.AddSeconds(63)); Check(l.Balance("u1", "alice") == 115, "next minute earns again");
            e.Handle(M("bob", "yo", "u2"), t0.AddSeconds(45)); Check(l.Balance("u2", "bob") == 5, "only one first-chatter bonus per stream");
            e.Handle(M("botty", "spam", "u9", bot: true), t0.AddSeconds(46)); Check(l.Find("botty") == null, "bots never earn");
            s.Excluded = "carol"; e.Handle(M("carol", "hey", "u3"), t0.AddSeconds(47)); Check(l.Find("carol") == null, "excluded users never earn");
            e.Handle(M("alice", "dup id", "u1") with { Id = "dupe" }, t0.AddSeconds(70)); var b1 = l.Balance("u1", "alice");
            e.Handle(M("alice", "dup id 2", "u1") with { Id = "dupe" }, t0.AddSeconds(140)); Check(l.Balance("u1", "alice") == b1, "the same message id is handled once");
        }
        // ---------- streak ----------
        {
            var s = new EngageSettings { StreakBonus = 25, StreakMaxDays = 7, FirstChatterBonus = 0, ChatBonus = 0 };
            var l = new PointsLedger(); var e = new EngageEngine(s, l);
            var u = l.Get("u1", "alice"); u.LastDay = t0.AddDays(-1).ToString("yyyy-MM-dd"); u.Streak = 3;
            var v = l.Get("u2", "bob"); v.LastDay = t0.AddDays(-3).ToString("yyyy-MM-dd"); v.Streak = 5;
            e.StartStream(t0); e.Handle(M("alice", "hi", "u1"), t0); e.Handle(M("bob", "hi", "u2"), t0);
            Check(u.Streak == 4 && u.Points == 100, "consecutive day → streak 4 → 4×25 bonus");
            Check(v.Streak == 1 && v.Points == 0, "missed days reset the streak");
            e.Handle(M("alice", "again", "u1"), t0.AddMinutes(5)); Check(u.Points == 100, "streak bonus once per stream");
            e.EndStream(); e.StartStream(t0.AddHours(2)); e.Handle(M("alice", "2nd stream", "u1"), t0.AddHours(2)); Check(u.Points == 100 && u.Streak == 4, "second stream the same day: no extra streak");
        }
        // ---------- points commands, host/mods ----------
        {
            var s = new EngageSettings { Moderators = "modmike, 64aa00000000000000000001", UserCooldownSeconds = 0 };
            var l = new PointsLedger(); var e = new EngageEngine(s, l) { SelfName = "sam", SelfId = "self0" };
            l.Add(l.Get("u1", "alice"), 300); l.Add(l.Get("u2", "bob"), 50);
            var o = e.Handle(M("alice", "!points", "u1", room: "Bar"), t0);
            Check(o.Handled && o.Replies.Single().Room == "Bar" && o.Replies[0].Text.Contains("300 Epik Points") && o.Replies[0].Text.Contains("#1"), "!points answers in the source room with rank");
            o = e.Handle(M("alice", "!give bob 100", "u1"), t0.AddSeconds(1));
            Check(l.Balance("u1", "alice") == 200 && l.Balance("u2", "bob") == 150, "!give moves points");
            e.Handle(M("bob", "!give alice 9999", "u2"), t0.AddSeconds(2)); Check(l.Balance("u2", "bob") == 150, "!give can't overspend");
            s.GiveEnabled = false; e.Handle(M("alice", "!give bob 10", "u1"), t0.AddSeconds(3)); Check(l.Balance("u2", "bob") == 150, "!give toggle");
            e.Handle(M("bob", "!addpoints bob 1000", "u2"), t0.AddSeconds(4)); Check(l.Balance("u2", "bob") == 150, "viewers can't !addpoints");
            e.Handle(M("sam", "!addpoints bob 1000", "self0", self: true), t0.AddSeconds(5)); Check(l.Balance("u2", "bob") == 1150, "host !addpoints (self message)");
            e.Handle(M("modmike", "!removepoints bob 150", "u7"), t0.AddSeconds(6)); Check(l.Balance("u2", "bob") == 1000, "moderator by name");
            e.Handle(M("renamed", "!removepoints bob 5000", "64aa00000000000000000001"), t0.AddSeconds(7)); Check(l.Balance("u2", "bob") == 0, "moderator by userId; never below zero");
            o = e.Handle(M("alice", "!top", "u1"), t0.AddSeconds(8)); Check(o.Replies[0].Text.StartsWith("🏆 Top") && e.View(t0.AddSeconds(9)).Leaderboard?.Count >= 1, "!top replies + leaderboard overlay");
            Check(e.View(t0.AddSeconds(60)).Leaderboard == null, "leaderboard overlay times out");
            Check(e.Triggers().Contains("!points") && !e.Triggers().Contains("!give"), "!help list follows toggles");
        }
        // ---------- anti-abuse guard ----------
        {
            var s = new EngageSettings { UserCooldownSeconds = 3, GlobalCommandsPerMinute = 5 };
            var l = new PointsLedger(); var e = new EngageEngine(s, l) { SelfName = "sam" };
            int n1 = e.Handle(M("alice", "!points", "u1"), t0).Replies.Count + e.Handle(M("alice", "!points", "u1"), t0.AddSeconds(1)).Replies.Count;
            Check(n1 == 1, "per-user command cooldown");
            int n = 0; for (int i = 0; i < 10; i++) n += e.Handle(M("v" + i, "!points", "x" + i), t0.AddSeconds(5 + i)).Replies.Count;
            Check(n == 4, $"global rate limit (5/min incl. the first) → {n}");
            Check(e.Handle(M("sam", "!top", "", self: true), t0.AddSeconds(20)).Replies.Count == 1, "the host bypasses the rate limit");
            Check(e.Handle(M("b0t", "!points", "bb", bot: true), t0.AddSeconds(90)).Handled == false, "bot messages ignored entirely");
        }
        // ---------- redemptions ----------
        {
            var s = new EngageSettings { UserCooldownSeconds = 0 }; var host = new FakeHost();
            var l = new PointsLedger(); var e = new EngageEngine(s, l) { SelfName = "sam", Host = host };
            var a = l.Get("u1", "alice"); l.Add(a, 2000);
            var o = e.Handle(M("alice", "!redeem song Blue Monday", "u1", room: "Music"), t0);
            Check(a.Points == 1500 && host.Log.Contains("song alice Blue Monday") && o.Replies[0].Room == "Music", "!redeem song → request queue, cost taken");
            o = e.Handle(M("alice", "!redeem song again", "u1"), t0.AddSeconds(5)); Check(a.Points == 1500 && o.Replies[0].Text.Contains("cooling down"), "per-reward cooldown");
            o = e.Handle(M("alice", "!redeem song nosuch thing", "u1"), t0.AddSeconds(70));
            Check(a.Points == 1500 && o.Replies[0].Text.Contains("refunded"), "failed song request refunded");
            e.Handle(M("alice", "!redeem bigball", "u1"), t0.AddSeconds(80)); Check(a.Points == 1400 && host.Log.Contains("pit alice !bigball"), "!redeem bigball → Ball Pit effect");
            host.Pit = false; o = e.Handle(M("alice", "!redeem shake", "u1"), t0.AddSeconds(90)); Check(a.Points == 1400 && o.Replies[0].Text.Contains("isn't on"), "Ball Pit rewards need the pit");
            host.Pit = true; e.Handle(M("alice", "!redeem shake", "u1"), t0.AddSeconds(91)); Check(a.Points == 1150 && host.Log.Contains("pit alice !shake"), "!redeem shake");
            e.Handle(M("alice", "!redeem hype let's go", "u1"), t0.AddSeconds(92)); Check(host.Log.Contains("alert 🔥 alice says: let's go"), "custom alert text with {user}/{arg}");
            o = e.Handle(M("alice", "!redeem airhorn", "u1"), t0.AddSeconds(93)); Check(o.Replies[0].Text.Contains("no reward"), "disabled rewards can't be redeemed");
            s.Rewards.First(r => r.Name == "airhorn").Enabled = true; s.Rewards.First(r => r.Name == "airhorn").Arg = "/tmp/horn.wav";
            e.Handle(M("alice", "!redeem airhorn", "u1"), t0.AddSeconds(94)); Check(host.Log.Contains("sound /tmp/horn.wav"), "sound reward");
            var bob = l.Get("u2", "bob"); l.Add(bob, 100); o = e.Handle(M("bob", "!redeem song x", "u2"), t0.AddSeconds(200)); Check(bob.Points == 100 && o.Replies[0].Text.Contains("costs"), "can't redeem without enough points");
            s.RequireApproval = true; var pts = a.Points;
            o = e.Handle(M("alice", "!redeem bigball", "u1", room: "Bar"), t0.AddSeconds(300));
            Check(a.Points == pts - 100 && e.Pending().Count == 1 && o.Replies[0].Text.Contains("approval (#"), "approval queue holds the points");
            var id = e.Pending()[0].Id; o = e.Handle(M("sam", $"!deny {id}", "", self: true), t0.AddSeconds(301));
            Check(a.Points == pts && e.Pending().Count == 0 && o.Replies[0].Room == "Bar", "!deny refunds (reply in the redeemer's room)");
            e.Handle(M("alice", "!redeem hype yay", "u1"), t0.AddSeconds(400)); id = e.Pending()[0].Id; host.Log.Clear();
            o = e.Approve(id, t0.AddSeconds(401)); Check(host.Log.Any(x => x.StartsWith("alert")) && a.Points == pts - 300, "approve runs the reward");
        }
        // ---------- games (seeded) ----------
        {
            var g = new GameSettings();
            var r1 = new Random(42); var r2 = new Random(42);
            var res = Enumerable.Range(0, 20).Select(_ => EngageGames.Gamble(100, g, r1)).ToList();
            Check(res.SequenceEqual(Enumerable.Range(0, 20).Select(_ => EngageGames.Gamble(100, g, r2))), "gamble is deterministic with a seed");
            Check(res.All(x => x.Delta == (x.Won ? 100 : -100)), "gamble pays stake×(2−1) / loses the stake");
            var rr = new Random(7); int wins = 0; for (int i = 0; i < 100000; i++) if (EngageGames.Gamble(10, g, rr).Won) wins++;
            Check(Math.Abs(wins / 100000.0 - 0.45) < 0.01, $"gamble odds ≈ 45% ({wins / 1000.0:0.0}%)");
            var rs = new Random(9); double ret = 0; for (int i = 0; i < 200000; i++) ret += EngageGames.Slots(10, g, rs).Delta;
            double ev = (90.0 / 216 * 1.5 + 5.0 / 216 * 8 + 1.0 / 216 * 25) - 1;
            Check(Math.Abs(ret / 200000 / 10 - ev) < 0.02, $"slots return matches the configured table (ev {ev:0.000}, got {ret / 2000000:0.000})");
            var sl = EngageGames.Slots(10, g, new Random(3)); Check(sl.Reels.Length == 3 && sl.Reels.All(EngageGames.SlotSymbols.Contains), "slots reels");
            Check(EngageGames.ParseBet("all", 1234, 1000) == 1000 && EngageGames.ParseBet("50%", 200, 0) == 100 && EngageGames.ParseBet("1k", 5000, 0) == 1000 && EngageGames.ParseBet("-5", 10, 0) == null && EngageGames.ParseBet("abc", 10, 0) == null, "bet parsing (all/%/k, caps, invalid)");
            Check(Math.Abs(EngageGames.HeistChance(1, g) - 0.35) < 1e-9 && Math.Abs(EngageGames.HeistChance(5, g) - 0.59) < 1e-9 && EngageGames.HeistChance(50, g) == 0.8, "heist chance scales with crew, capped");

            var s = new EngageSettings { UserCooldownSeconds = 0 }; s.Games.GameCooldownSeconds = 0; var host = new FakeHost();
            var l = new PointsLedger(); var e = new EngageEngine(s, l, new Random(5)) { SelfName = "sam", Host = host };
            var a = l.Get("u1", "alice"); var b = l.Get("u2", "bob"); var c = l.Get("u3", "carol"); l.Add(a, 1000); l.Add(b, 1000); l.Add(c, 1000);
            var expect = new Random(5); var eg = EngageGames.Gamble(200, s.Games, expect);
            e.Handle(M("alice", "!gamble 200", "u1"), t0); Check(a.Points == 1000 + eg.Delta, "engine !gamble uses the injected RNG");
            var pa = a.Points; e.Handle(M("alice", "!gamble 5", "u1"), t0.AddSeconds(1)); Check(a.Points == pa, "min bet");
            s.Games.GameCooldownSeconds = 20; e.Handle(M("bob", "!slots", "u2"), t0.AddSeconds(2)); var pb = b.Points;
            var oc = e.Handle(M("bob", "!slots", "u2"), t0.AddSeconds(3)); Check(b.Points == pb && oc.Replies[0].Text.Contains("again in"), "per-game cooldown"); s.Games.GameCooldownSeconds = 0;
            Check(e.Handle(M("sam", "!gamble 10", "", self: true), t0).Replies.Count == 0, "the host can't play");
            // duel
            a.Points = 1000; b.Points = 1000;
            e.Handle(M("alice", "!duel bob 300", "u1"), t0.AddSeconds(10)); Check(a.Points == 700, "duel stake held");
            var o = e.Handle(M("bob", "!accept", "u2"), t0.AddSeconds(20));
            Check(a.Points + b.Points == 2000 && (a.Points == 1300 || b.Points == 1300) && o.Replies[0].Text.Contains("beats"), "duel resolves, winner takes the stake");
            e.Handle(M("alice", "!duel carol 100", "u1"), t0.AddSeconds(30)); var pa2 = a.Points;
            o = e.Tick(t0.AddSeconds(95)); Check(a.Points == pa2 + 100 && o.Replies[0].Text.Contains("didn't accept"), "unanswered duel refunds after 60 s");
            e.Handle(M("alice", "!duel carol 100", "u1"), t0.AddSeconds(100)); e.Handle(M("carol", "!decline", "u3"), t0.AddSeconds(101)); Check(a.Points == pa2 + 100, "!decline refunds");
            // heist (seeded: same draws as a fresh Random(77) after the engine's previous draws is hard → just check conservation)
            var e2 = new EngageEngine(s, l, new Random(77)) { SelfName = "sam", Host = host };
            a.Points = 1000; b.Points = 1000; c.Points = 1000;
            e2.Handle(M("alice", "!heist 100", "u1"), t0); e2.Handle(M("bob", "!heist 200", "u2"), t0.AddSeconds(5)); e2.Handle(M("carol", "!heist 300", "u3"), t0.AddSeconds(6));
            Check(a.Points == 900 && b.Points == 800 && c.Points == 700 && e2.View(t0.AddSeconds(7)).Heist?.Crew == 3, "heist stakes held, crew of 3");
            var hr = new Random(77); bool success = EngageGames.Heist(3, s.Games, hr);
            o = e2.Tick(t0.AddSeconds(61));
            Check(success ? (a.Points == 1080 && b.Points == 1160 && c.Points == 1240) : (a.Points == 900 && c.Points == 700), $"heist resolves after 60 s ({(success ? "success ×1.8" : "fail")}) — matches the seeded draw");
            // trivia
            s.Games.TriviaBuiltIn = false; File.WriteAllText(dir + "/pack.json", "{\"name\":\"t\",\"questions\":[{\"q\":\"Color of the sky?\",\"a\":[\"Blue\",\"azure\"]}]}"); s.Games.TriviaPacks = new() { dir + "/pack.json" };
            var e3 = new EngageEngine(s, l, new Random(1)) { SelfName = "sam", Host = host };
            o = e3.Handle(M("sam", "!trivia", "", self: true, room: "Bar"), t0); Check(o.Replies[0].Text.Contains("Color of the sky?") && o.Replies[0].Room == "Bar", "custom trivia pack; host-triggered");
            pa = a.Points; e3.Handle(M("bob", "red", "u2"), t0.AddSeconds(3)); o = e3.Handle(M("alice", "The BLUE!", "u1"), t0.AddSeconds(4));
            Check(a.Points == pa + 100 && o.Replies[0].Text.Contains("alice got it"), "first correct answer wins (normalised)");
            o = e3.Handle(M("carol", "blue", "u3"), t0.AddSeconds(5)); Check(o.Replies.Count == 0, "only the first correct answer");
            e3.Handle(M("sam", "!trivia", "", self: true), t0.AddSeconds(10)); o = e3.Tick(t0.AddSeconds(45)); Check(o.Replies[0].Text.Contains("answer was: blue"), "trivia times out with the answer");
            Check(TriviaPack.BuiltIn.Length >= 20 && TriviaPack.Parse("[{\"question\":\"x?\",\"answer\":\"Y\"}]").Single().A[0] == "y", "built-in pack + array pack format");
            s.Games.TriviaEveryMinutes = 5; s.Games.TriviaBuiltIn = true; s.Games.TriviaPacks.Clear();
            var e4 = new EngageEngine(s, l, new Random(1)) { SelfName = "sam", Host = host, DefaultRoom = () => "Lobby" }; e4.StartStream(t0);
            Check(e4.Tick(t0.AddMinutes(4)).Replies.Count == 0 && e4.Tick(t0.AddMinutes(5)).Replies.Any(r => r.Text.StartsWith("❓")), "auto trivia every N minutes while live");
            s.Games.TriviaEveryMinutes = 0;
        }
        // ---------- race ----------
        {
            var rg1 = new RaceGame(new[] { "a", "b", "c", "d" }, new Random(11), t0, 10); var rg2 = new RaceGame(new[] { "a", "b", "c", "d" }, new Random(11), t0, 10);
            Check(rg1.Winner == rg2.Winner && rg1.FinishTimes.SequenceEqual(rg2.FinishTimes), "race plan is deterministic with a seed");
            Check(rg1.FinishTimes.All(f => f > 4 && f < 25), $"race lasts a sensible time ({string.Join(",", rg1.FinishTimes.Select(f => f.ToString("0.0")))})");
            Check(Math.Abs(rg1.Progress(rg1.Winner, rg1.FinishTimes[rg1.Winner]) - 1) < 1e-6 && rg1.Progress(0, 0) == 0 && Enumerable.Range(0, 4).All(i => rg1.Progress(i, 2) <= rg1.Progress(i, 3)), "progress monotonic, winner reaches 1 at its finish time");
            var wc = new int[4]; var rr = new Random(3); for (int i = 0; i < 4000; i++) wc[new RaceGame(new[] { "a", "b", "c", "d" }, rr, t0).Winner]++;
            Check(wc.All(x => x > 800 && x < 1200), "race is fair across lanes: " + string.Join("/", wc));

            var s = new EngageSettings { UserCooldownSeconds = 0 }; s.Games.RaceBetSeconds = 30; s.Games.RaceMaxRacers = 4; var host = new FakeHost();
            var l = new PointsLedger(); var e = new EngageEngine(s, l, new Random(21)) { SelfName = "sam", Host = host };
            var a = l.Get("u1", "alice"); var b = l.Get("u2", "bob"); l.Add(a, 1000); l.Add(b, 1000);
            var o = e.Handle(M("sam", "!race", "", self: true, room: "Music"), t0); Check(o.Replies[0].Text.Contains("race in 30s"), "host starts a race");
            e.Handle(M("alice", "!joinrace", "u1"), t0.AddSeconds(1)); e.Handle(M("bob", "!joinrace", "u2"), t0.AddSeconds(2));
            e.Handle(M("alice", "!bet bob 300", "u1"), t0.AddSeconds(3)); e.Handle(M("bob", "!bet 1 200", "u2"), t0.AddSeconds(4));
            o = e.Handle(M("alice", "!bet 1 10", "u1"), t0.AddSeconds(5)); Check(o.Replies[0].Text.Contains("already backed"), "one racer per bettor");
            Check(a.Points == 700 && b.Points == 800 && e.View(t0.AddSeconds(6)).RaceBetting?.Pot == 500, "bets held; pot on the overlay");
            o = e.Tick(t0.AddSeconds(31));
            Check(host.RaceNow != null && host.RaceNow.Racers.Count == 4 && host.RaceNow.Racers[0] == "alice" && o.Replies[0].Text.Contains("they're off"), "betting closes → race starts with joiners + pit fill");
            var race = host.RaceNow!; var end = race.StartUtc.AddSeconds(race.FinishTimes[race.Winner] + 2).ToLocalTime();
            Check(e.Tick(t0.AddSeconds(33)).Replies.Count == 0, "race running");
            o = e.Tick(end);
            var w = race.Racers[race.Winner];
            long expA = 700 + (race.Winner == 1 ? 500 : race.Winner == 0 ? 0 : 300) + (w == "alice" ? 100 : 0);
            long expB = 800 + (race.Winner == 0 ? 500 : race.Winner == 1 ? 0 : 200) + (w == "bob" ? 100 : 0);
            Check(host.RaceNow == null && o.Replies[0].Text.Contains($"{w} wins"), $"race finishes: {w} wins");
            Check(a.Points == expA && b.Points == expB, $"race payout (pot to backers, refund if nobody backed the winner, winner bonus) a={a.Points}/{expA} b={b.Points}/{expB}");
            host.Pit = false; o = e.Handle(M("sam", "!race", "", self: true), t0.AddMinutes(5)); Check(o.Replies[0].Text.Contains("needs the Ball Pit"), "race needs the pit");
            // scene race mode
            var sc = new BallPitScene(new BallPitSettings { Mode = "Source" }, 1); sc.Join("x"); 
            var rg = new RaceGame(new[] { "x", "y", "z" }, new Random(2), t0.ToUniversalTime(), 8); var clock = t0.ToUniversalTime();
            sc.RaceClock = () => clock; sc.StartRace(rg);
            Check(sc.Names().Count == 3, "racers not in the pit are added");
            for (int i = 0; i < 300; i++) { clock = clock.AddSeconds(1 / 30.0); sc.Step(1 / 30.0); }
            float wx; lock (sc.Sync) wx = sc.Balls.First(bb => bb.Name == rg.Racers[rg.Winner]).Pos.X;
            Check(wx > BallPitScene.RaceFinishX - 0.3f, $"winner's ball reaches the finish line in the scene (x={wx:0.00})");
            using var rend = new BallPitRenderer(640, 360); rend.Render(sc); Check(rend.Bytes.ToArray().Any(x => x != 0), "race renders");
        }
        // ---------- polls ----------
        {
            var pp = Poll.Parse("!poll \"Best snack?\" chips | popcorn | fruit 2m", 90);
            Check(pp is { } x && x.Q == "Best snack?" && x.Opts.Count == 3 && x.Secs == 120, "poll parse (quoted, trailing duration)");
            Check(Poll.Parse("!poll 30 Pizza or tacos? | pizza | tacos", 90) is { Secs: 30, Opts.Count: 2 }, "poll parse (unquoted, leading duration)");
            Check(Poll.Parse("!poll \"Q\" only", 90) == null && Poll.Parse("!poll", 90) == null, "poll parse rejects <2 options");
            Check(Poll.Percents(new[] { 1, 1, 1 }).Sum() == 100 && Poll.Percents(new[] { 2, 1, 0 }).SequenceEqual(new[] { 67, 33, 0 }) && Poll.Percents(new[] { 0, 0 }).Sum() == 0, "percentages add up to 100");
            var s = new EngageSettings { UserCooldownSeconds = 0, Moderators = "mod1" }; var l = new PointsLedger();
            var e = new EngageEngine(s, l) { SelfName = "sam" };
            Check(e.Handle(M("alice", "!poll \"x\" a | b", "u1"), t0).Replies.Count == 0 && e.CurrentPoll == null, "viewers can't start polls");
            var o = e.Handle(M("mod1", "!poll \"Best snack?\" chips | popcorn | fruit 60s", "u9", room: "Bar"), t0);
            Check(e.CurrentPoll != null && o.Replies[0].Room == "Bar" && o.Replies[0].Text.Contains("1. chips"), "mods start polls; announced in their room");
            e.Handle(M("alice", "!vote 2", "u1"), t0.AddSeconds(1)); e.Handle(M("bob", "2", "u2"), t0.AddSeconds(2)); e.Handle(M("carol", "fruit", "u3"), t0.AddSeconds(3));
            e.Handle(M("dave", "I think 2 is good", "u4"), t0.AddSeconds(4)); e.Handle(M("alice", "!vote 3", "u1"), t0.AddSeconds(5)); e.Handle(M("bob", "2", "u2"), t0.AddSeconds(6));
            Check(e.CurrentPoll!.Counts().SequenceEqual(new[] { 0, 1, 2 }), "one vote per user, changeable; bare number/text votes; sentences ignored");
            s.Polls.BareVotes = false; e.Handle(M("erin", "1", "u5"), t0.AddSeconds(7)); Check(e.CurrentPoll!.Total == 3, "bare votes can be turned off"); s.Polls.BareVotes = true;
            var v = e.View(t0.AddSeconds(8)); Check(v.Poll != null && !v.PollFinal, "poll on the overlay");
            o = e.Tick(t0.AddSeconds(61));
            Check(o.Replies.Single().Room == "Bar" && o.Replies[0].Text.Contains("winner: fruit") && o.Replies[0].Text.Contains("67%") && e.PollHistory.Count == 1, "poll closes on time: results to chat + history");
            Check(e.View(t0.AddSeconds(65)).PollFinal && e.View(t0.AddSeconds(90)).Poll == null, "final results shown, then hidden");
            e.StartPoll("Tie?", new() { "a", "b" }, 30, "Lobby", t0.AddSeconds(100)); e.Handle(M("x", "1", "x1"), t0.AddSeconds(101)); e.Handle(M("y", "2", "y1"), t0.AddSeconds(102));
            o = e.Handle(M("sam", "!endpoll", "", self: true), t0.AddSeconds(103)); Check(o.Replies[0].Text.Contains("tie: a / b"), "app-created poll, !endpoll, tie");
        }
        // ---------- predictions ----------
        {
            var pay = EngageGames.Parimutuel(new[] { ("a", 0, 100L), ("b", 0, 300L), ("c", 1, 600L) }, 0);
            Check(pay["a"] == 250 && pay["b"] == 750 && !pay.ContainsKey("c"), "parimutuel: winners share the pool by stake");
            pay = EngageGames.Parimutuel(new[] { ("a", 0, 1L), ("b", 0, 1L), ("c", 0, 1L), ("d", 1, 1L) }, 0);
            Check(pay.Values.Sum() == 4 && pay.Values.Max() == 2, "rounding dust goes to a winner (pool conserved)");
            pay = EngageGames.Parimutuel(new[] { ("a", 0, 10L), ("b", 0, 20L) }, 1);
            Check(pay["a"] == 10 && pay["b"] == 20, "nobody on the winner → refunds");
            var s = new EngageSettings { UserCooldownSeconds = 0 }; var l = new PointsLedger(); var e = new EngageEngine(s, l) { SelfName = "sam" };
            var a = l.Get("u1", "alice"); var b = l.Get("u2", "bob"); var c = l.Get("u3", "carol"); l.Add(a, 1000); l.Add(b, 1000); l.Add(c, 1000);
            var o = e.Handle(M("sam", "!prediction \"Will Sam win?\" yes | no", "", self: true, room: "Lobby"), t0); Check(e.CurrentPrediction != null && o.Replies[0].Text.Contains("1. yes"), "host opens a prediction");
            e.Handle(M("alice", "!predict 1 100", "u1"), t0.AddSeconds(1)); e.Handle(M("bob", "!predict 1 300", "u2"), t0.AddSeconds(2)); e.Handle(M("carol", "!predict 2 600", "u3"), t0.AddSeconds(3));
            o = e.Handle(M("alice", "!predict 2 50", "u1"), t0.AddSeconds(4)); Check(o.Replies[0].Text.Contains("already on 1") && a.Points == 900, "can't switch sides");
            e.Handle(M("alice", "!predict 1 100", "u1"), t0.AddSeconds(5)); Check(a.Points == 800 && e.CurrentPrediction!.Pool == 1100, "stakes add up");
            e.Handle(M("sam", "!lock", "", self: true), t0.AddSeconds(6)); o = e.Handle(M("bob", "!predict 1 10", "u2"), t0.AddSeconds(7)); Check(b.Points == 700 && o.Replies[0].Text.Contains("locked"), "locked: no more stakes");
            o = e.Handle(M("sam", "!resolve 1", "", self: true), t0.AddSeconds(8));
            // pool 1100; winners alice 200, bob 300 → alice 440, bob 660
            Check(a.Points == 800 + 440 && b.Points == 700 + 660 && c.Points == 400 && o.Replies[0].Text.Contains("2 winner(s) share 1,100"), $"resolve pays proportionally (a={a.Points} b={b.Points} c={c.Points})");
            e.Handle(M("sam", "!prediction \"Again?\" a | b", "", self: true), t0.AddSeconds(20)); e.Handle(M("carol", "!predict 2 400", "u3"), t0.AddSeconds(21));
            e.CancelPrediction(); Check(c.Points == 400, "cancel refunds stakes");
        }
        // ---------- persistence + migration + CSV ----------
        {
            var path = dir + "/points.json";
            File.WriteAllText(path, "{\"alice\": 120, \"Bob\": 30}");
            var l = new PointsLedger(path);
            Check(l.Count == 2 && l.Balance("", "alice") == 120 && l.Find("bob")?.Points == 30, "v1 {name: points} file migrated");
            var u = l.Get("64aa000000000000000000aa", "alice"); Check(u.Id == "64aa000000000000000000aa" && l.Count == 2 && u.Points == 120, "name-keyed record moves to the userId key when the id is learned");
            var r2 = l.Get("64aa000000000000000000aa", "alice_renamed"); Check(r2.Points == 120 && r2.Name == "alice_renamed", "renames follow the userId");
            l.Save(t0); var txt = File.ReadAllText(path);
            Check(txt.Contains("\"Version\": 2") && txt.Contains("64aa000000000000000000aa"), "saved as v2 with ids");
            Check(l.Backups().Count() == 1 && File.ReadAllText(l.Backups().First()).Contains("\"alice\": 120"), "daily backup keeps the previous file");
            l.Add(u, 5); l.Save(t0.AddHours(1)); Check(l.Backups().Count() == 1, "one backup per day");
            l.Add(u, 5); l.Save(t0.AddDays(1)); Check(l.Backups().Count() == 2, "next day → new backup");
            var l2 = new PointsLedger(path); Check(l2.Balance("64aa000000000000000000aa", "") == 130 && l2.Find("alice_renamed") != null, "reload round-trips");
            File.WriteAllText(path, "{ not json"); var l3 = new PointsLedger(path);
            Check(l3.Count == 2 && Directory.GetFiles(dir, "points.json.corrupt-*").Length == 1, "corrupt file kept aside, newest backup restored");
            for (int i = 2; i < 20; i++) { l.Add(u, 1); l.Save(t0.AddDays(i)); } Check(l.Backups().Count() == 14, "backups rotate (14 kept)");
            File.WriteAllText(dir + "/v1arr.json", "[{\"name\":\"zed\",\"userId\":\"u77\",\"points\":9}]"); Check(new PointsLedger(dir + "/v1arr.json").Balance("u77", "zed") == 9, "v1 array file migrated");
            var csv = l2.ExportCsv(); l2.Get("", "=cmd|evil").Points = 1; var csv2 = l2.ExportCsv();
            Check(csv.StartsWith("userId,name,points") && csv2.Contains("\"'=cmd|evil\""), "CSV export (formula-safe)");
            var l4 = new PointsLedger(); int n = l4.ImportCsv(csv2, true);
            Check(n == 3 && l4.Balance("64aa000000000000000000aa", "") == 130 && l4.Find("=cmd|evil")?.Points == 1, "CSV import (replace)");
            l4.ImportCsv("name,points\nalice_renamed,10\nnewbie,5\n", false); Check(l4.Find("alice_renamed")!.Points == 140 && l4.Find("newbie")!.Points == 5, "CSV import (add, minimal columns)");
            try { l4.ImportCsv("foo,bar\n1,2", true); Check(false, "bad CSV rejected"); } catch (FormatException) { Check(true, "bad CSV rejected"); }
            var top = l4.Top(2); Check(top.Count == 2 && top[0].Points >= top[1].Points, "top N");
            l4.ResetAll(); Check(l4.Snapshot().All(x => x.Points == 0), "reset all");
            Check(System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(new Profile()))!.Engage.Rewards.Count == 5 &&
                  System.Text.Json.JsonSerializer.Deserialize<Profile>("{\"Name\":\"old\"}")!.Engage.CurrencyName == "Epik Points", "old profiles get Engage defaults");
        }
        // ---------- reply batching ----------
        {
            var rb = new ReplyBatcher(); rb.Add("Lobby", "a"); rb.Add("Bar", "b"); rb.Add("Lobby", "c");
            var f = rb.Flush(); Check(f.Count == 2 && f.Contains(("Lobby", "a · c")) && f.Contains(("Bar", "b")), "replies batched per room");
            var longTxt = string.Join(" · ", Enumerable.Range(1, 40).Select(i => $"{i}. user{i} {i * 100:N0}"));
            var ch = ReplyBatcher.Chunk(longTxt); Check(ch.Count >= 2 && ch.All(x => x.Length <= 250) && !ch[1].StartsWith("·"), $"long reply split at separators ≤250 ({ch.Count} chunks)");
            var word = string.Concat(Enumerable.Repeat("x", 600)); ch = ReplyBatcher.Chunk(word); Check(ch.All(x => x.Length <= 250) && ch.Count == 3, "unbreakable text hard-split ≤250");
            var emoji = string.Concat(Enumerable.Repeat("😀", 200)); ch = ReplyBatcher.Chunk(emoji); Check(ch.All(x => x.Length <= 250 && !char.IsHighSurrogate(x.TrimEnd('…')[^1])), "never splits a surrogate pair");
            for (int i = 0; i < 30; i++) rb.Add("Lobby", $"reply number {i} with some words");
            f = rb.Flush(); Check(f.All(x => x.Text.Length <= 250) && f.Count < 30 && string.Join(" ", f.Select(x => x.Text)).Contains("reply number 29"), $"30 replies → {f.Count} messages, none lost");
        }
        // ---------- overlay ----------
        {
            var s = new EngageSettings { UserCooldownSeconds = 0 }; var l = new PointsLedger(); var e = new EngageEngine(s, l) { SelfName = "sam" };
            Check(EngageOverlay.Text(e.View(t0), s.Overlay) == "", "no cards → empty overlay text");
            e.StartPoll("Best snack? 🍿", new() { "chips", "popcorn", "fruit" }, 60, "Lobby", t0);
            e.Handle(M("a", "1", "a"), t0); e.Handle(M("b", "2", "b"), t0); e.Handle(M("c", "2", "c"), t0);
            var txt = EngageOverlay.Text(e.View(t0.AddSeconds(15)), s.Overlay);
            Check(txt.Contains("Best snack?") && txt.Contains("[45s]") && txt.Contains("67% (2)") && !txt.Contains("🍿") && !txt.Contains("📊"), "text overlay: bars, countdown, emoji stripped:\n" + txt);
            using var bmp = new SKBitmap(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul)); using var cv = new SKCanvas(bmp); cv.Clear(SKColors.Black);
            EngageOverlay.Draw(cv, 1280, 720, e.View(t0.AddSeconds(15)), s.Overlay); cv.Flush();
            Check(bmp.GetPixel(1100, 40) != SKColors.Black && bmp.GetPixel(100, 600) == SKColors.Black, "card drawn top-right only");
            s.Overlay.Enabled = false; Check(EngageOverlay.Text(e.View(t0), s.Overlay) == "", "overlay toggle");
        }
        Console.WriteLine(fails == 0 ? "ENGAGE PASSED" : $"ENGAGE FAILED ({fails})");
        return fails == 0 ? 0 : 1;
    }
}
