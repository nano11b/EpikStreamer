using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using EpikStreamer.Core;
/// <summary>2.8.2: streaming with only the Ball Pit scene (+ radio, or silence) and no playlist.
/// dotnet T pitonly            unit: source selector ↔ Ball Pit mode sync, start validation, encoder args (radio on/off)
/// dotnet T pitonly-encode     ~30 s encode, no playlist, pit Source + mock radio (RADIO=0 → radio off); OUT, SECS</summary>
static class PitOnlyTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    public static int Unit()
    {
        fails = 0;
        // source selector ↔ Ball Pit tab mode
        var b = new BallPitSettings();
        Check(SourceMode.Of(b) == SourceMode.Playlist, "default source = playlist (pit Off)");
        SourceMode.Set(b, SourceMode.BallPit); Check(b.Mode == "Source" && b.Active && SourceMode.Of(b) == SourceMode.BallPit, "Ball Pit scene → BallPit.Mode = Source");
        SourceMode.Set(b, SourceMode.Playlist); Check(b.Mode == "Off", "back to playlist → pit Off");
        b.Mode = "Overlay"; SourceMode.Set(b, SourceMode.Playlist); Check(b.Mode == "Overlay" && SourceMode.Of(b) == SourceMode.Playlist, "playlist keeps an Overlay pit");
        SourceMode.Set(b, SourceMode.BallPit); Check(b.Mode == "Source", "Overlay → Ball Pit scene = Source");
        b.Mode = "Overlay"; Check(SourceMode.Of(b) == SourceMode.Playlist, "Ball Pit tab set to Overlay → selector shows Playlist");
        var p = new Profile { Name = "x" };
        var j = System.Text.Json.JsonSerializer.Serialize(p, ProfileStore.Json);
        SourceMode.Set(p.BallPit, SourceMode.BallPit);
        var p2 = System.Text.Json.JsonSerializer.Deserialize<Profile>(System.Text.Json.JsonSerializer.Serialize(p, ProfileStore.Json), ProfileStore.Json)!;
        Check(SourceMode.Of(p2.BallPit) == SourceMode.BallPit && !j.Contains("SourceKind"), "the choice persists as BallPit.Mode (no second setting to drift)");
        // start validation
        var e = new Profile { Name = "e" };
        Check(SourceMode.StartProblem(e)?.Contains("Ball Pit scene") == true, "playlist source + empty playlist → blocked, hint names the Ball Pit option");
        e.Playlist.Add("/nonexistent/a.mp4"); Check(SourceMode.StartProblem(e) == "None of the playlist files exist.", "missing files → blocked");
        e.Playlist.Clear(); e.BallPit.Mode = "Source";
        Check(SourceMode.StartProblem(e) == null && SourceMode.PitOnly(e), "Ball Pit scene + empty playlist → allowed (pit-only)");
        e.Playlist.Add("/nonexistent/a.mp4"); Check(SourceMode.StartProblem(e) == null && SourceMode.PitOnly(e), "Ball Pit scene + only missing files → allowed, still pit-only");
        Check(SourceMode.Note(e).Contains("silence"), "note: radio off → silence");
        e.Radio = new RadioSettings { Enabled = true, Url = "https://radio.example.invalid/x.pls" };
        Check(SourceMode.Note(e).Contains("Radio tab"), "note: radio on → audio from the Radio tab");
        // encoder command with an empty playlist + pit Source: live (wall-clock) inputs, no -re; radio input only when on
        var q = new Profile { Name = "q" }; q.BallPit.Mode = "Source";
        var dest = new[] { new CommandBuilder.Destination("File", "/tmp/x.flv") };
        foreach (var radio in new[] { true, false })
        {
            var lo = new CommandBuilder.LiveOptions(BallPitPort: 41000, BallPitMode: "Source", BallPitW: q.Width, BallPitH: q.Height, RadioPort: radio ? 41001 : 0);
            var a = CommandBuilder.EncoderArgs(q, dest, false, lo);
            var s = string.Join(" ", a); var fc = a[a.IndexOf("-filter_complex") + 1];
            Check(!a.Contains("-re") && s.Contains("-use_wallclock_as_timestamps 1"), $"radio {(radio ? "on" : "off")}: no -re (2.6.1 pacing), pit input on the wall clock");
            Check(fc.Contains("[0:v]nullsink"), $"radio {(radio ? "on" : "off")}: pit is the picture, the filler's video is discarded");
            Check(radio ? Regex.IsMatch(fc, @"\[\d+:a\]aresample=48000:async=1000.*volume@radio") : !fc.Contains("volume@radio"), $"radio {(radio ? "on: radio input mixed" : "off: no radio input")}");
            Check(fc.Contains("[0:a]") && a.Contains("[aout]"), $"radio {(radio ? "on" : "off")}: the filler's (silent anullsrc) audio keeps an audio track");
        }
        Console.WriteLine(fails == 0 ? "PITONLY UNIT PASSED" : $"{fails} FAILED");
        return fails;
    }

    public static int Encode(string ff, string dir, string outMp4, bool radio, double seconds)
    {
        fails = 0;
        Directory.CreateDirectory(dir); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outMp4))!);
        RadioTests.MockStation? ms = null; List<string>? urls = null; RadioHost? rh = null;
        var p = new Profile { Name = "PitOnly", Encoder = Environment.GetEnvironmentVariable("ENCODER") ?? "libx264", MaxRetries = 2 };
        p.Playlist.Clear(); SourceMode.Set(p.BallPit, SourceMode.BallPit); p.BallPit.OrbitSpeed = 6; p.BallPit.ShowCount = true; p.Audio.Normalize = "Off";
        Check(SourceMode.StartProblem(p) == null, "UI start validation passes with an empty playlist");
        if (radio)
        {
            ms = new RadioTests.MockStation();
            var mp3 = Path.Combine(dir, "radio-tone.mp3");
            if (!File.Exists(mp3)) RadioTests.Run(ff, "-hide_banner", "-y", "-f", "lavfi", "-i", "sine=f=330:d=30,volume=0.6", "-f", "lavfi", "-i", "sine=f=550:d=30,volume=0.3", "-filter_complex", "amix=inputs=2,aformat=channel_layouts=stereo", "-c:a", "libmp3lame", "-b:a", "128k", "-ar", "44100", mp3);
            ms.Mp3 = File.ReadAllBytes(mp3);
            p.Radio = new RadioSettings { Enabled = true, Url = ms.Base + "/public/test/playlist.pls", Volume = 0.9, BeatPulse = true, ShowListeners = true };
            rh = new RadioHost(p.Radio, Path.Combine(dir, "art")); rh.Apply(p.Radio);
            urls = rh.ResolveAsync(default).Result;
        }
        var flv = Path.Combine(dir, "pitonly.flv"); try { File.Delete(flv); File.Delete(outMp4); } catch { }
        var sc = BallPitTests.DemoScene(30, 11, p.BallPit); sc.BeatStrength = 0.6f;
        var eng = new StreamEngine(p, ff, new[] { new CommandBuilder.Destination("File", flv) }, Array.Empty<string>(), Path.Combine(dir, "work-pitonly"))
        { BallPit = sc, RadioUrls = urls, RadioLevel = sc.AudioLevel, RadioInfo = rh == null ? null : rh.CardInfo };
        var log = new System.Text.StringBuilder(); eng.Log += l => { lock (log) log.AppendLine(l); };
        var samples = new List<(double wall, long frame, double speed, string name, int count)>(); var sw = Stopwatch.StartNew();
        eng.Stats += st => { lock (samples) samples.Add((sw.Elapsed.TotalSeconds, st.Frames, st.Speed, st.ItemName ?? "", st.ItemCount)); };
        var tracks = new List<string>(); eng.TrackChanged += t => tracks.Add(t);
        System.Threading.Timer? cardTimer = rh == null ? null : new(_ => sc.RadioCard = rh.PitCard(), null, 0, 500);
        eng.Start();
        Check(eng.PitOnly, "engine started in pit-only mode (no playlist)");
        eng.Skip();
        while (sw.Elapsed.TotalSeconds < seconds + 1.5) Thread.Sleep(50);
        var stopAt = sw.Elapsed.TotalSeconds; eng.Stop(); cardTimer?.Dispose();
        Check(eng.Wait(20000), "engine stopped");
        var lg = log.ToString();
        if (Environment.GetEnvironmentVariable("BPLOG") != null) Console.WriteLine(lg);
        Check(lg.Contains("no playlist") && lg.Contains(radio ? "audio = the radio" : "silence (radio off)"), "log names the pit-only source and the audio");
        Check(lg.Contains("Nothing to skip"), "Next/skip is a no-op in pit-only mode");
        Check(tracks.Count == 0, $"no track changes / now-playing / request / history entries for the filler ({tracks.Count})");
        List<(double wall, long frame, double speed, string name, int count)> S; lock (samples) S = samples.Where(x => x.frame > 0 && x.wall < stopAt).ToList();   // the final stats line after Stop is the encoder flushing its queue, not pacing
        Check(S.Count > 0 && S.All(x => x.name == "Ball Pit" && x.count == 0), "stats show \"Ball Pit\" (not the filler file)");
        // pacing: frames vs wall clock after start-up; the lag must not grow
        if (S.Count > 10)
        {
            double fps = p.Fps;
            var steady = S.Where(x => x.wall > seconds * 0.4).ToList();   // ffmpeg speed= is cumulative from process start; judge the steady state by output vs wall clock
            double Lag((double wall, long frame, double speed, string, int) x) => (x.wall - S[0].wall) - (x.frame - S[0].frame) / fps;
            // print the lag curve: a one-off start-up catch-up (the encoder drains what queued while it opened) is fine; a trend is not
            Console.WriteLine("  lag (wall − output) by second: " + string.Join(" ", S.GroupBy(x => (int)x.wall).Select(g => Lag(g.Last()).ToString("0.00", CultureInfo.InvariantCulture))));
            steady = S.Where(x => x.wall > seconds * 0.6).ToList();
            var lag0 = Lag(steady.First()); var lag1 = Lag(steady.Last());
            var span = steady.Last().wall - steady.First().wall; var rate = (steady.Last().frame - steady.First().frame) / fps / span;
            var sp0 = steady.First().speed; var sp1 = steady.Last().speed;
            Console.WriteLine($"  pacing: output {rate:0.000} s per wall s over the last {span:0} s (after start-up catch-up); ffmpeg speed {sp0:0.000}x → {sp1:0.000}x (cumulative, start-up included); lag {lag0:0.00} → {lag1:0.00} s");
            Check(rate is > 0.97 and < 1.03, $"steady speed ≈ 1.0x ({rate:0.000})");
            Check(sp1 >= sp0 - 0.005 && sp1 > 0.85, "ffmpeg speed= converging to 1.0, not falling behind");
            Check(Math.Abs(lag1 - lag0) < 1.0, $"no growing lag ({lag1 - lag0:+0.00;-0.00} s)");
        }
        else Check(false, "enough stats samples");
        Check(!lg.Contains("Resumed reading") && !lg.Contains("Encoder exited"), "no readrate catch-up, encoder never restarted");
        RadioTests.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-i", flv, "-c", "copy", "-movflags", "+faststart", outMp4);
        var fp = Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe"); if (!File.Exists(fp)) fp = "ffprobe";
        var probe = RadioTests.Run(fp, "-v", "error", "-show_entries", "stream=codec_type,codec_name,sample_rate,width,height:format=duration", "-of", "compact", outMp4);
        Console.WriteLine("  " + probe.Replace("\n", " | "));
        var dur = double.TryParse(Regex.Match(probe, @"duration=([\d.]+)").Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        Check(dur >= seconds - 6, $"~{seconds:0} s encoded ({dur:0.0} s)");
        Check(probe.Contains("codec_type=audio") && probe.Contains("codec_type=video"), "video + audio streams present");
        var nf = RadioTests.Run(fp, "-v", "error", "-count_frames", "-select_streams", "v", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", outMp4);
        Check(int.TryParse(nf.Trim(), out var frames) && Math.Abs(frames - dur * p.Fps) < p.Fps, $"video continuous: {frames} frames ≈ {dur:0.0} s × {p.Fps}");
        var rms = RadioTests.RmsPerSecond(ff, outMp4);
        Console.WriteLine("  RMS dB per s: " + string.Join(" ", rms.Select(x => x.ToString("0", CultureInfo.InvariantCulture))));
        int loud = rms.Count(x => x > -50);
        if (radio) Check(loud >= rms.Count * 0.7, $"radio audible ({loud}/{rms.Count} s above -50 dB)");
        else Check(loud == 0, $"radio off: the audio track is silence ({loud} loud seconds)");
        var png = Path.ChangeExtension(outMp4, null) + "-t10.png";
        RadioTests.Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-ss", "10", "-i", outMp4, "-frames:v", "1", png);
        Check(File.Exists(png), "frame: " + png);
        rh?.Dispose(); ms?.Dispose();
        Console.WriteLine(fails == 0 ? "PITONLY ENCODE PASSED" : $"{fails} FAILED");
        return fails;
    }
}
