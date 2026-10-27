using System.Text;
using SkiaSharp;
namespace EpikStreamer.Core;

/// <summary>v2.8 Engage on-stream cards (same look as the radio / now-playing cards): poll bars + countdown, prediction pool,
/// race betting / live standings, leaderboard, trivia, heist, toasts. Drawn into the Ball Pit frames when the pit is live;
/// otherwise <see cref="Text"/> feeds a reloaded drawtext file (no extra ffmpeg inputs either way).</summary>
public static class EngageOverlay
{
    static SKColor Col(string hex, SKColor fb, double alpha = 1) { var c = BallPitScene.ParseColor(hex) is uint v ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v) : fb; return c.WithAlpha((byte)(255 * Math.Clamp(alpha, 0, 1))); }
    static float Measure(SKFont f, string s) => BallPitTextures.Measure(f, s);
    static string Fit(SKFont f, string s, float w) { if (Measure(f, s) <= w) return s; while (s.Length > 1 && Measure(f, s + "…") > w) s = s[..^1]; return s + "…"; }
    static string Secs(DateTime ends, DateTime now) { var s = Math.Max(0, (int)Math.Ceiling((ends - now).TotalSeconds)); return s >= 60 ? $"{s / 60}:{s % 60:00}" : $"{s}s"; }

    sealed class Card { public string Title = "", Badge = ""; public List<(string Label, string Right, double Frac, bool Hi)> Rows = new(); public List<string> Lines = new(); }

    static List<Card> Cards(EngageView v)
    {
        var cards = new List<Card>(); var now = v.Now;
        if (v.Poll is { } p)
        {
            var c = p.Counts(); var pc = Poll.Percents(c); var lead = p.Leaders();
            var k = new Card { Title = "📊 " + p.Question, Badge = v.PollFinal ? "FINAL" : Secs(p.Ends, now) };
            for (int i = 0; i < p.Options.Count; i++) k.Rows.Add(($"{i + 1}. {p.Options[i]}", $"{pc[i]}% ({c[i]})", pc[i] / 100.0, v.PollFinal && lead.Contains(i)));
            k.Lines.Add(v.PollFinal ? $"{p.Total} votes" : $"{p.Total} votes · type !vote <number>");
            cards.Add(k);
        }
        if (v.Prediction is { } pr)
        {
            var per = pr.PerOutcome(); var n = pr.Bettors(); long pool = Math.Max(1, per.Sum());
            var k = new Card { Title = "🔮 " + pr.Question, Badge = pr.Result is int r && r >= 0 ? "RESULT" : pr.Locked ? "LOCKED" : "OPEN" };
            for (int i = 0; i < pr.Outcomes.Count; i++)
                k.Rows.Add(($"{i + 1}. {pr.Outcomes[i]}", $"{per[i]:N0} · {n[i]}" + (per[i] > 0 ? $" · 1:{(double)pool / per[i]:0.0#}" : ""), (double)per[i] / pool, pr.Result == i));
            k.Lines.Add($"Pool {per.Sum():N0} {v.Currency}" + (pr.Locked ? "" : " · !predict <n> <amount>"));
            cards.Add(k);
        }
        if (v.RaceBetting is { } rb)
        {
            var k = new Card { Title = "🏁 Ball Pit race", Badge = Secs(rb.Ends, now) };
            k.Lines.Add(rb.Racers.Count > 0 ? "Racers: " + string.Join(", ", rb.Racers.Select((x, i) => $"{i + 1}. {x}")) : "!joinrace to race");
            k.Lines.Add($"Pot {rb.Pot:N0} {v.Currency} · !bet <racer> <amount>");
            cards.Add(k);
        }
        if (v.Race is { } race)
        {
            double t = race.Elapsed(now.ToUniversalTime());
            var k = new Card { Title = "🏁 Race", Badge = t < 0 ? $"GO IN {Math.Ceiling(-t)}" : t >= race.FinishTimes[race.Winner] ? "FINISH" : "LIVE" };
            foreach (var i in Enumerable.Range(0, race.Racers.Count).OrderByDescending(i => race.Progress(i, t)).ThenBy(i => race.FinishTimes[i]).Take(8))
                k.Rows.Add(($"{i + 1}. {race.Racers[i]}", race.Progress(i, t) >= 1 ? (i == race.Winner ? "WIN" : "done") : $"{race.Progress(i, t):P0}", race.Progress(i, t), t >= race.FinishTimes[race.Winner] && i == race.Winner));
            cards.Add(k);
        }
        if (v.Leaderboard is { } lb)
        {
            var k = new Card { Title = "🏆 Top " + v.Currency }; long max = Math.Max(1, lb.Select(x => x.Points).DefaultIfEmpty(1).Max());
            for (int i = 0; i < lb.Count; i++) k.Rows.Add(($"{i + 1}. {lb[i].Name}", lb[i].Points.ToString("N0"), (double)lb[i].Points / max, i == 0));
            if (lb.Count == 0) k.Lines.Add("Nobody yet — watch & chat to earn!");
            cards.Add(k);
        }
        if (v.Trivia is { } tr) cards.Add(new Card { Title = "❓ Trivia", Badge = Secs(tr.Ends, now), Lines = { tr.Q, "First correct answer in chat wins!" } });
        if (v.Heist is { } h) cards.Add(new Card { Title = "💰 Heist", Badge = Secs(h.Ends, now), Lines = { $"Crew {h.Crew} · stake {h.Pot:N0} {v.Currency}", "!heist <amount> to join" } });
        if (v.Toasts.Count > 0) { var k = new Card(); k.Lines.AddRange(v.Toasts.TakeLast(3)); cards.Add(k); }
        return cards;
    }

    /// <summary>Draws the cards stacked in the configured corner.</summary>
    public static void Draw(SKCanvas c, int W, int H, EngageView v, EngageOverlaySettings s)
    {
        if (!s.Enabled || v.Empty) return;
        var cards = Cards(v); if (cards.Count == 0) return;
        foreach (var k in cards) { k.Title = Strip(k.Title); k.Rows = k.Rows.Select(r => (Strip(r.Label), r.Right, r.Frac, r.Hi)).ToList(); k.Lines = k.Lines.Select(Strip).ToList(); }
        float cw = (float)Math.Clamp(W * s.WidthFraction, 220, W - 32), pad = Math.Clamp(H / 60f, 8, 18), fs = Math.Clamp(H / 40f, 11, 26);
        using var title = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default, fs * 1.1f);
        using var body = new SKFont(SKTypeface.Default, fs);
        using var p = new SKPaint { IsAntialias = true };
        var bg = Col(s.Background, new SKColor(16, 16, 24), s.Opacity); var fg = Col(s.TextColor, SKColors.White); var acc = Col(s.AccentColor, new SKColor(168, 85, 247));
        float Height(Card k) => pad * 2 + (k.Title.Length > 0 ? title.Size * 1.35f : 0) + k.Rows.Count * fs * 1.9f + k.Lines.Count * fs * 1.35f;
        bool right = s.Corner is Corner.TopRight or Corner.BottomRight, center = s.Corner is Corner.TopCenter or Corner.BottomCenter or Corner.Center, bottom = s.Corner is Corner.BottomLeft or Corner.BottomCenter or Corner.BottomRight;
        float x0 = right ? W - cw - 16 : center ? (W - cw) / 2 : 16;
        float total = cards.Sum(Height) + 10 * (cards.Count - 1);
        float y = bottom ? H - 16 - total : s.Corner == Corner.Center ? (H - total) / 2 : 16;
        foreach (var k in cards)
        {
            float h = Height(k); if (y + h > H - 4) break;
            p.Color = bg; c.DrawRoundRect(new SKRect(x0, y, x0 + cw, y + h), 14, 14, p);
            p.Color = acc; c.DrawRoundRect(new SKRect(x0, y + 10, x0 + 4, y + h - 10), 2, 2, p);
            float tx = x0 + pad + 4, tw = cw - 2 * pad - 4, ty = y + pad;
            if (k.Title.Length > 0)
            {
                float bw = 0;
                if (k.Badge.Length > 0)
                {
                    bw = Measure(body, k.Badge) + 14;
                    p.Color = acc; c.DrawRoundRect(new SKRect(x0 + cw - pad - bw, ty, x0 + cw - pad, ty + title.Size * 1.1f), 8, 8, p);
                    p.Color = SKColors.White; c.DrawText(k.Badge, x0 + cw - pad - bw + 7, ty + title.Size * 0.85f, body, p);
                }
                p.Color = fg; c.DrawText(Fit(title, k.Title, tw - bw - 8), tx, ty + title.Size * 0.95f, title, p);
                ty += title.Size * 1.35f;
            }
            foreach (var (label, rt, frac, hi) in k.Rows)
            {
                float rw = Measure(body, rt);
                p.Color = hi ? Col(s.AccentColor, acc) : fg; c.DrawText(Fit(body, label, tw - rw - 10), tx, ty + fs, body, p);
                p.Color = fg.WithAlpha(200); c.DrawText(rt, tx + tw - rw, ty + fs, body, p);
                var track = new SKRect(tx, ty + fs * 1.3f, tx + tw, ty + fs * 1.3f + fs * 0.35f);
                p.Color = new SKColor(255, 255, 255, 45); c.DrawRoundRect(track, 3, 3, p);
                p.Color = hi ? new SKColor(250, 204, 21) : acc; c.DrawRoundRect(new SKRect(track.Left, track.Top, track.Left + (float)Math.Clamp(frac, 0, 1) * tw, track.Bottom), 3, 3, p);
                ty += fs * 1.9f;
            }
            foreach (var l in k.Lines) { p.Color = fg.WithAlpha(k.Title.Length == 0 ? (byte)255 : (byte)190); c.DrawText(Fit(body, l, tw), tx, ty + fs, body, p); ty += fs * 1.35f; }
            y += h + 10;
        }
    }

    /// <summary>Plain-text version for the ffmpeg drawtext file (no Ball Pit): ASCII bars, one card after another.</summary>
    public static string Text(EngageView v, EngageOverlaySettings s)
    {
        if (!s.Enabled || v.Empty) return "";
        var sb = new StringBuilder();
        foreach (var k in Cards(v))
        {
            if (sb.Length > 0) sb.Append('\n');
            if (k.Title.Length > 0) sb.Append(Strip(k.Title)).Append(k.Badge.Length > 0 ? "  [" + k.Badge + "]" : "").Append('\n');
            foreach (var (label, rt, frac, _) in k.Rows) sb.Append(Strip(label).PadRight(18)[..18]).Append(' ').Append(new string('#', (int)Math.Round(Math.Clamp(frac, 0, 1) * 12)).PadRight(12, '.')).Append(' ').Append(rt).Append('\n');
            foreach (var l in k.Lines) sb.Append(Strip(l)).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }
    /// <summary>drawtext fonts rarely have emoji: drop them (keeps ★♣♦♥♠ etc.).</summary>
    public static string Strip(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsSurrogate(s[i])) continue;
            if (s[i] == '\uFE0F' || s[i] == '\u200D') continue;
            sb.Append(s[i]);
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"^\s+", "");
    }
}
