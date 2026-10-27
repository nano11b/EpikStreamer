using System.Diagnostics;
using System.Numerics;
using EpikStreamer.Core;
using SkiaSharp;

/// <summary>v2.5 Ball Pit: physics/roster/commands unit tests, renderer fps, and end-to-end ffmpeg encodes.</summary>
static class BallPitTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    /// <summary>Synthetic avatar PNG (no real people): coloured gradient + smiley.</summary>
    public static byte[] FakeAvatar(int i)
    {
        using var bmp = new SKBitmap(96, 96); using var c = new SKCanvas(bmp);
        var h = (i * 47) % 360;
        using var p = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new(0, 0), new(96, 96), new[] { SKColor.FromHsl(h, 70, 60), SKColor.FromHsl((h + 60) % 360, 70, 40) }, SKShaderTileMode.Clamp) };
        c.DrawRect(0, 0, 96, 96, p); p.Shader = null;
        p.Color = SKColors.White; c.DrawCircle(48, 48, 30, p);
        p.Color = SKColors.Black; c.DrawCircle(38, 40, 5, p); c.DrawCircle(58, 40, 5, p);
        p.Style = SKPaintStyle.Stroke; p.StrokeWidth = 4; using var m = new SKPath(); m.MoveTo(34, 56); m.QuadTo(48, 70, 62, 56); c.DrawPath(m, p);
        using var img = SKImage.FromBitmap(bmp); return img.Encode(SKEncodedImageFormat.Png, 90).ToArray();
    }

    public static int Unit()
    {
        fails = 0;
        var s = new BallPitSettings { Bounciness = 0.8, Friction = 0.8 };
        // drop + rebound height ≈ e² h
        var sc = new BallPitScene(s, 1); sc.Join("drop", announce: false); var b = sc.Balls[0];
        b.Pos = new Vector3(0, 6, 0); b.Vel = Vector3.Zero; float r = b.Radius; double h0 = 6 - r;
        double minY = 99, peak = 0; bool fell = false;
        for (int i = 0; i < 600; i++)
        {
            sc.Step(1 / 120.0);
            if (!fell) { if (b.Pos.Y < minY) minY = b.Pos.Y; if (b.Vel.Y > 0) fell = true; }
            else { peak = Math.Max(peak, b.Pos.Y - r); if (b.Vel.Y < 0 && peak > 0) break; }
        }
        Check(minY <= r + 0.05, $"ball reaches the floor (min y {minY:0.000}, r {r:0.000})");
        Check(Math.Abs(peak / h0 - 0.64) < 0.12, $"first rebound ≈ e²·h (e=0.8: {peak / h0:0.00}, expect ≈0.64)");
        for (int i = 0; i < 1200; i++) sc.Step(1 / 60.0);
        Check(Math.Abs(b.Pos.Y - r) < 0.02 && b.Vel.Length() < 0.05, $"ball comes to rest on the floor (y-r {b.Pos.Y - r:0.0000}, |v| {b.Vel.Length():0.0000})");

        // rolling rotates the texture
        var ax0 = b.AX; b.Vel = new Vector3(3, 0, 0); for (int i = 0; i < 30; i++) sc.Step(1 / 60.0);
        Check(Vector3.Dot(ax0, b.AX) < 0.95, "a rolling ball rotates (avatar rolls with it)");

        // crowd: 200 balls stay inside, don't sink into each other, energy dissipates
        var s2 = new BallPitSettings { Bounciness = 0.6 }; var c2 = new BallPitScene(s2, 2);
        for (int i = 0; i < 230; i++) c2.Join("u" + i, announce: false);
        Check(c2.Count == 200 && c2.Waiting == 30, $"cap 200 balls, extra people wait ({c2.Count} + {c2.Waiting} waiting)");
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 600; i++) c2.Step(1 / 30.0);
        double perStep = sw.Elapsed.TotalMilliseconds / 600;
        c2.Command("host", "!shake", "", true);
        for (int i = 0; i < 300; i++) c2.Step(1 / 30.0);
        float maxPen = 0; bool inside = true;
        var bs = c2.Balls;
        foreach (var x in bs)
        {
            float rr = x.Radius;
            inside &= x.Pos.X >= -BallPitScene.RoomX + rr - 1e-3 && x.Pos.X <= BallPitScene.RoomX - rr + 1e-3 && x.Pos.Z >= -BallPitScene.RoomZ + rr - 1e-3 && x.Pos.Z <= BallPitScene.RoomZ - rr + 1e-3 && x.Pos.Y >= rr - 1e-3 && x.Pos.Y < BallPitScene.Ceiling;
            foreach (var y in bs) if (x != y) maxPen = Math.Max(maxPen, (x.Radius + y.Radius - Vector3.Distance(x.Pos, y.Pos)) / x.Radius);
        }
        Check(inside, "200 balls (after a !shake) all stay inside the room");
        Check(maxPen < 0.3, $"no deep overlaps (max penetration {maxPen:0.00} r)");
        Check(bs.All(x => float.IsFinite(x.Pos.X + x.Pos.Y + x.Pos.Z)), "no NaNs");
        double e1 = c2.Energy(); for (int i = 0; i < 300; i++) c2.Step(1 / 30.0); double e2 = c2.Energy();
        Check(e2 <= e1 + 1, $"energy settles (no explosion): {e1:0} → {e2:0}");
        Check(perStep < 8, $"physics step with 200 balls: {perStep:0.00} ms (frame budget 33 ms)");
        Check(c2.Balls[0].Radius < (float)s2.BallSize, $"crowd scaling shrinks balls (r={c2.Balls[0].Radius:0.00} vs {s2.BallSize})");

        // leave → pop + promote waiting
        for (int i = 0; i < 5; i++) c2.Leave("u" + i);
        c2.Step(0.6);
        Check(c2.Count == 200 && c2.Waiting == 25 && !c2.Has("u0") && c2.Has("u205"), $"leavers pop out and waiting people drop in ({c2.Count} + {c2.Waiting})");

        // commands
        var c3 = new BallPitScene(new BallPitSettings(), 3); c3.Join("amy", announce: false); c3.Join("bob", announce: false);
        for (int i = 0; i < 120; i++) c3.Step(1 / 60.0);
        var amy = c3.Balls.First(x => x.Name == "amy");
        Check(c3.Command("amy", "!bounce", "", false) != null && amy.Vel.Y > 5, "!bounce jumps your own ball");
        Check(c3.Command("amy", "!color", "#ff8800", false) != null && amy.Tint == 0xFF8800 && amy.LabelColor == 0xFF8800, "!color #ff8800 tints the ball + label");
        Check(c3.Command("amy", "!color", "nope", false) == null, "!color with a bad value is ignored");
        Check(c3.Command("bob", "!bigball", "", false) != null && c3.Balls.First(x => x.Name == "bob").Radius > amy.Radius * 1.5, "!bigball makes the ball big");
        Check(c3.Command("bob", "!shake", "", false) != null, "!shake (viewer, cooldown free)");
        Check(c3.Command("amy", "!shake", "", false) == null, "!shake again within the cooldown is ignored");
        Check(c3.Command("host", "!shake", "", true) != null, "host can always !shake");
        c3.Settings.ShakeHostOnly = true; c3.Step(40);
        Check(c3.Command("bob", "!shake", "", false) == null, "host-only !shake ignores viewers");
        c3.Settings.Commands = false; Check(c3.Command("amy", "!bounce", "", false) == null, "commands off → ignored");
        c3.Chat("amy", "hello   there,   this is a fairly long chat message that should be truncated");
        Check(amy.Bubble != null && amy.Bubble.Length <= 41 && amy.Bubble.StartsWith("hello there,"), "chat → speech bubble (whitespace collapsed, truncated): " + amy.Bubble);
        c3.Chat("newbie", "hi"); Check(c3.Has("newbie"), "a chatter who isn't in the list gets a ball");
        Check(BallPitScene.ParseColor("rgb(255, 0, 16)") == 0xFF0010 && BallPitScene.ParseColor("#abc") == 0xAABBCC, "color parsing (rgb(), #rgb)");

        // ChatCommands routes Ball Pit commands (cooldowns, self allowed, no chat reply)
        var cs = new ChatSettings { CommandsEnabled = true, CooldownSeconds = 10, MyUsername = "me" };
        var cc = new ChatCommands(cs); var got = new List<string>();
        cc.BallPit = (u, cmd, arg, host) => { got.Add($"{u} {cmd} {arg} {host}"); return true; };
        var t0 = new DateTime(2026, 10, 26, 12, 0, 0);
        Check(cc.Handle(new("amy", "!bounce", "1"), new(), t0) == null && got.Count == 1, "!bounce routed to the ball pit, no chat reply");
        cc.Handle(new("amy", "!bounce", "2"), new(), t0.AddSeconds(3));
        Check(got.Count == 1, "per-user cooldown respected");
        cc.Handle(new("bob", "!bounce", "3"), new(), t0.AddSeconds(3));
        Check(got.Count == 2, "other users are not blocked by amy's cooldown");
        cc.Handle(new("me", "!shake", "4", Self: true), new(), t0.AddSeconds(4));
        Check(got.Count == 3 && got[2].EndsWith("True"), "the streamer's own !shake works and counts as host");
        cc.Handle(new("x", "!color #00ff00", "5"), new(), t0.AddSeconds(5));
        Check(got.Count == 4 && got[3] == "x !color #00ff00 False", "!color argument passed through");
        Check(cc.Triggers.Contains("!bounce"), "!help lists Ball Pit commands when enabled");
        cs.CommandsEnabled = false; cc.Handle(new("zed", "!bounce", "6"), new(), t0.AddSeconds(60));
        Check(got.Count == 4, "chat commands off → Ball Pit commands off too");

        // roster: virtualized member scans, viewers, chatters
        var rs = new BallPitSettings { ChatterMinutes = 10 }; var ro = new BallPitRoster(rs);
        var joins = new List<string>(); var leaves = new List<string>();
        ro.OnJoin = m => joins.Add(m.Name + (m.Avatar != null ? "+av" : "")); ro.OnLeave = n => leaves.Add(n);
        var now = t0;
        BallPitRoster.Member M(string n, string? av = null) => new(n, av);
        ro.Members(new[] { M("Amy", "https://x/a.png"), M("Bob") }, false, now);      // first screen (incomplete)
        ro.Members(new[] { M("Cat"), M("Dan") }, false, now);                        // scrolled further
        Check(joins.SequenceEqual(new[] { "Amy+av", "Bob", "Cat", "Dan" }), "incremental (virtualized) reads merge by name: " + string.Join(",", joins));
        ro.Members(new[] { M("amy"), M("Bob") }, false, now);
        Check(joins.Count == 4 && leaves.Count == 0, "case-insensitive merge, incomplete scans never remove");
        ro.Members(new[] { M("Amy"), M("Bob"), M("Cat") }, true, now);
        Check(leaves.Count == 0, "missing from one complete scan → not yet gone");
        ro.Members(new[] { M("Amy"), M("Bob"), M("Cat") }, true, now);
        Check(leaves.SequenceEqual(new[] { "Dan" }), "missing from two complete scans → leaves");
        ro.Members(new[] { M("Bob", "https://x/b.png") }, false, now);
        Check(joins.Last() == "Bob+av", "avatar learned later updates the ball");
        ro.Viewers(new[] { "Vic" }, now); Check(joins.Last() == "Vic", "LIVE viewers included");
        ro.Viewers(Array.Empty<string>(), now); Check(leaves.Last() == "Vic", "viewer gone → leaves");
        ro.Chatter("Tia", now); Check(joins.Last() == "Tia", "chat author included");
        ro.Tick(now.AddMinutes(5)); Check(!leaves.Contains("Tia"), "chatter stays while recently active");
        ro.Tick(now.AddMinutes(11)); Check(leaves.Last() == "Tia", "chatter leaves after ChatterMinutes of silence");
        ro.Chatter("Amy", now); ro.Tick(now.AddMinutes(30)); Check(!leaves.Contains("Amy"), "a member who chatted doesn't leave on chat silence");

        // textures
        var tex = BallPitTextures.FromEncoded(FakeAvatar(1));
        Check(tex != null && tex.Length == 64 * 64, "avatar PNG decoded to a 64×64 texture");
        Check(BallPitTextures.FromEncoded(new byte[] { 1, 2, 3 }) == null, "garbage avatar → null (initials used)");
        var cache = new AvatarCache(Path.Combine(Path.GetTempPath(), "epik-av-test"));
        var tcs = new TaskCompletionSource<uint[]>(); cache.Ready += (_, t) => tcs.TrySetResult(t);
        Check(cache.Get("data:image/png;base64," + Convert.ToBase64String(FakeAvatar(2))) == null && tcs.Task.Wait(5000), "data: URL avatar loads via the cache");
        var pf = new TaskCompletionSource<uint[]>(); var c4 = new AvatarCache(Path.Combine(Path.GetTempPath(), "epik-av-test2")) { PageFetch = _ => Task.FromResult<byte[]?>(FakeAvatar(3)) };
        c4.Ready += (_, t) => pf.TrySetResult(t); c4.Request("https://127.0.0.1:9/nope.png");
        Check(pf.Task.Wait(15000), "HTTP failure → falls back to the page (WebView) fetch");

        // ffmpeg args
        var p = new Profile(); p.BallPit.Mode = "Overlay";
        var lo = new CommandBuilder.LiveOptions(BallPitPort: 5555, BallPitMode: "Overlay", BallPitW: 640, BallPitH: 360);
        var a = string.Join(" ", CommandBuilder.EncoderArgs(p, new[] { new CommandBuilder.Destination("x", "rtmp://h/x/k") }, false, lo));
        Check(a.Contains("-f rawvideo -pix_fmt bgra -video_size 640x360 -framerate 30 -i tcp://127.0.0.1:5555?listen=1") && a.Contains("[1:v]setpts=PTS-STARTPTS,scale=1280:720") && a.Contains("unpremultiply=inplace=1[pit]") && a.Contains("overlay@pit=x=0:y=0:eof_action=pass"), "overlay mode: raw BGRA input, scaled, un-premultiplied, overlaid");
        lo = lo with { BallPitMode = "Source", BallPitW = 1280, BallPitH = 720 };
        a = string.Join(" ", CommandBuilder.EncoderArgs(p, new[] { new CommandBuilder.Destination("x", "rtmp://h/x/k") }, false, lo));
        Check(a.Contains("[0:v]nullsink;[1:v]setpts=PTS-STARTPTS,fps=30,format=yuv420p,setsar=1[v0]") && !a.Contains("scale=1280:720:flags"), "source mode: ball pit replaces the video, playlist keeps the audio");
        Console.WriteLine(fails == 0 ? "BALL PIT UNIT TESTS PASSED" : $"{fails} FAILED");
        return fails;
    }

    public static BallPitScene DemoScene(int n, int seed, BallPitSettings? s = null)
    {
        var sc = new BallPitScene(s ?? new BallPitSettings(), seed);
        var texs = Enumerable.Range(0, 12).Select(i => BallPitTextures.FromEncoded(FakeAvatar(i))!).ToArray();
        sc.TextureFor = url => texs[int.Parse(url.Split('/').Last()) % texs.Length];
        string[] names = { "Amy", "Bobcat", "Cleo", "DJ_Dan", "Echo", "Fizz", "GigaGabe", "Hana", "Ivo", "Jules", "Kai", "Luna", "Milo", "Nova", "Ozzy", "Pip" };
        string[] cols = { "#F87171", "#FBBF24", "#34D399", "#60A5FA", "#A78BFA", "#F472B6", "", "" };
        for (int i = 0; i < n; i++)
            sc.Join(i < names.Length ? names[i] : $"{names[i % names.Length]}{i}", i % 3 == 2 ? null : "fake://avatar/" + i, cols[i % cols.Length], announce: false);
        return sc;
    }

    /// <summary>Renderer throughput (step + render) at the given size.</summary>
    public static double Fps(int w, int h, int balls, int frames, string? png = null)
    {
        var sc = DemoScene(balls, 7);
        for (int i = 0; i < 90; i++) sc.Step(1 / 30.0);   // let them fall in
        using var r = new BallPitRenderer(w, h);
        for (int i = 0; i < 10; i++) { sc.Step(1 / 30.0); r.Render(sc); }   // warm-up (JIT)
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++) { sc.Step(1 / 30.0); r.Render(sc); }
        var fps = frames / sw.Elapsed.TotalSeconds;
        if (png != null) r.SavePng(png);
        return fps;
    }

    public static int Bench(string outDir)
    {
        fails = 0; Directory.CreateDirectory(outDir);
        Console.WriteLine($"CPU: {Environment.ProcessorCount} logical cores");
        foreach (var (w, h, n) in new[] { (1280, 720, 50), (1280, 720, 200), (1280, 720, 300), (960, 540, 200), (1920, 1080, 200) })
        {
            var fps = Fps(w, h, n, 150);
            Console.WriteLine($"  render+physics {w}x{h}, {n} balls: {fps:0.0} fps ({1000 / fps:0.0} ms/frame)");
            if (w == 1280 && n <= 200) Check(fps >= 30, $"≥30 fps at 720p with {n} balls ({fps:0.0})");
        }
        // single-core figure (a busy PC): one band only
        // still frames
        var sc = DemoScene(60, 11, new BallPitSettings { CameraYaw = -25, Orbit = false });
        for (int i = 0; i < 75; i++) sc.Step(1 / 30.0);
        sc.Chat("Amy", "hi everyone!"); sc.Join("NewPerson", "fake://avatar/5", "#FDE047"); sc.Command("Cleo", "!bigball", "", false);
        for (int i = 0; i < 9; i++) sc.Step(1 / 30.0);
        using (var r = new BallPitRenderer(1280, 720)) { r.Render(sc); r.SavePng(Path.Combine(outDir, "ballpit-frame1.png")); }
        sc.Leave("Bobcat"); sc.Command("host", "!shake", "", true); for (int i = 0; i < 8; i++) sc.Step(1 / 30.0);
        using (var r = new BallPitRenderer(1280, 720)) { r.Render(sc); r.SavePng(Path.Combine(outDir, "ballpit-frame2.png")); }
        using (var r = new BallPitRenderer(1280, 720) { Transparent = true }) { r.Render(sc); r.SavePng(Path.Combine(outDir, "ballpit-overlay-alpha.png")); }
        Check(File.Exists(Path.Combine(outDir, "ballpit-frame1.png")), "PNG frames written to " + outDir);
        Console.WriteLine(fails == 0 ? "BALL PIT BENCH PASSED" : $"{fails} FAILED");
        return fails;
    }

    /// <summary>End-to-end: the real StreamEngine streams a Ball Pit (source mode, empty playlist, background music) to a local FLV
    /// + recording, while people join/chat/leave; then an overlay-mode run on top of the playlist.</summary>
    public static int Encode(string ff, string dir, string outMp4, string mode)
    {
        fails = 0;
        var p = new Profile { Name = "BallPit", Encoder = Environment.GetEnvironmentVariable("ENCODER") ?? "libx264", MaxRetries = 2, CrossfadeSeconds = 0 };
        p.BallPit.Mode = mode; p.BallPit.OrbitSpeed = 6;
        if (Environment.GetEnvironmentVariable("BPPLAY") is { } bpp) p.Playlist.Add(bpp);
        if (mode == "Overlay") { p.Playlist.Add("/workspace/epikchat/sample.mp4"); p.LoopPlaylist = true; p.BallPit.ShowCount = true; }
        else if (File.Exists("/tmp/zt/music1.mp3")) { p.Audio.MusicEnabled = true; p.Audio.MusicPlaylist.Add("/tmp/zt/music1.mp3"); p.Audio.MusicVolume = 0.5; }
        var flv = Path.Combine(dir, "ballpit-" + mode + ".flv"); File.Delete(flv);
        var sc = DemoScene(40, 21, p.BallPit);
        try { File.Delete(flv); File.Delete(outMp4); } catch { }
        var eng = new StreamEngine(p, ff, new[] { new CommandBuilder.Destination("File", flv) }, Array.Empty<string>(), Path.Combine(dir, "work-bp")) { BallPit = sc };
        var log = new System.Text.StringBuilder(); eng.Log += l => { lock (log) log.AppendLine(l); };
        StreamStats? last = null; eng.Stats += st => last = st;
        var sw = Stopwatch.StartNew();
        eng.Start();
        void At(double t, Action a) { while (sw.Elapsed.TotalSeconds < t) Thread.Sleep(20); a(); }
        At(2.5, () => sc.Join("Newcomer", "fake://avatar/3", "#FDE047"));
        At(4.0, () => { sc.Chat("Amy", "hello ball pit!"); sc.Command("Cleo", "!bigball", "", false); });
        At(6.0, () => sc.Command("host", "!shake", "", true));
        At(7.5, () => { sc.Leave("Bobcat"); sc.Chat("Newcomer", "!bounce"); sc.Command("Newcomer", "!bounce", "", false); });
        At(11.0, () => eng.Stop());
        Check(eng.Wait(20000), "engine stopped");
        var lg = log.ToString();
        Console.WriteLine($"  encoder fps {last?.Fps:0.0}, speed {last?.Speed:0.00}x, dup {last?.Duplicated}, drop {last?.Dropped}; feed: {eng.PitFeed?.Frames} frames, {eng.PitFeed?.Repeats} repeats, render {eng.PitFeed?.RenderFps:0} fps");
        if (fails > 0 || !File.Exists(flv) || Environment.GetEnvironmentVariable("BPLOG") != null) Console.WriteLine(lg[^Math.Min(lg.Length, 6000)..]);
        var rem = Process.Start(new ProcessStartInfo(ff, new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", flv, "-c", "copy", "-movflags", "+faststart", outMp4 }) { RedirectStandardError = true })!;
        rem.WaitForExit();
        var probe = Run(Path.Combine(Path.GetDirectoryName(ff)!, "ffprobe"), "-v", "error", "-show_entries", "stream=codec_type,width,height,avg_frame_rate:format=duration", "-of", "compact", outMp4);
        Console.WriteLine("  " + probe.Replace("\n", " | "));
        var dur = double.TryParse(System.Text.RegularExpressions.Regex.Match(probe, @"duration=([\d.]+)").Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        Check(dur >= 8.5, $"{mode}: ~10 s clip encoded ({dur:0.0} s)");
        Check(probe.Contains("width=1280|height=720") && Fr(probe) is > 29.5 and < 30.5 && probe.Contains("codec_type=audio"), $"{mode}: 1280x720 @ 30 fps with audio");
        Check(!lg.Contains("Could not connect the Ball Pit feed"), $"{mode}: feed connected");
        Check((eng.PitFeed?.Frames ?? 0) >= 9 * 30, $"{mode}: feed delivered real-time frames ({eng.PitFeed?.Frames})");
        Check(last != null && last.Speed > 0.9, $"{mode}: encoder kept real-time (speed {last?.Speed:0.00}x)");
        var png = Path.ChangeExtension(outMp4, null) + "-t8.png";
        Run(ff, "-hide_banner", "-loglevel", "error", "-y", "-ss", "8", "-i", outMp4, "-frames:v", "1", png);
        Check(File.Exists(png), $"{mode}: frame extracted: {png}");
        Console.WriteLine(fails == 0 ? $"BALL PIT ENCODE ({mode}) PASSED" : $"{fails} FAILED");
        return fails;
    }
    static double Fr(string probe)
    {
        var m = System.Text.RegularExpressions.Regex.Match(probe, @"codec_type=video\|[^|]*\|[^|]*\|avg_frame_rate=(\d+)/(\d+)");
        return m.Success && double.Parse(m.Groups[2].Value) > 0 ? double.Parse(m.Groups[1].Value) / double.Parse(m.Groups[2].Value) : 0;
    }
    static string Run(string exe, params string[] a)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var x in a) psi.ArgumentList.Add(x);
        using var pr = Process.Start(psi)!; var o = pr.StandardOutput.ReadToEnd() + pr.StandardError.ReadToEnd(); pr.WaitForExit(); return o.Trim();
    }
}
