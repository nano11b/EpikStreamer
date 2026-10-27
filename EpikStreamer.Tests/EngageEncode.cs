using System.Diagnostics;
using EpikStreamer.Core;

/// <summary>v2.8: a real encode with the Engage overlay. PITMODE=Source: the poll card, toasts, race track and leaderboard are
/// drawn into the Ball Pit frames (no extra inputs). PITMODE=Off: the text fallback through the engage.txt drawtext.
/// usage: SECS=60 PITMODE=Source OUT=x.mp4 FFMPEG=… dotnet T engage-encode</summary>
static class EngageEncode
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    sealed class Host : IEngageHost
    {
        public BallPitScene? Sc; public int Races;
        public (bool Ok, string Reply) RequestSong(string user, string query) => (true, "");
        public bool BallPit(string user, string command) => Sc?.Command(user, command, "", true) != null;
        public bool PitActive => Sc != null;
        public IReadOnlyList<string> PitNames() => Sc?.Balls.Select(b => b.Name).ToList() ?? new List<string>();
        public void Alert(string text) { }
        public void Sound(string path) { }
        public void Race(RaceGame? race) { if (race != null) Races++; Sc?.StartRace(race); }
    }

    public static int Run(string ff, string dir, string outMp4, string mode, double secs)
    {
        fails = 0;
        bool pit = mode != "Off";
        var p = new Profile { Name = "Engage", Encoder = Environment.GetEnvironmentVariable("ENCODER") ?? "libx264", MaxRetries = 2, CrossfadeSeconds = 0 };
        if (pit) { p.BallPit.Mode = mode; p.BallPit.OrbitSpeed = 4; }
        else { p.Playlist.Add("/workspace/epikchat/sample.mp4"); p.LoopPlaylist = true; }
        if (File.Exists("/tmp/zt/music1.mp3")) { p.Audio.MusicEnabled = true; p.Audio.MusicPlaylist.Add("/tmp/zt/music1.mp3"); p.Audio.MusicVolume = 0.5; }
        p.Engage.Games.RaceBetSeconds = 10; p.Engage.Games.RaceSeconds = 12; p.Engage.UserCooldownSeconds = 0;
        var flv = Path.Combine(dir, "engage-" + mode + ".flv");
        try { File.Delete(flv); File.Delete(outMp4); } catch { }
        var sc = pit ? BallPitTests.DemoScene(24, 5, p.BallPit) : null;
        var host = new Host { Sc = sc };
        var e = new EngageEngine(p.Engage, new PointsLedger(), new Random(11)) { Host = host, SelfName = "host", DefaultRoom = () => "Lobby" };
        var names = sc?.Balls.Select(b => b.Name).ToList() ?? new List<string> { "Amy", "Bobcat", "Cleo", "Dax", "Eve", "Finn" };
        foreach (var n in names) e.Ledger.Add(e.Ledger.Get("id-" + n.ToLowerInvariant(), n), 1000, true);
        if (sc != null) sc.Overlay = (c, w, h) => { var v = e.View(DateTime.Now); if (!v.Empty) EngageOverlay.Draw(c, w, h, v, p.Engage.Overlay); };
        var eng = new StreamEngine(p, ff, new[] { new CommandBuilder.Destination("File", flv) }, Array.Empty<string>(), Path.Combine(dir, "work-engage")) { BallPit = sc, TextOverlays = true };
        var log = new System.Text.StringBuilder(); eng.Log += l => { lock (log) log.AppendLine(l); };
        StreamStats? last = null; eng.Stats += st => last = st;
        var replies = new List<string>();
        void Out(EngageOutput o) { lock (replies) replies.AddRange(o.Replies.Select(r => r.Text)); }
        ChatMessage M(string u, string t, bool self = false) => new(u, t, null, false, self, "Lobby", "", self ? "" : "id-" + u.ToLowerInvariant());
        var sw = Stopwatch.StartNew(); var t0 = DateTime.Now;
        eng.Start();
        e.StartStream(DateTime.Now);
        var ticker = new Thread(() =>
        {
            while (sw.Elapsed.TotalSeconds < secs + 1)
            {
                Thread.Sleep(250); var o = e.Tick(DateTime.Now); Out(o);
                if (!pit) eng.SetEngageText(EngageOverlay.Text(e.View(DateTime.Now), p.Engage.Overlay));
            }
        }) { IsBackground = true };
        ticker.Start();
        void At(double t, Action a) { while (sw.Elapsed.TotalSeconds < t) Thread.Sleep(20); a(); }
        double f = secs / 60.0;   // timeline scales with SECS (60 s by default)
        At(2 * f, () => Out(e.Handle(M("host", $"!poll \"Best snack tonight?\" chips | popcorn | fruit | pizza {Math.Max(10, (int)(36 * f))}", true), DateTime.Now)));
        var votes = new[] { 2, 4, 2, 1, 2, 4, 3, 2 }; for (int i = 0; i < Math.Min(votes.Length, names.Count); i++) { int k = i; At((4 + 2.5 * k) * f, () => Out(e.Handle(M(names[k], "!vote " + votes[k]), DateTime.Now))); }
        if (pit)
        {
            At(14 * f, () => Out(e.StartRace("Lobby", DateTime.Now)));
            At(15 * f, () => { Out(e.Handle(M(names[0], "!joinrace"), DateTime.Now)); Out(e.Handle(M(names[1], "!joinrace"), DateTime.Now)); });
            At(17 * f, () => { Out(e.Handle(M(names[2], $"!bet {names[0]} 200"), DateTime.Now)); Out(e.Handle(M(names[3], $"!bet {names[1]} 300"), DateTime.Now)); Out(e.Handle(M(names[4], $"!bet {names[0]} 100"), DateTime.Now)); });
        }
        else At(20 * f, () => Out(e.Handle(M(names[5], "!gamble 100"), DateTime.Now)));
        At(48 * f, () => Out(e.Handle(M(names[3], "!top"), DateTime.Now)));
        At(secs, () => eng.Stop());
        Check(eng.Wait(20000), "engine stopped");
        var lg = log.ToString();
        Console.WriteLine($"  encoder fps {last?.Fps:0.0}, speed {last?.Speed:0.00}x, dup {last?.Duplicated}, drop {last?.Dropped}" + (pit ? $"; feed: {eng.PitFeed?.Frames} frames, render {eng.PitFeed?.RenderFps:0} fps" : ""));
        lock (replies) foreach (var r in replies) Console.WriteLine("  chat: " + r);
        if (fails > 0 || !File.Exists(flv) || Environment.GetEnvironmentVariable("BPLOG") != null) Console.WriteLine(lg[^Math.Min(lg.Length, 6000)..]);
        Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-i", flv, "-c", "copy", "-movflags", "+faststart", outMp4);
        var probe = Run(Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe"), "-v", "error", "-show_entries", "stream=codec_type,width,height:format=duration", "-of", "compact", outMp4);
        Console.WriteLine("  " + probe.Replace("\n", " | "));
        var dur = double.TryParse(System.Text.RegularExpressions.Regex.Match(probe, @"duration=([\d.]+)").Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        Check(dur >= secs - 2.5, $"{mode}: ~{secs:0} s encoded ({dur:0.0} s)");
        Check(probe.Contains("width=1280|height=720") && probe.Contains("codec_type=audio"), $"{mode}: 1280x720 with audio");
        Check(last != null && last.Speed > 0.9, $"{mode}: encoder kept real-time (speed {last?.Speed:0.00}x)");
        lock (replies)
        {
            Check(replies.Any(r => r.Contains("Poll closed")), $"{mode}: poll ran and closed ({e.PollHistory.FirstOrDefault()})");
            if (pit) Check(host.Races == 1 && replies.Any(r => r.Contains("wins the race") || r.Contains("won the race") || r.Contains("🏆")), $"{mode}: race ran and paid out");
        }
        if (pit) Check(!lg.Contains("Could not connect the Ball Pit feed") && (eng.PitFeed?.Frames ?? 0) >= (dur - 1) * 30, $"{mode}: pit feed real-time ({eng.PitFeed?.Frames} frames)");
        else Check(CommandBuilderHasEngage(eng), $"{mode}: engage drawtext in the filter graph");
        var stem = Path.ChangeExtension(outMp4, null);
        foreach (var (at, tag) in pit ? new[] { (12 * f, "poll"), (22 * f, "race-bets"), (34 * f, "race"), (37 * f, "race-late"), (44 * f, "results"), (51 * f, "leaderboard") } : new[] { (12 * f, "poll"), (44 * f, "results"), (51 * f, "leaderboard") })
        {
            var png = $"{stem}-{tag}.png";
            Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-ss", at.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), "-i", outMp4, "-frames:v", "1", png);
            Check(File.Exists(png), $"{mode}: frame {tag} @ {at:0.0}s → {png}");
        }
        Console.WriteLine(fails == 0 ? $"ENGAGE ENCODE ({mode}) PASSED" : $"{fails} FAILED");
        return fails;
    }
    static bool CommandBuilderHasEngage(StreamEngine eng) => File.Exists(Path.Combine(eng.WorkDir, CommandBuilder.EngageFile));
    static string Run(string exe, params string[] a)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var x in a) psi.ArgumentList.Add(x);
        using var pr = Process.Start(psi)!; var o = pr.StandardOutput.ReadToEnd() + pr.StandardError.ReadToEnd(); pr.WaitForExit(); return o.Trim();
    }
}
