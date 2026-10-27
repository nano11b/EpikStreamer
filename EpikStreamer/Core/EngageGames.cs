using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace EpikStreamer.Core;

/// <summary>v2.8 mini-game math (pure; every random draw comes from the injected Random, so tests can seed it).</summary>
public static class EngageGames
{
    /// <summary>!gamble: returns the net change (+stake×(mult−1) on a win, −stake on a loss).</summary>
    public static (bool Won, long Delta) Gamble(long stake, GameSettings g, Random rnd)
    {
        bool won = rnd.NextDouble() < Math.Clamp(g.GambleWinChance, 0, 1);
        return (won, won ? (long)Math.Floor(stake * Math.Max(1, g.GambleMultiplier)) - stake : -stake);
    }

    public static readonly string[] SlotSymbols = { "♣", "♦", "♥", "♠", "★", "7" };
    /// <summary>!slots: three reels; pair / triple / jackpot (three 7s) multipliers return stake × m (0 = lose the stake).</summary>
    public static (string[] Reels, double Mult, long Delta) Slots(long stake, GameSettings g, Random rnd)
    {
        var r = new[] { SlotSymbols[rnd.Next(SlotSymbols.Length)], SlotSymbols[rnd.Next(SlotSymbols.Length)], SlotSymbols[rnd.Next(SlotSymbols.Length)] };
        double m = r[0] == r[1] && r[1] == r[2] ? (r[0] == "7" ? g.SlotsJackpot : g.SlotsTriple) : (r[0] == r[1] || r[1] == r[2] || r[0] == r[2]) ? g.SlotsPair : 0;
        return (r, m, (long)Math.Floor(stake * m) - stake);
    }

    /// <summary>!duel: true = the challenger wins (50/50).</summary>
    public static bool Duel(Random rnd) => rnd.Next(2) == 0;

    public static double HeistChance(int crew, GameSettings g) => Math.Clamp(g.HeistBaseChance + g.HeistPerMember * Math.Max(0, crew - 1), 0, Math.Clamp(g.HeistMaxChance, 0, 1));
    public static bool Heist(int crew, GameSettings g, Random rnd) => rnd.NextDouble() < HeistChance(crew, g);

    /// <summary>Parimutuel split (predictions, race bets): everyone who picked the winner shares the whole pool in proportion to
    /// their stake (rounded down; the rounding dust goes to the biggest winning stake). Nobody on the winner → everyone refunded.</summary>
    public static Dictionary<string, long> Parimutuel(IEnumerable<(string Key, int Pick, long Stake)> stakes, int winner)
    {
        var all = stakes.Where(s => s.Stake > 0).ToList();
        long pool = all.Sum(s => s.Stake), win = all.Where(s => s.Pick == winner).Sum(s => s.Stake);
        var r = new Dictionary<string, long>();
        if (win == 0) { foreach (var s in all) r[s.Key] = r.GetValueOrDefault(s.Key) + s.Stake; return r; }
        foreach (var s in all.Where(s => s.Pick == winner)) r[s.Key] = r.GetValueOrDefault(s.Key) + (long)Math.Floor((double)s.Stake * pool / win);
        var dust = pool - r.Values.Sum();
        if (dust > 0) { var top = all.Where(s => s.Pick == winner).OrderByDescending(s => s.Stake).First().Key; r[top] += dust; }
        return r;
    }

