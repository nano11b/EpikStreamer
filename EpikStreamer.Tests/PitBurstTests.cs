using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using EpikStreamer.Core;
/// <summary>2.8.3: the "+50 balls took the stream down" repro. Real engine (pit Source + mock radio, x264), real BallPitHost
/// (roster → scene, AvatarCache loading file:// avatars), a fake UI thread delivering the member list like the chat panel does.
/// dotnet T pitburst   env: SECS (60), OUT, BURST_AT (15), BIG_AT (35; 0 = off), AVSIZE (avatar px, 1024)</summary>
static class PitBurstTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static double Env(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

    public static int Run(string ff, string dir, string outMp4)
    {
        fails = 0; Environment.SetEnvironmentVariable("EPIK_TEST_ALLOW_FILE", "1");
        double secs = Env("SECS", 60), burstAt = Env("BURST_AT", 15), bigAt = Env("BIG_AT", 35); int avSize = (int)Env("AVSIZE", 1024);
        Directory.CreateDirectory(dir); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outMp4))!);
        // 40 distinct avatar photos (noise, so the JPEG decode is realistic); 550 people reuse them through distinct URLs
        var avDir = Path.Combine(dir, $"avatars-src-{avSize}"); Directory.CreateDirectory(avDir);
        for (int i = 0; i < 40; i++)
        {
            var f = Path.Combine(avDir, $"a{i}.jpg"); if (File.Exists(f)) continue;
            RadioTests.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"cellauto=s={avSize}x{avSize}:rule={18 + i * 3 % 200},hue=h={i * 37}", "-frames:v", "1", "-q:v", "3", f);
        }
        string Av(int i) => new Uri(Path.Combine(avDir, $"a{i % 40}.jpg")).AbsoluteUri + "?u=" + i;
        var ms = new RadioTests.MockStation();
        var mp3 = Path.Combine(dir, "radio-tone.mp3");
        if (!File.Exists(mp3)) RadioTests.Run(ff, "-hide_banner", "-y", "-f", "lavfi", "-i", "sine=f=330:d=30,volume=0.6", "-f", "lavfi", "-i", "sine=f=550:d=30,volume=0.3", "-filter_complex", "amix=inputs=2,aformat=channel_layouts=stereo", "-c:a", "libmp3lame", "-b:a", "128k", "-ar", "44100", mp3);
        ms.Mp3 = File.ReadAllBytes(mp3);
        var p = new Profile { Name = "PitBurst", Encoder = Environment.GetEnvironmentVariable("ENCODER") ?? "libx264", MaxRetries = 5 };
        p.Playlist.Clear(); SourceMode.Set(p.BallPit, SourceMode.BallPit); p.BallPit.DefaultsVersion = 284;
        if (Environment.GetEnvironmentVariable("BG") is { Length: > 0 } bg) p.BallPit.Background = bg;
        if (Environment.GetEnvironmentVariable("QUALITY") is { Length: > 0 } ql) p.BallPit.Quality = ql; p.Audio.Normalize = "Off";
        p.Radio = new RadioSettings { Enabled = true, Url = ms.Base + "/public/test/playlist.pls", Volume = 0.9, BeatPulse = true };
        var rh = new RadioHost(p.Radio, Path.Combine(dir, "art")); rh.Apply(p.Radio); var urls = rh.ResolveAsync(default).Result;
        var cache = Path.Combine(dir, "avcache-" + Guid.NewGuid().ToString("N")[..6]);
        using var host = new BallPitHost(p.BallPit, cache);
        host.Scene.BeatStrength = 0.6f;
        // fake UI thread (Avalonia/WPF dispatcher): every roster call happens here, like MainWindow.V25
        var ui = new BlockingCollection<Action>(); var uiThread = new Thread(() => { foreach (var a in ui.GetConsumingEnumerable()) try { a(); } catch (Exception ex) { Console.WriteLine("UI exception: " + ex.Message); } }) { IsBackground = true, Name = "fake UI" }; uiThread.Start();
        var members = Enumerable.Range(0, 30).Select(i => new BallPitRoster.Member($"Regular{i}", Av(i))).ToList();
        ui.Add(() => host.OnMembers(members, true, members.Count));
        var flv = Path.Combine(dir, "pitburst.flv"); try { File.Delete(flv); File.Delete(outMp4); } catch { }
        var eng = new StreamEngine(p, ff, new[] { new CommandBuilder.Destination("File", flv) }, Array.Empty<string>(), Path.Combine(dir, "work-pitburst"))
        { BallPit = host.Scene, RadioUrls = urls, RadioLevel = host.Scene.AudioLevel, RadioInfo = rh.CardInfo };
        var log = new System.Text.StringBuilder(); eng.Log += l => { lock (log) log.AppendLine(l); };
        var samples = new List<(double wall, long frame, double speed)>(); var sw = Stopwatch.StartNew();
        eng.Stats += st => { lock (samples) samples.Add((sw.Elapsed.TotalSeconds, st.Frames, st.Speed)); };
        var cardTimer = new System.Threading.Timer(_ => host.Scene.RadioCard = rh.PitCard(), null, 0, 500);
        eng.Start();
        // sampler: feed state every 100 ms
        var feedLog = new List<string>(); double maxLag = 0, maxLagAt = 0; long feedGen = 0; BallPitFeed? lastFeed = null; int feeds = 0;
        var proc = Process.GetCurrentProcess(); var cpu0 = proc.TotalProcessorTime; double cpuWall0 = 0;
        bool burst = false, big = false; int chatN = 0; var rnd = new Random(5);
        while (sw.Elapsed.TotalSeconds < secs + 1.5)
        {
            Thread.Sleep(100); double t = sw.Elapsed.TotalSeconds;
            var f = eng.PitFeed; if (f != null && f != lastFeed) { lastFeed = f; feeds++; }
            if (f != null && f.Lag > maxLag) { maxLag = f.Lag; maxLagAt = t; }
            if (!burst && t >= burstAt)
            {
                burst = true; var more = members.Concat(Enumerable.Range(30, 50).Select(i => new BallPitRoster.Member($"Burst{i}", Av(i)))).ToList(); members = more;
                ui.Add(() => host.OnMembers(more, true, more.Count)); Console.WriteLine($"  t={t:0.0}s +50 joins (member list {more.Count})");
            }
            if (!big && bigAt > 0 && t >= bigAt)
            {
                big = true; var all = Enumerable.Range(0, 500).Select(i => new BallPitRoster.Member(i < 30 ? $"Regular{i}" : $"Member{i}", Av(i))).ToList();
                ui.Add(() => host.OnMembers(all, true, all.Count)); Console.WriteLine($"  t={t:0.0}s 500-member room");
            }
            if ((int)(t * 10) % 7 == 0) { var u = $"Regular{rnd.Next(30)}"; var txt = "hello " + chatN++; ui.Add(() => host.OnChat(new ChatMessage(u, txt))); }
            if ((int)(t * 10) % 10 == 0 && f != null)
                lock (feedLog) feedLog.Add($"{t:0} s: balls {host.Scene.Count} waiting {host.Scene.Waiting} lag {f.Lag:0.00} skipped {f.Skipped} repeats {f.Repeats} renderfps {f.RenderFps:0} {Extra(f)}");
        }
        var stopAt = sw.Elapsed.TotalSeconds; proc.Refresh(); var cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / stopAt;
        eng.Stop(); cardTimer.Dispose(); ui.CompleteAdding();
        Check(eng.Wait(20000), "engine stopped");
        var lg = log.ToString();
        File.WriteAllText(Path.ChangeExtension(outMp4, null) + "-log.txt", lg + "\n---- feed\n" + string.Join("\n", feedLog));
        foreach (var l in feedLog.Where((_, i) => i % 5 == 0)) Console.WriteLine("  " + l);
        foreach (var l in lg.Split('\n').Where(l => Regex.IsMatch(l, "⚠|Encoder exited|No progress|restart|Ball Pit|error", RegexOptions.IgnoreCase) && !l.StartsWith("> ffmpeg")).Take(30)) Console.WriteLine("  log: " + l.Trim());
        Console.WriteLine($"  test process CPU {cpu:0.00} cores; max feed lag {maxLag:0.00} s at {maxLagAt:0.0} s; feeds started {feeds}; balls {host.Scene.Count} waiting {host.Scene.Waiting}; avatars failed {host.Avatars.Failed}");
        List<(double wall, long frame, double speed)> S; lock (samples) S = samples.Where(x => x.frame > 0 && x.wall < stopAt).ToList();
        if (S.Count > 10)
        {
            double fps = p.Fps;
            double Lag((double wall, long frame, double speed) x) => (x.wall - S[0].wall) - (x.frame - S[0].frame) / fps;
            Console.WriteLine("  lag (wall − output) by second: " + string.Join(" ", S.GroupBy(x => (int)x.wall).Select(g => Lag(g.Last()).ToString("0.00", CultureInfo.InvariantCulture))));
            var after = S.Where(x => x.wall > Math.Min(10, burstAt - 2)).ToList();
            var l0 = Lag(after.First()); var l1 = Lag(after.Last()); var lmax = after.Max(Lag);
            var span = after.Last().wall - after.First().wall; var rate = (after.Last().frame - after.First().frame) / fps / span;
            Console.WriteLine($"  pacing from {after.First().wall:0} s: output {rate:0.000} s per wall s over {span:0} s; lag {l0:0.00} → {l1:0.00} s (max {lmax:0.00})");
            Check(rate is > 0.97 and < 1.03, $"30 fps pacing through the burst ({rate:0.000})");
            Check(Math.Abs(l1 - l0) < 1.0 && lmax - l0 < 1.0, $"flat lag ({l1 - l0:+0.00;-0.00} s, peak +{lmax - l0:0.00} s)");
            long gaps = 0; for (int i = 1; i < S.Count; i++) if (S[i].wall - S[i - 1].wall > 2.5) gaps++;
            Check(gaps == 0, $"encoder progress never stalled > 2.5 s ({gaps})");
        }
        else Check(false, "enough stats samples");
        Check(!Regex.IsMatch(lg, "Encoder exited|No progress from encoder|restarting the encoder"), "encoder never restarted");
        Check(feeds == 1, $"one Ball Pit feed for the whole run ({feeds})");
        RadioTests.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-i", flv, "-c", "copy", "-movflags", "+faststart", outMp4);
        var fp = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe"); if (!File.Exists(fp)) fp = "ffprobe";
        var probe = RadioTests.Run(fp, "-v", "error", "-show_entries", "stream=codec_type,width,height:format=duration", "-of", "compact", outMp4);
        var dur = double.TryParse(Regex.Match(probe, @"duration=([\d.]+)").Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        Check(dur >= secs - 6, $"~{secs:0} s encoded ({dur:0.0} s)");
        Check(probe.Contains("codec_type=audio") && probe.Contains("codec_type=video"), "video + audio present");
        var nf = RadioTests.Run(fp, "-v", "error", "-count_frames", "-select_streams", "v", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", outMp4);
        Check(int.TryParse(nf.Trim(), out var frames) && Math.Abs(frames - dur * p.Fps) < p.Fps, $"video continuous: {frames} frames ≈ {dur:0.0} s × {p.Fps}");
        foreach (var at in new[] { burstAt + 3, bigAt + 8 }) if (at > 0 && at < dur) RadioTests.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-ss", at.ToString(CultureInfo.InvariantCulture), "-i", outMp4, "-frames:v", "1", Path.ChangeExtension(outMp4, null) + $"-t{at:0}.png");
        rh.Dispose(); ms.Dispose(); try { Directory.Delete(cache, true); } catch { }
        Console.WriteLine(fails == 0 ? "PITBURST PASSED" : $"{fails} FAILED");
        return fails;
    }
    static string Extra(BallPitFeed f) => $"| {f.StatsLine}, repeats {f.Repeats}, late {f.LateFrames}";
}
