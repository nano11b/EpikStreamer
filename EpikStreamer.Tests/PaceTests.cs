using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EpikStreamer.Core;

/// <summary>v2.6.1 real-time pacing repro: Ball Pit (Source / Overlay over a video playlist) + radio (+ now-playing card)
/// for N seconds; samples encoder speed, dup/drop, "Resumed reading … lag" messages, the Ball Pit feed's frame delivery
/// and the radio feed's byte delivery against the wall clock.</summary>
public static class PaceTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static readonly Regex Lag = new(@"\[(\w+)#(\d+):\d+/(\w+) @[^\]]*\] Resumed reading at pts ([\d.]+) .*after a lag of ([\d.]+)s", RegexOptions.Compiled);

    public static int Run(string ff, string dir, string mode, bool card, double secs, string encoder, string outMp4)
    {
        fails = 0;
        var ms = new RadioTests.MockStation();
        var mp3 = Path.Combine(dir, "radio-tone.mp3");
        if (!File.Exists(mp3)) RadioTests.Run(ff, "-hide_banner", "-y", "-f", "lavfi", "-i", "sine=f=330:d=30,volume=0.6", "-c:a", "libmp3lame", "-b:a", "128k", "-ar", "44100", mp3);
        ms.Mp3 = File.ReadAllBytes(mp3);
        var p = new Profile { Name = "Pace", Encoder = encoder, MaxRetries = 0, CrossfadeSeconds = 0 };
        if (Environment.GetEnvironmentVariable("RES") is { } res) { var wh = res.Split('x'); p.Width = int.Parse(wh[0]); p.Height = int.Parse(wh[1]); }
        p.BallPit.Mode = mode; p.BallPit.OrbitSpeed = 6; p.BallPit.ShowCount = true;
        if (Environment.GetEnvironmentVariable("SCALE") is { } sc0) p.BallPit.RenderScale = double.Parse(sc0, CultureInfo.InvariantCulture);
        p.Radio = new RadioSettings { Enabled = true, Url = ms.Base + "/public/test/playlist.pls", Volume = 0.9, BeatPulse = true, ShowListeners = true };
        p.Audio.Normalize = "Off";
        if (mode == "Overlay")
        {
            var vid = Path.Combine(dir, "pace-bg.mp4");
            if (!File.Exists(vid)) RadioTests.Run(ff, "-hide_banner", "-y", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=30:d=40", "-f", "lavfi", "-i", "sine=f=220:d=40,volume=0.2",
                "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-g", "60", "-c:a", "aac", "-shortest", vid);
            p.Playlist = new() { vid }; p.LoopPlaylist = true;
        }
        p.NowPlaying.Enabled = card;
        var work = Path.Combine(dir, "work-pace-" + mode + (card ? "-card" : "") + Environment.GetEnvironmentVariable("TAG")); Directory.CreateDirectory(work);
        File.Copy("/tmp/zt/font.ttf", Path.Combine(work, CommandBuilder.FontName), true);
        var rh = new RadioHost(p.Radio, Path.Combine(dir, "art")); rh.Apply(p.Radio);
        var urls = rh.ResolveAsync(default).Result;
        var flv = Path.ChangeExtension(outMp4, ".flv"); try { File.Delete(flv); File.Delete(outMp4); } catch { }
        var scene = BallPitTests.DemoScene(Environment.GetEnvironmentVariable("BALLS") is { } nb ? int.Parse(nb) : 120, 7, p.BallPit); scene.BeatStrength = 0.6f;
        var eng = new StreamEngine(p, ff, new[] { new CommandBuilder.Destination("File", flv) }, Array.Empty<string>(), work)
        { BallPit = scene, RadioUrls = urls, RadioLevel = scene.AudioLevel, RadioInfo = rh.CardInfo };
        var log = new StringBuilder(); var lags = new List<(double wall, string tag, double pts, double lag)>();
        var sw = Stopwatch.StartNew();
        eng.Log += l => { lock (log) log.AppendLine(l); var m = Lag.Match(l); if (m.Success) lock (lags) lags.Add((sw.Elapsed.TotalSeconds, m.Groups[1].Value + "#" + m.Groups[2].Value + "/" + m.Groups[3].Value, double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture))); };
        StreamStats? last = null; var speeds = new List<(double wall, double speed, long frames, long dup, long drop)>();
        long fOff = 0, fPrev = 0;   // frames are per encoder run: accumulate across planned restarts
        eng.Stats += st => { last = st; if (st.Frames < fPrev) fOff += fPrev; fPrev = st.Frames; lock (speeds) speeds.Add((sw.Elapsed.TotalSeconds, st.Speed, st.Frames + fOff, st.Duplicated, st.Dropped)); };
        eng.Start();
        double firstFrameWall = -1; long f0 = 0, r0 = 0, rb0 = 0, rs0 = 0; double tPrev = 0;
        Console.WriteLine($"  [{mode}{(card ? "+card" : "")}, {encoder}, {secs:0}s, {Path.GetFileName(Path.GetDirectoryName(ff))}]");
        Console.WriteLine("   wall  outT  speed  dup drop | pit fps(deliv) rep renderFps scale | radio KiB/s(silent) | lag msgs");
        while (sw.Elapsed.TotalSeconds < secs)
        {
            Thread.Sleep(10000);
            var t = sw.Elapsed.TotalSeconds; var pf = eng.PitFeed; var rf = eng.RadioFeedNow;
            long fr = pf?.Frames ?? 0, rp = pf?.Repeats ?? 0, rb = rf?.Bytes ?? 0, rs = rf?.SilentBytes ?? 0;
            if (fr < f0) { f0 = 0; r0 = 0; } if (rb < rb0) { rb0 = 0; rs0 = 0; }
            if (firstFrameWall < 0 && last != null && last.Frames > 0) firstFrameWall = t - last.Frames / 30.0;
            var lastLag = ""; lock (lags) if (lags.Count > 0) lastLag = string.Join(" ", lags.GroupBy(x => x.tag).Select(g => $"{g.Key}:{g.Count()}× lag {g.Last().lag:0.0}s"));
            Console.WriteLine($"  {t,5:0} {(fPrev + fOff) / 30.0,5:0} {last?.Speed ?? 0,5:0.00}x {last?.Duplicated ?? 0,4} {last?.Dropped ?? 0,4} | {(fr - f0) / (t - tPrev),5:0.0} {rp - r0,4} {pf?.RenderFps ?? 0,6:0} {pf?.Scale ?? 1,4:0.00} | {(rb - rb0) / 1024.0 / (t - tPrev),6:0} ({(rs - rs0) / 1024.0 / (t - tPrev):0}) | {lastLag}");
            if (pf != null) Console.WriteLine($"         feed: written {pf.Written} skipped {pf.Skipped} blocked-in-write {pf.WriteSec:0.0}s, lag {pf.Lag:0.00}s, half-rate {pf.HalfRate}, card draws {pf.CardDraws}, degrade {eng.PitDegrade:0.00}");
            f0 = fr; r0 = rp; rb0 = rb; rs0 = rs; tPrev = t;
        }
        var feedRate = eng.PitFeed?.Frames ?? 0; var cardDraws = eng.PitFeed?.CardDraws ?? 0; int degrades = eng.AutoDegrades + (eng.PitFeed?.HalfRate == true ? 1 : 0);
        eng.Stop(); Check(eng.Wait(20000), "engine stopped");
        var lg = log.ToString();
        foreach (var l in lg.Split('\n').Where(l => l.Contains("⚠") || l.Contains("Encoder can't")).Distinct().Take(10)) Console.WriteLine("  log: " + l.Trim());
        if (Environment.GetEnvironmentVariable("BPLOG") != null) Console.WriteLine(lg[^Math.Min(lg.Length, 6000)..]);
        // judge the steady state (skip the first 20 s of start-up)
        List<(double wall, double speed, long frames, long dup, long drop)> ss; lock (speeds) ss = speeds.Where(x => x.wall > Math.Max(secs * 0.35, secs - 60)).ToList();
        // ffmpeg's speed= is cumulative per encoder run; after an auto-degrade restart only the last 30 s say how it's doing now
        var sp = ss.Where(x => x.wall > secs - 30).ToList(); if (sp.Count == 0) sp = ss;
        double avgSpeed = sp.Count > 0 ? sp.Average(x => x.speed) : 0;
        double outRate = ss.Count > 1 ? (ss[^1].frames - ss[0].frames) / 30.0 / (ss[^1].wall - ss[0].wall) : 0;
        Console.WriteLine($"  steady state: avg speed {avgSpeed:0.000}x, output advanced {outRate:0.000} s per wall second, last speed {last?.Speed:0.00}x, dup {last?.Duplicated} drop {last?.Dropped}");
        List<(double wall, string tag, double pts, double lag)> lg2; lock (lags) lg2 = lags.ToList();
        var growing = lg2.GroupBy(x => x.tag).Select(g => { var l1 = g.Where(x => x.wall > Math.Max(secs * 0.35, secs - 60)).ToList(); return (g.Key, n: l1.Count, grow: l1.Count > 1 ? l1[^1].lag - l1[0].lag : 0); }).ToList();
        foreach (var g in growing) Console.WriteLine($"  lag messages {g.Key}: {g.n} in the steady state, lag grew {g.grow:0.0} s");
        Check(growing.All(g => g.grow < 3), "no growing \"Resumed reading … lag\"");
        if (card) Check(cardDraws > 0, $"now-playing card drawn into the Ball Pit frames ({cardDraws} draws)");
        Console.WriteLine($"  auto-degrade steps: {degrades} (encoder-side {eng.AutoDegrades}, final render size {eng.PitDegrade * p.BallPit.RenderScale:P0}, half-rate {eng.PitFeed?.HalfRate})");
        if (Environment.GetEnvironmentVariable("EXPECT_DEGRADE") == "1") Check(degrades > 0, "auto-degrade kicked in");
        Check(outRate > 0.97 && outRate < 1.03, $"output keeps wall-clock pace ({outRate:0.000} s/s)");
        Check(avgSpeed > 0.96 && (last?.Speed ?? 0) > 0.97, $"speed ≈ 1.0x (steady-state avg {avgSpeed:0.000}, last {last?.Speed:0.00})");
        RadioTests.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-i", flv, "-c", "copy", "-movflags", "+faststart", outMp4);
        try { File.Delete(flv); } catch { }
        rh.Dispose(); ms.Dispose();
        Console.WriteLine(fails == 0 ? "PACE PASSED" : $"{fails} FAILED");
        return fails;
    }
}
