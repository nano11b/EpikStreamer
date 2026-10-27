using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using EpikStreamer.Core;

/// <summary>v2.6 Radio: playlist parsing, AzuraCast client (against a local mock), graph wiring, relay drop/recovery, and
/// optional live checks against a real station (read-only: now-playing + stream; never submits a request).</summary>
static class RadioTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static readonly string Mock = Path.Combine(AppContext.BaseDirectory, "../../../mock");

    /// <summary>Local stand-in for an AzuraCast station + Icecast mount (all on 127.0.0.1).</summary>
    public sealed class MockStation : IDisposable
    {
        readonly HttpListener _l = new(); public readonly int Port; public string Base => $"http://127.0.0.1:{Port}";
        public volatile bool Blocked; public byte[] Mp3 = Array.Empty<byte>(); public int ByteRate = 16000;
        public readonly List<string> Log = new(); public readonly List<string?> ApiKeys = new(); public int Submits, StreamConnects;
        public string RefuseMessage = ""; readonly List<HttpListenerResponse> _streams = new();
        public MockStation()
        {
            for (int i = 0; ; i++) { Port = StreamControl.FreePort(); try { _l.Prefixes.Clear(); _l.Prefixes.Add(Base + "/"); _l.Start(); break; } catch when (i < 5) { } }
            new Thread(() => { while (_l.IsListening) { try { var c = _l.GetContext(); ThreadPool.QueueUserWorkItem(_ => Serve(c)); } catch { break; } } }) { IsBackground = true }.Start();
        }
        public void Block(bool b) { Blocked = b; if (b) lock (_streams) { foreach (var r in _streams) try { r.Abort(); } catch { } _streams.Clear(); } }
        void Text(HttpListenerContext c, string body, string type = "application/json", int code = 200)
        { var b = Encoding.UTF8.GetBytes(body); c.Response.StatusCode = code; c.Response.ContentType = type; c.Response.ContentLength64 = b.Length; c.Response.OutputStream.Write(b); c.Response.Close(); }
        void Serve(HttpListenerContext c)
        {
            var path = c.Request.Url!.AbsolutePath; var q = c.Request.QueryString;
            lock (Log) { Log.Add(c.Request.HttpMethod + " " + c.Request.Url.PathAndQuery); ApiKeys.Add(c.Request.Headers["X-API-Key"]); }
            try
            {
                if (path == "/public/test/playlist.pls")
                    Text(c, $"[playlist]\nFile1={Base}/hls/test/live.m3u8\nTitle1=Test - HLS\nFile2={Base}/listen/test/radio.mp3\nTitle2=Test - /radio.mp3 (128kbps MP3)\nNumberOfEntries=2\nVersion=2\n", "audio/x-scpls");
                else if (path == "/api/nowplaying/test")
                    Text(c, File.ReadAllText(Path.Combine(Mock, "azuracast-nowplaying.json")).Replace("\"shortcode\": \"labsfm\"", "\"shortcode\": \"test\""));
                else if (path == "/api/station/test/requests")
                {
                    var ph = (q["searchPhrase"] ?? "").ToLowerInvariant();
                    var rows = new[] { ("r1", "Daft Punk", "One More Time"), ("r2", "Daft Punk", "Around the World"), ("r3", "Moby", "Porcelain") }
                        .Where(r => ph.Split(' ').All(w => (r.Item2 + " " + r.Item3).ToLowerInvariant().Contains(w)))
                        .Select(r => $"{{\"request_id\":\"{r.Item1}\",\"request_url\":\"/api/station/1/request/{r.Item1}\",\"song\":{{\"id\":\"{r.Item1}\",\"text\":\"{r.Item2} - {r.Item3}\",\"artist\":\"{r.Item2}\",\"title\":\"{r.Item3}\",\"album\":\"\",\"art\":\"\"}}}}");
                    Text(c, $"{{\"page\":1,\"per_page\":{q["rowCount"]},\"total\":{rows.Count()},\"rows\":[{string.Join(",", rows)}]}}");
                }
                else if (path.StartsWith("/api/station/1/request/"))
                {
                    if (c.Request.HttpMethod != "POST") { Text(c, "{\"code\":405,\"message\":\"Method not allowed\"}", code: 405); return; }
                    Interlocked.Increment(ref Submits);
                    if (RefuseMessage.Length > 0) Text(c, $"{{\"code\":400,\"type\":\"Exception\",\"message\":\"{RefuseMessage}\",\"formatted_message\":\"{RefuseMessage}\",\"success\":false}}", code: 400);
                    else Text(c, "{\"success\":true,\"message\":\"Your request has been submitted and will be played soon.\",\"formatted_message\":\"Your request has been submitted and will be played soon.\"}");
                }
                else if (path == "/listen/test/radio.mp3")
                {
                    if (Blocked) { Text(c, "down", "text/plain", 503); return; }
                    Interlocked.Increment(ref StreamConnects);
                    var r = c.Response; r.ContentType = "audio/mpeg"; r.SendChunked = true; lock (_streams) _streams.Add(r);
                    var o = r.OutputStream; var sw = Stopwatch.StartNew(); long sent = 0; int pos = 0;
                    while (!Blocked)
                    {
                        long due = (long)(sw.Elapsed.TotalSeconds * ByteRate) + ByteRate * 2;   // 2 s burst-on-connect like Icecast
                        if (sent >= due) { Thread.Sleep(20); continue; }
                        int n = Math.Min(4096, Mp3.Length - pos); o.Write(Mp3, pos, n); o.Flush(); pos = (pos + n) % Mp3.Length; sent += n;
                    }
                    try { r.Abort(); } catch { }
                }
                else Text(c, "{\"code\":404}", code: 404);
            }
            catch { try { c.Response.Abort(); } catch { } }
        }
        public void Dispose() { Block(true); try { _l.Stop(); } catch { } }
    }

    public static int Unit()
    {
        fails = 0;
        // ---- playlists ----
        var pls = RadioPlaylist.Parse(File.ReadAllText(Path.Combine(Mock, "labsfm.pls")), "https://radio.seven-labs.com/public/labsfm/playlist.pls");
        Check(pls.Count == 2 && pls[0].Url == "https://radio.seven-labs.com/listen/labsfm/impulsivefrequencies" && pls[1].IsHls, "real 7LFM .pls parsed (MP3 + HLS)");
        var ord = RadioPlaylist.Order(pls, false); Check(!ord[0].IsHls && ord[1].IsHls, "MP3 preferred, HLS fallback");
        Check(RadioPlaylist.Order(pls, true)[0].IsHls, "Prefer HLS puts HLS first");
        var rev = RadioPlaylist.Order(new[] { pls[1], pls[0] }, false); Check(!rev[0].IsHls, "order independent of file order");
        var m3u = RadioPlaylist.Parse("#EXTM3U\n#EXTINF:-1,Station AAC\nhttps://s.example/aac\n# c\nrelative/mp3\n", "https://s.example/list/x.m3u");
        Check(m3u.Count == 2 && m3u[0].Title == "Station AAC" && m3u[1].Url == "https://s.example/list/relative/mp3", "m3u with EXTINF + relative URL");
        var hls = RadioPlaylist.Parse("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\nseg1.aac\n", "https://s.example/hls/live.m3u8");
        Check(hls.Count == 1 && hls[0].Url == "https://s.example/hls/live.m3u8", "an HLS playlist is the stream itself");
        Check(RadioPlaylist.ParsePls("[playlist]\nFile1=file:///etc/passwd\nFile2=javascript:x\n").Count == 0, "non-http entries ignored");
        Check(RadioPlaylist.IsPlaylistUrl("https://h/public/x/playlist.pls?x=1") && !RadioPlaylist.IsPlaylistUrl("https://h/listen/x/radio.mp3"), "playlist URL detection");

        // ---- AzuraCast detection ----
        var t = AzuraClient.Detect("https://radio.seven-labs.com/public/labsfm/playlist.pls");
        Check(t == new AzuraTarget("https://radio.seven-labs.com", "labsfm"), "detect base + shortcode from the .pls URL");
        Check(AzuraClient.Detect("https://radio.seven-labs.com/listen/labsfm/impulsivefrequencies") == t, "detect from a /listen mount");
        Check(AzuraClient.Detect("https://radio.seven-labs.com/hls/labsfm/live.m3u8") == t, "detect from the HLS URL");
        Check(AzuraClient.Detect("https://x.org/radio/public/abc_1/playlist.m3u") == new AzuraTarget("https://x.org/radio", "abc_1"), "sub-path installs");
        Check(AzuraClient.Detect("https://ice.example/stream.mp3") == null, "non-AzuraCast → no detection");
        var ov = RadioHost.TargetFor(new RadioSettings { Url = "https://ice.example/stream.mp3", ApiBase = "radio.example.com/", Shortcode = "main" });
        Check(ov == new AzuraTarget("https://radio.example.com", "main"), "manual API base / shortcode override");

        // ---- now playing ----
        var np = AzuraNowPlaying.Parse(File.ReadAllText(Path.Combine(Mock, "azuracast-nowplaying.json")), DateTime.UtcNow);
        Check(np.Station == "7LFM" && np.Shortcode == "labsfm" && np.StationId == "1" && np.RequestsEnabled, "station fields");
        Check(np.Song.Text.Length > 0 && np.History.Count > 0, $"song + history parsed ({np.Display})");
        Check(np.Duration <= 0 || np.Elapsed <= np.Duration, "elapsed sane");
        var off = AzuraNowPlaying.Parse("{\"station\":{\"name\":\"7LFM\",\"id\":1},\"is_online\":false,\"now_playing\":{\"elapsed\":1761500000,\"duration\":0,\"song\":{\"id\":\"x\",\"text\":\"Station Offline\",\"artist\":\"\",\"title\":\"Station Offline\"}},\"playing_next\":null,\"song_history\":[]}", DateTime.UtcNow);
        Check(!off.Online && off.Elapsed == 0 && off.Duration == 0 && off.PositionAt(DateTime.UtcNow.AddSeconds(30)) == 0, "offline station: epoch elapsed / zero duration clamped");
        var np2 = AzuraNowPlaying.Parse("{\"station\":{\"name\":\"S\"},\"now_playing\":{\"elapsed\":10,\"duration\":200,\"song\":{\"text\":\"A - B\",\"artist\":\"A\",\"title\":\"B\"}}}", DateTime.UtcNow.AddSeconds(-5));
        Check(Math.Abs(np2.PositionAt(DateTime.UtcNow) - 15) < 0.5 && np2.PositionAt(DateTime.UtcNow.AddHours(1)) == 200, "position interpolated + clamped");

        // ---- card variants ----
        var radioInfo = new NowPlayingInfo("One More Time", "", "Moby – Porcelain", 0, 0, "", "ON AIR · 7LFM", "Daft Punk", true);
        Check(NowPlayingCard.TimeText(radioInfo) == "● LIVE", "radio card without duration shows LIVE");
        var pc = new Profile(); var ra = NowPlayingCard.RenderArgs(pc, radioInfo, "x.png", false);
        Check(!string.Join(" ", ra).Contains("color=white@0.25"), "no progress bar when the duration is unknown");
        Check(string.Join(" ", NowPlayingCard.RenderArgs(pc, radioInfo with { Duration = 200, Position = 50 }, "x.png", false)).Contains("color=white@0.25"), "progress bar with a duration");

        // ---- chat commands ----
        var cs = new ChatSettings { CommandsEnabled = true, CooldownSeconds = 10 }; var cc = new ChatCommands(cs) { RadioListed = true };
        var ctx = new ChatCommands.Context { Song = "playlist.mp4" };
        var now = DateTime.UtcNow;
        Check(cc.Handle(new("amy", "!song"), ctx, now) == "Now playing: playlist.mp4", "!song without radio → playlist");
        var host = new RadioHost(new RadioSettings { Enabled = true, Url = "x" }, "/tmp/epiktest/art");
        ctx = new ChatCommands.Context { Song = "playlist.mp4", Radio = "Daft Punk – One More Time", RadioLast = "Moby – Porcelain", Station = "7LFM",
            RadioSongTemplate = new RadioSettings().SongTemplate, RadioLastTemplate = new RadioSettings().LastTemplate };
        Check(cc.Handle(new("amy", "!np"), ctx, now.AddSeconds(20)) == "Now playing on 7LFM: Daft Punk – One More Time", "!np → radio track");
        Check(cc.Handle(new("bob", "!song"), ctx, now.AddSeconds(25)) == null, "!song shares !np's cooldown");
        Check(cc.Handle(new("bob", "!lastsong"), ctx, now.AddSeconds(26)) == "Last song on 7LFM: Moby – Porcelain", "!lastsong from song_history");
        Check(cc.Handle(new("bob", "!help"), ctx, now.AddSeconds(27))!.Contains("!lastsong"), "!help lists radio commands");
        Check(cc.Handle(new("bob", "!lastsong"), new ChatCommands.Context(), now.AddSeconds(60)) == null, "!lastsong ignored without radio");

        // ---- graph ----
        foreach (var caps in new[] { FfmpegCaps.All, new FfmpegCaps { InputThreadQueue = false } })
        {
            var p = new Profile(); p.Playlist.Add("/x.mp4"); p.Radio.Enabled = true; p.Radio.Url = "x"; p.Audio.Ducking = false; p.Radio.Duck = true;
            var lo = new CommandBuilder.LiveOptions(0, 0, null, false, RadioPort: 7777, RadioVolume: 0.4);
            var a = CommandBuilder.EncoderArgs(p, new List<CommandBuilder.Destination> { new("f", "/tmp/x.flv") }, false, lo, caps);
            var g = a[a.IndexOf("-filter_complex") + 1]; int ri = a.IndexOf("tcp://127.0.0.1:7777?listen=1&listen_timeout=20000");
            Check(ri > 0 && a[ri - 1] == "-i" && a[ri - 6] == "s16le", "radio input = raw PCM TCP listen");
            Check(caps.InputThreadQueue == (a[ri - 9] == "-thread_queue_size"), $"radio -thread_queue_size only when the build accepts it ({caps.InputThreadQueue})");
            Check(g.Contains("[1:a]aformat=sample_fmts=fltp:channel_layouts=stereo,volume@radio=0.4[ara]") && g.Contains("[ara][asc]sidechaincompress@duck") && g.Contains("[aout]"), "radio branch: volume@radio + ducking");
            p.Audio.MusicEnabled = true; p.Radio.Normalize = true;
            var lo2 = lo with { MusicPort = 6666 };
            var g2 = CommandBuilder.EncoderGraph(p, false, lo2, 1, -1, caps, -1, -1, 2);
            Check(g2.Contains("[amu][ara]amix=inputs=2") && g2.Contains("loudnorm=I=-20") && g2.Contains("[abg][asc]sidechaincompress"), "music + radio bed, radio loudnorm, ducked together");
        }
        var rargs = RadioRelay.RelayArgs("https://r/listen/x/mp3", FfmpegCaps.All, false);
        int ii = rargs.IndexOf("-i");
        Check(rargs.IndexOf("-reconnect") < ii && rargs.IndexOf("-reconnect_streamed") < ii && rargs[rargs.IndexOf("-reconnect_delay_max") + 1] == "5" && rargs.Contains("-rw_timeout"), "relay: http reconnect options before -i");
        var noRc = RadioRelay.RelayArgs("https://r/x", new FfmpegCaps { HttpReconnect = false, HttpReconnectDelayMax = false, RwTimeout = false }, false);
        Check(!noRc.Contains("-reconnect") && !noRc.Contains("-rw_timeout") && !noRc.Contains("-thread_queue_size"), "relay: options gated by caps");

        // ---- beat pulse ----
        var sc = new BallPitScene(new BallPitSettings(), 3); for (int i = 0; i < 8; i++) sc.Join("u" + i, announce: false);
        for (int i = 0; i < 400; i++) sc.Step(1 / 60.0);
        sc.BeatStrength = 1;
        for (int i = 0; i < 200; i++) sc.AudioLevel(0.05f); for (int i = 0; i < 60; i++) sc.Step(1 / 60.0);
        int b0 = sc.Beats; for (int i = 0; i < 5; i++) sc.AudioLevel(0.6f); sc.Step(1 / 60.0);
        Check(sc.Beats == b0 + 1 && sc.Pulse > 0.3, $"beat detected on a loudness jump (pulse {sc.Pulse:0.00})");
        Check(sc.Balls.Any(b => b.Vel.Y > 1), "balls on the floor kicked upward");
        for (int i = 0; i < 30; i++) sc.Step(1 / 60.0); Check(sc.Pulse == 0, "pulse decays");
        sc.BeatStrength = 0; sc.Step(1); for (int i = 0; i < 200; i++) sc.AudioLevel(0.01f); for (int i = 0; i < 5; i++) sc.AudioLevel(0.9f); int b1 = sc.Beats; sc.Step(1 / 60.0);
        Check(sc.Beats == b1, "no beats when the toggle is off");

        // ---- mock station: requests + API key ----
        using var ms = new MockStation();
        var rs = new RadioSettings { Enabled = true, Url = ms.Base + "/public/test/playlist.pls", Requests = true };
        Check(RadioHost.TargetFor(rs) == new AzuraTarget(ms.Base, "test"), "mock station detected");
        var urls = RadioPlaylist.ResolveAsync(RadioHost.Http, rs.Url, false, default).Result;
        Check(urls.Count == 2 && urls[0].Url.EndsWith("/radio.mp3") && urls[1].IsHls, "resolve over HTTP: MP3 first even when listed second");
        var rh = new RadioHost(rs, "/tmp/epiktest/art"); rh.Apply(rs);
        for (int i = 0; i < 50 && rh.Current == null; i++) Thread.Sleep(100);
        Check(rh.Current?.Station == "7LFM", "poller fetched now-playing");
        lock (ms.Log) Check(ms.ApiKeys.All(k => k == null), "no X-API-Key header without a key");
        var t0 = DateTime.UtcNow;
        var r1 = rh.Requests.Handle("amy", "!request daft one more", 300, 0, t0, default).Result;
        Check(r1 == "🎵 Requested on the radio for amy: Daft Punk – One More Time" && ms.Submits == 1, $"!request → search + POST submit ({r1})");
        lock (ms.Log) Check(ms.Log.Any(l => l.StartsWith("GET /api/station/test/requests?searchPhrase=daft%20one%20more&rowCount=10")) && ms.Log.Contains("POST /api/station/1/request/r1"), "documented request endpoints used");
        Check(rh.Requests.Handle("amy", "!request moby", 300, 0, t0.AddSeconds(5), default).Result!.Contains("please wait") && ms.Submits == 1, "per-user cooldown");
        Check(rh.Requests.Handle("bob", "!request zzzz", 300, 0, t0.AddSeconds(6), default).Result!.Contains("no requestable song") && ms.Submits == 1, "no match → no submit");
        ms.RefuseMessage = "You have submitted a request too recently! Please wait before submitting another one.";
        var r4 = rh.Requests.Handle("cleo", "!sr porcelain", 300, 0, t0.AddSeconds(7), default).Result!;
        Check(r4.Contains("too recently") && ms.Submits == 2, "AzuraCast rate-limit message relayed");
        Check(!rh.Requests.Handle("cleo", "!sr porcelain", 300, 0, t0.AddSeconds(8), default).Result!.Contains("please wait"), "refused request doesn't burn the user's cooldown");
        Check(rh.Requests.Handle("dan", "!request", 300, 0, t0.AddSeconds(9), default).Result!.StartsWith("Usage"), "usage text");
        var offReq = new RadioHost(rs, "/tmp/epiktest/art"); var rsOff = new RadioSettings { Enabled = true, Url = rs.Url, Requests = false }; offReq.Apply(rsOff);
        Check(offReq.Requests.Handle("x", "!request moby", 0, 0, t0, default).Result == null, "requests toggle off → not handled");
        offReq.Dispose();
        rh.Dispose();
        // API key header only when set (and never in the profile JSON in plain text)
        if (!OperatingSystem.IsWindows() && Secret.PlatformProtect == null)
        {   // stand-in for libsecret in the test process (reversible, NOT real encryption)
            Secret.PlatformProtect = v => "t:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(v).Reverse().ToArray());
            Secret.PlatformUnprotect = v => v.StartsWith("t:") ? Encoding.UTF8.GetString(Convert.FromBase64String(v[2..]).Reverse().ToArray()) : "";
        }
        var keyed = new RadioSettings { Enabled = true, Url = rs.Url, ProtectedApiKey = Secret.Protect("unit-test-not-a-real-key") };
        var json = System.Text.Json.JsonSerializer.Serialize(keyed);
        Check(!json.Contains("unit-test-not-a-real-key"), "API key stored encrypted");
        var kh = new RadioHost(keyed, "/tmp/epiktest/art"); kh.Apply(keyed);
        for (int i = 0; i < 50; i++) { lock (ms.Log) if (ms.ApiKeys.Contains("unit-test-not-a-real-key")) break; Thread.Sleep(100); }
        lock (ms.Log) Check(ms.ApiKeys.Contains("unit-test-not-a-real-key"), "X-API-Key sent when set");
        kh.Dispose();
        var card = new RadioHost(new RadioSettings { Enabled = true, Url = "x", ShowListeners = true }, "/tmp/epiktest/art");
        Check(card.CardInfo() == null && card.PitCard() == null, "no now-playing → no radio card");

        Console.WriteLine(fails == 0 ? "RADIO UNIT PASSED" : $"{fails} FAILED");
        return fails;
    }

    static string Run(string exe, params string[] a)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var x in a) psi.ArgumentList.Add(x);
        using var pr = Process.Start(psi)!; var o = pr.StandardOutput.ReadToEndAsync(); var e = pr.StandardError.ReadToEnd(); pr.WaitForExit(); return (o.Result + e).Trim();
    }
    /// <summary>Per-second RMS levels (dB) of the audio of a file.</summary>
    public static List<double> RmsPerSecond(string ff, string file)
    {
        var o = Run(ff, "-hide_banner", "-nostats", "-i", file, "-vn", "-af", "aresample=44100,asetnsamples=n=44100:p=0,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level:file=-", "-f", "null", "-");
        return Regex.Matches(o, @"RMS_level=(-?[\d.]+|-inf)").Select(m => m.Groups[1].Value == "-inf" ? -120 : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>Ball Pit + radio encode. url = real station (.pls) or the mock. drop: "", "block" (mock refuses + aborts) or "kill" (kill the relay).</summary>
    public static int Encode(string ff, string dir, string outMp4, string url, double seconds, string drop, double dropAt, double dropFor)
    {
        fails = 0;
        MockStation? ms = null;
        if (url == "mock")
        {
            ms = new MockStation();
            var mp3 = Path.Combine(dir, "radio-tone.mp3");
            if (!File.Exists(mp3)) Run(ff, "-hide_banner", "-y", "-f", "lavfi", "-i", "sine=f=330:d=30,volume=0.6", "-f", "lavfi", "-i", "sine=f=550:d=30,volume=0.3", "-filter_complex", "amix=inputs=2,aformat=channel_layouts=stereo", "-c:a", "libmp3lame", "-b:a", "128k", "-ar", "44100", mp3);
            ms.Mp3 = File.ReadAllBytes(mp3); url = ms.Base + "/public/test/playlist.pls";
        }
        var p = new Profile { Name = "Radio", Encoder = Environment.GetEnvironmentVariable("ENCODER") ?? "libx264", MaxRetries = 2, CrossfadeSeconds = 0 };
        p.BallPit.Mode = "Source"; p.BallPit.OrbitSpeed = 6; p.BallPit.ShowCount = true;
        p.Radio = new RadioSettings { Enabled = true, Url = url, Volume = 0.9, BeatPulse = true, ShowListeners = true };
        p.Audio.Normalize = "Off";
        var rh = new RadioHost(p.Radio, Path.Combine(dir, "art")); rh.Apply(p.Radio);
        var urls = rh.ResolveAsync(default).Result;
        Console.WriteLine("  streams: " + string.Join(" , ", urls));
        var flv = Path.Combine(dir, "radio.flv"); try { File.Delete(flv); File.Delete(outMp4); } catch { }
        var sc = BallPitTests.DemoScene(30, 7, p.BallPit); sc.BeatStrength = 0.6f;
        var eng = new StreamEngine(p, ff, new[] { new CommandBuilder.Destination("File", flv) }, Array.Empty<string>(), Path.Combine(dir, "work-radio"))
        { BallPit = sc, RadioUrls = urls, RadioLevel = sc.AudioLevel, RadioInfo = rh.CardInfo };
        var log = new StringBuilder(); eng.Log += l => { lock (log) log.AppendLine(l); };
        StreamStats? last = null; eng.Stats += st => last = st;
        var cardTimer = new System.Threading.Timer(_ => sc.RadioCard = rh.PitCard(), null, 0, 500);
        var sw = Stopwatch.StartNew();
        eng.Start(); var relay = eng.Radio;
        void At(double t, Action a) { while (sw.Elapsed.TotalSeconds < t) Thread.Sleep(20); a(); }
        double t0 = 0; List<(double t, double outT)> marks = new();
        if (drop.Length > 0)
        {
            At(dropAt, () => { t0 = sw.Elapsed.TotalSeconds; if (drop == "block") ms!.Block(true); else eng.Radio?.KillCurrent(); Console.WriteLine($"  >> drop ({drop}) at {t0:0.0} s, relay state: {eng.Radio?.State}"); });
            if (drop == "block") At(dropAt + dropFor, () => { ms!.Block(false); Console.WriteLine($"  >> station back at {sw.Elapsed.TotalSeconds:0.0} s"); });
        }
        At(seconds + 1.5, () => eng.Stop());
        cardTimer.Dispose();
        Check(eng.Wait(20000), "engine stopped");
        var lg = log.ToString();
        Console.WriteLine($"  encoder fps {last?.Fps:0.0}, speed {last?.Speed:0.00}x; radio: reconnects {relay?.Reconnects}, in {relay?.BytesIn / 1024} KiB");
        foreach (var l in lg.Split('\n').Where(l => l.Contains("📻") || l.Contains("[radio]")).Take(20)) Console.WriteLine("  log: " + l.Trim());
        if (fails > 0 || !File.Exists(flv) || Environment.GetEnvironmentVariable("BPLOG") != null) Console.WriteLine(lg[^Math.Min(lg.Length, 5000)..]);
        Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-i", flv, "-c", "copy", "-movflags", "+faststart", outMp4);
        var probe = Run(Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe"), "-v", "error", "-show_entries", "stream=codec_type,codec_name,sample_rate,width,height:format=duration", "-of", "compact", outMp4);
        Console.WriteLine("  " + probe.Replace("\n", " | "));
        var dur = double.TryParse(Regex.Match(probe, @"duration=([\d.]+)").Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        Check(dur >= seconds - 6, $"~{seconds:0} s clip encoded ({dur:0.0} s; the first seconds are encoder start-up)");
        var nf = Run(Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe"), "-v", "error", "-count_frames", "-select_streams", "v", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", outMp4);
        Check(int.TryParse(nf.Trim(), out var frames) && Math.Abs(frames - dur * 30) < 30, $"video continuous: {frames} frames ≈ {dur:0.0} s × 30 fps");
        Check(probe.Contains("codec_type=audio") && probe.Contains("codec_type=video"), "video + audio streams present");
        Check(last != null && last.Speed > 0.9, $"encoder kept real-time (speed {last?.Speed:0.00}x)");
        Check(!lg.Contains("Could not connect the radio feed") && !lg.Contains("Encoder exited"), "radio feed connected, encoder never restarted");
        var rms = RmsPerSecond(ff, outMp4);
        Console.WriteLine("  RMS dB per s: " + string.Join(" ", rms.Select(x => x.ToString("0", CultureInfo.InvariantCulture))));
        int loud = rms.Count(x => x > -50);
        if (drop.Length == 0) Check(loud >= rms.Count * 0.7, $"radio audio is not silent ({loud}/{rms.Count} s above -50 dB)");
        else
        {
            // timeline in the output ≈ wall clock since the first frame; locate the gap and the recovery
            int firstLoud = rms.FindIndex(x => x > -50), gapStart = rms.FindIndex(Math.Max(0, firstLoud), x => x <= -60);
            int recover = gapStart >= 0 ? rms.FindIndex(gapStart, x => x > -50) : -1;
            Console.WriteLine($"  audio: first sound {firstLoud} s, gap from {gapStart} s, back at {recover} s");
            Check(firstLoud >= 0 && firstLoud < dropAt, "radio audible before the drop");
            if (drop == "block") Check(gapStart > 0 && recover > gapStart && rms.Skip(recover).Take(3).All(x => x > -50), "silence during the outage, audio recovers after it");
            else Check(recover < 0 || recover - gapStart <= 6, $"audio back within a few seconds after the relay was killed (gap {(recover < 0 ? 0 : recover - gapStart)} s)");
            Check((eng.Radio?.Reconnects ?? 1) >= 0 && lg.Contains("Radio reconnected"), "relay reconnected in the background");
        }
        foreach (var at in new[] { 3.0, Math.Floor(dur - 2) })
        {
            var png = Path.ChangeExtension(outMp4, null) + $"-t{at:0}.png";
            Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-ss", at.ToString(CultureInfo.InvariantCulture), "-i", outMp4, "-frames:v", "1", png);
            Check(File.Exists(png), "frame: " + png);
        }
        rh.Dispose(); ms?.Dispose();
        Console.WriteLine(fails == 0 ? "RADIO ENCODE PASSED" : $"{fails} FAILED");
        return fails;
    }

    /// <summary>Real station (read-only): resolve the .pls, decode check, now-playing, render the now-playing card PNG with the real song.</summary>
    public static int Live(string ff, string dir, string url, string outDir)
    {
        fails = 0;
        var s = new RadioSettings { Enabled = true, Url = url, ShowListeners = true };
        var rh = new RadioHost(s, Path.Combine(dir, "art")); rh.Apply(s);
        var test = rh.TestAsync(ff, default).Result; Console.WriteLine("  " + test.Replace("\n", "\n  "));
        Check(test.Contains("Playlist OK") && Regex.IsMatch(test, @"▶ .*: ok, "), "Test: playlist resolved and the first stream decodes");
        for (int i = 0; i < 100 && rh.Current == null; i++) Thread.Sleep(100);
        var np = rh.Current; Check(np != null, $"now playing: {np?.Display} (online {np?.Online}, next {np?.Next}, listeners {np?.Listeners})");
        for (int i = 0; i < 50 && rh.Poller?.ArtFile is not { Length: > 0 } && np?.Song.Art.Length > 0; i++) Thread.Sleep(100);
        var info = rh.CardInfo(); Check(info != null, "card info");
        var work = Path.Combine(dir, "work-card"); Directory.CreateDirectory(work);
        File.Copy("/tmp/zt/font.ttf", Path.Combine(work, CommandBuilder.FontName), true);
        var p = new Profile(); p.NowPlaying.ShowThumbnail = true; p.NowPlaying.ShowProgress = true;
        Check(NowPlayingCard.Render(ff, work, p, info), "card rendered: " + NowPlayingCard.LastError);
        var outPng = Path.Combine(outDir, "radio-nowplaying-card.png"); File.Copy(Path.Combine(work, NowPlayingCard.CardFile), outPng, true);
        Console.WriteLine("  card: " + outPng + " (art " + (rh.Poller?.ArtFile ?? "") + ")");
        // the in-scene Ball Pit radio card
        var sc = BallPitTests.DemoScene(25, 5, new BallPitSettings()); sc.RadioCard = rh.PitCard(); sc.BeatStrength = 1;
        for (int i = 0; i < 240; i++) sc.Step(1 / 60.0);
        using var r = new BallPitRenderer(1280, 720); r.Render(sc); var pit = Path.Combine(outDir, "ballpit-radio-card.png"); r.SavePng(pit);
        Check(File.Exists(pit), "Ball Pit radio card: " + pit);
        rh.Dispose();
        Console.WriteLine(fails == 0 ? "RADIO LIVE PASSED" : $"{fails} FAILED");
        return fails;
    }
}