    /// <summary>"all" → the balance; otherwise a positive whole number. Capped at MaxBet. Null if invalid.</summary>
    public static long? ParseBet(string s, long balance, int max)
    {
        s = (s ?? "").Trim().ToLowerInvariant().Replace(",", "");
        long v;
        if (s is "all" or "allin" or "max") v = balance;
        else if (s.EndsWith('%') && int.TryParse(s[..^1], out var pc) && pc is > 0 and <= 100) v = balance * pc / 100;
        else if (s.EndsWith('k') && double.TryParse(s[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var k)) v = (long)(k * 1000);
        else if (!long.TryParse(s, out v)) return null;
        if (max > 0) v = Math.Min(v, max);
        return v > 0 ? v : null;
    }
}

public record TriviaQuestion(string Q, string[] A, string Category = "");

/// <summary>Trivia packs: the built-in pack plus custom JSON packs ([{"q","a":[...]}] or {"name","questions":[...]}).</summary>
public static class TriviaPack
{
    public static readonly TriviaQuestion[] BuiltIn =
    {
        new("What planet is known as the Red Planet?", new[] { "mars" }, "Science"),
        new("How many sides does a hexagon have?", new[] { "6", "six" }, "Math"),
        new("What is the largest ocean on Earth?", new[] { "pacific", "pacific ocean" }, "Geography"),
        new("What gas do plants absorb from the air?", new[] { "carbon dioxide", "co2" }, "Science"),
        new("Which instrument has 88 keys?", new[] { "piano" }, "Music"),
        new("What is the capital of Japan?", new[] { "tokyo" }, "Geography"),
        new("How many minutes are in a full day?", new[] { "1440" }, "Math"),
        new("What is the chemical symbol for gold?", new[] { "au" }, "Science"),
        new("Which band sang 'Bohemian Rhapsody'?", new[] { "queen" }, "Music"),
        new("What is the fastest land animal?", new[] { "cheetah" }, "Nature"),
        new("How many strings does a standard guitar have?", new[] { "6", "six" }, "Music"),
        new("What is the smallest prime number?", new[] { "2", "two" }, "Math"),
        new("In which country are the pyramids of Giza?", new[] { "egypt" }, "Geography"),
        new("What color do you get mixing blue and yellow?", new[] { "green" }, "Art"),
        new("How many continents are there?", new[] { "7", "seven" }, "Geography"),
        new("What is frozen water called?", new[] { "ice" }, "Science"),
        new("Which planet has the most famous rings?", new[] { "saturn" }, "Science"),
        new("What is 12 × 12?", new[] { "144" }, "Math"),
        new("What is the tallest animal?", new[] { "giraffe" }, "Nature"),
        new("Which language has the most native speakers?", new[] { "mandarin", "chinese", "mandarin chinese" }, "Culture"),
        new("How many legs does a spider have?", new[] { "8", "eight" }, "Nature"),
        new("What is the hardest natural substance?", new[] { "diamond" }, "Science"),
        new("Which ocean lies between Africa and Australia?", new[] { "indian", "indian ocean" }, "Geography"),
        new("What is the boiling point of water in °C?", new[] { "100" }, "Science"),
    };
    public static List<TriviaQuestion> Parse(string json)
    {
        var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("questions", out var q)) root = q;
        var r = new List<TriviaQuestion>();
        if (root.ValueKind != JsonValueKind.Array) return r;
        foreach (var e in root.EnumerateArray().Take(5000))
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            string S(string k) => e.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()! : "";
            var question = S("q").Length > 0 ? S("q") : S("question");
            var ans = new List<string>();
            foreach (var k in new[] { "a", "answer", "answers" })
                if (e.TryGetProperty(k, out var a)) { if (a.ValueKind == JsonValueKind.String) ans.Add(a.GetString()!); else if (a.ValueKind == JsonValueKind.Array) ans.AddRange(a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)); }
            if (question.Length > 0 && ans.Count > 0) r.Add(new(question.Length > 200 ? question[..200] : question, ans.Select(Normalize).Where(x => x.Length > 0).ToArray(), S("category")));
        }
        return r;
    }
    public static List<TriviaQuestion> Load(GameSettings g, Action<string>? log = null)
    {
        var all = new List<TriviaQuestion>(g.TriviaBuiltIn ? BuiltIn : Array.Empty<TriviaQuestion>());
        foreach (var p in g.TriviaPacks) try { all.AddRange(Parse(File.ReadAllText(p))); } catch (Exception ex) { log?.Invoke($"trivia pack {Path.GetFileName(p)}: {ex.Message}"); }
        return all;
    }
    public static string Normalize(string s)
    {
        s = Regex.Replace((s ?? "").ToLowerInvariant().Trim(), @"[^\p{L}\p{N} ]", "");
        s = Regex.Replace(s, @"^(the|a|an) ", "");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
    public static bool IsCorrect(TriviaQuestion q, string answer) { var a = Normalize(answer); return a.Length > 0 && q.A.Any(x => Normalize(x) == a); }
}

/// <summary>Ball Pit race: the whole race is planned up front from the seeded RNG (speed per 0.5 s segment), so the winner
/// is known (and testable) at the start and every frame can ask for exact positions at any time.</summary>
public sealed class RaceGame
{
    public const double Segment = 0.5;
    public IReadOnlyList<string> Racers { get; }
    readonly double[][] _speed;       // progress per second, per racer per segment
    public DateTime StartUtc { get; }
    public double[] FinishTimes { get; }
    public int Winner { get; }
    public double Duration => FinishTimes.Max();
    public RaceGame(IReadOnlyList<string> racers, Random rnd, DateTime startUtc, double meanSeconds = 10)
    {
        Racers = racers; StartUtc = startUtc;
        int segs = (int)Math.Ceiling(meanSeconds * 3 / Segment);
        _speed = racers.Select(_ => Enumerable.Range(0, segs).Select(_ => (0.55 + 0.9 * rnd.NextDouble()) / meanSeconds).ToArray()).ToArray();
        FinishTimes = Enumerable.Range(0, racers.Count).Select(FinishTime).ToArray();
        Winner = Array.IndexOf(FinishTimes, FinishTimes.Min());
    }
    double FinishTime(int i)
    {
        double p = 0, t = 0;
        foreach (var v in _speed[i]) { if (p + v * Segment >= 1) return t + (1 - p) / v; p += v * Segment; t += Segment; }
        return t + (1 - p) / _speed[i][^1];
    }
    /// <summary>0..1 progress of racer i, t seconds after the start.</summary>
    public double Progress(int i, double t)
    {
        if (t <= 0) return 0; double p = 0, tt = 0;
        foreach (var v in _speed[i]) { if (tt + Segment >= t) return Math.Min(1, p + v * (t - tt)); p += v * Segment; tt += Segment; if (p >= 1) return 1; }
        return Math.Min(1, p + _speed[i][^1] * (t - tt));
    }
    public double Elapsed(DateTime utc) => (utc - StartUtc).TotalSeconds;
    public bool Finished(DateTime utc) => Elapsed(utc) >= FinishTimes[Winner] + 1.5;
    public int Lane(string name) { for (int i = 0; i < Racers.Count; i++) if (string.Equals(Racers[i], name, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
}
