using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EpikStreamer.Core;

/// <summary>2.8.3 Ball Pit limits: cap + priority, spawn rate limit and churn coalescing, avatar LRU, render-error recovery, degrade.</summary>
public static class PitLimitTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    public static int Run()
    {
        fails = 0;
        // ---- defaults / migration ----
        var d = new BallPitSettings();
        Check(d.MaxBalls == 60 && d.SpawnPerSecond == 4 && d.Quality == "Medium", $"defaults: cap {d.MaxBalls}, {d.SpawnPerSecond}/s, {d.Quality}");
        var old = new BallPitSettings { MaxBalls = 200, DefaultsVersion = 0 }; old.Migrate();
        Check(old.MaxBalls == 60, "2.8.2 default cap 200 migrates to 60");
        var custom = new BallPitSettings { MaxBalls = 120, DefaultsVersion = 0 }; custom.Migrate();
        Check(custom.MaxBalls == 120, "a user-chosen cap (120) is kept");
        var huge = new BallPitSettings { MaxBalls = 900, DefaultsVersion = 0 }; huge.Migrate();
        Check(huge.MaxBalls <= BallPitScene.HardCap, $"cap above the hard max is clamped ({huge.MaxBalls})");

        // ---- cap + priority + overflow ----
        var sc = new BallPitScene(new BallPitSettings { MaxBalls = 10 }, 1);
        for (int i = 0; i < 30; i++) sc.Join("m" + i, announce: false);
        sc.SpawnAllNow();
        Check(sc.Count == 10 && sc.Overflow == 20, $"cap 10: {sc.Count} balls, +{sc.Overflow} more");
        sc.Join("viewer1", announce: false, viewer: true);
        sc.Chat("m25", "hello");   // a queued member chats → becomes a top-priority chatter
        for (int i = 0; i < 120; i++) sc.Step(1 / 30.0);
        Check(sc.Has("m25"), "a recent chatter waiting in the queue swaps into the full pit");
        Check(sc.Has("viewer1"), "a LIVE viewer swaps in ahead of plain members");
        Check(sc.Count == 10, $"still capped at 10 ({sc.Count})");
        int before = sc.Count; for (int i = 0; i < 300; i++) sc.Step(1 / 30.0);
        Check(sc.Has("m25") && sc.Has("viewer1") && sc.Count == 10, "no swap churn among equal-priority members");

        // ---- spawn rate limit ----
        var r = new BallPitScene(new BallPitSettings { MaxBalls = 60, SpawnPerSecond = 4 }, 2);
        for (int i = 0; i < 50; i++) r.Join("b" + i, announce: true);
        r.Step(1 / 30.0); int c0 = r.Count;
        for (int i = 0; i < 30; i++) r.Step(1 / 30.0); int c1 = r.Count;
        Check(c0 <= 3, $"a +50 burst doesn't spawn at once ({c0} balls in the first frame)");
        Check(c1 >= 4 && c1 <= 8, $"about 4 spawns per second ({c1} after 1 s)");
        for (int i = 0; i < 30 * 15; i++) r.Step(1 / 30.0);
        Check(r.Count == 50 && r.Overflow == 0, $"everyone is in after ~13 s ({r.Count})");

        // ---- churn coalescing ----
        var ch = new BallPitScene(new BallPitSettings(), 3);
        ch.Join("flap", announce: false); long sp0 = ch.Spawned;
        ch.Leave("flap"); ch.Join("flap", announce: false); ch.Leave("flap"); ch.Join("flap", announce: false);
        for (int i = 0; i < 60; i++) ch.Step(1 / 30.0);
        Check(ch.Count == 1 && ch.Spawned - sp0 <= 1 && ch.Coalesced >= 2, $"join/leave/join churn coalesces (count {ch.Count}, spawned {ch.Spawned - sp0}, coalesced {ch.Coalesced})");
        ch.Join("flap2", announce: false); ch.Leave("flap2");
        for (int i = 0; i < 60; i++) ch.Step(1 / 30.0);
        Check(!ch.Has("flap2"), "someone who left before spawning never appears");

        // ---- degrade levels ----
        var q4 = BallPitQuality.For("High", 4);
        Check(!q4.Shadows && !q4.Specular && q4.LabelLimit == 0 && q4.HalfRate && q4.CapScale == 0.5, "level 4 = no shadows/highlights/labels, half rate, half the balls");
        var cs = new BallPitScene(new BallPitSettings { MaxBalls = 60 }, 4);
        for (int i = 0; i < 60; i++) cs.Join("c" + i, announce: false); cs.SpawnAllNow();
        cs.Quality = q4; for (int i = 0; i < 60; i++) cs.Step(1 / 30.0);
        Check(cs.Count == 30 && cs.Overflow == 30, $"level 4 halves the pit ({cs.Count} + {cs.Overflow})");
        cs.Quality = BallPitQuality.For("Medium", 0); for (int i = 0; i < 30 * 10; i++) cs.Step(1 / 30.0);
        Check(cs.Count == 60, $"back to full after recovering ({cs.Count})");

        // ---- avatar LRU ----
        var dir = Path.Combine(Path.GetTempPath(), "pitlru" + Environment.ProcessId);
        var cache = new AvatarCache(dir, maxBytes: 10 * BallPitScene.TexSize * BallPitScene.TexSize * 4);
        var tex = () => new uint[BallPitScene.TexSize * BallPitScene.TexSize];
        for (int i = 0; i < 10; i++) cache.Put("u" + i, tex());
        cache.Get("u0");   // touch → most recent
        cache.Put("u10", tex()); cache.Put("u11", tex());
        Check(cache.Count == 10 && cache.Bytes <= cache.MaxBytes, $"LRU stays within its byte budget ({cache.Count} items, {cache.Bytes / 1024} KB)");
        Check(cache.Contains("u0") && !cache.Contains("u1") && !cache.Contains("u2") && cache.Contains("u11"), "least recently used are evicted, recently used kept");
        // concurrency limit + scaled decode
        Environment.SetEnvironmentVariable("EPIK_TEST_ALLOW_FILE", "1");
        var big = Path.Combine(dir, "big.jpg");
        using (var bmp = new SkiaSharp.SKBitmap(2000, 2000)) { using (var cv = new SkiaSharp.SKCanvas(bmp)) cv.Clear(SkiaSharp.SKColors.OrangeRed); using var img = SkiaSharp.SKImage.FromBitmap(bmp); using var dat = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80); File.WriteAllBytes(big, dat.ToArray()); }
        var c2 = new AvatarCache(Path.Combine(dir, "c2")); int ready = 0; c2.Ready += (_, _) => Interlocked.Increment(ref ready);
        for (int i = 0; i < 40; i++) c2.Request(new Uri(big).AbsoluteUri + "?u=" + i);
        var sw = Stopwatch.StartNew(); while (ready < 40 && sw.Elapsed.TotalSeconds < 30) Thread.Sleep(20);
        Check(ready == 40, $"40 avatars (2000² JPEG) load ({ready}) in {sw.Elapsed.TotalSeconds:0.0} s");
        Check(c2.PeakInFlightDecodes <= AvatarCache.MaxConcurrent, $"at most {AvatarCache.MaxConcurrent} decodes at once (peak {c2.PeakInFlightDecodes})");
        c2.Request("file:///nonexistent/x.png"); Thread.Sleep(300); int f0 = c2.Failed; c2.Request("file:///nonexistent/x.png"); Thread.Sleep(300);
        Check(f0 == 1 && c2.Failed == 1, "a failed avatar is not retried for a while (negative cache)");

        // ---- render exception recovery: frames keep flowing ----
        var es = new BallPitScene(new BallPitSettings { Mode = "Source" }, 5);
        for (int i = 0; i < 20; i++) es.Join("e" + i, announce: false); es.SpawnAllNow();
        int boom = 0; es.Overlay = (_, _, _) => { if (Volatile.Read(ref boom) == 1) throw new InvalidOperationException("test overlay failure"); };
        var feed = new BallPitFeed(es, 320, 180, 30, false); var logs = new List<string>(); feed.Log += m => { lock (logs) logs.Add(m); };
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port;
        bool run = true; var th = new Thread(() => feed.Run(port, () => run)) { IsBackground = true }; th.Start();
        using var cl = l.AcceptTcpClient(); var ns = cl.GetStream(); var buf = new byte[1 << 16]; long got = 0;
        var rd = new Thread(() => { try { int k; while ((k = ns.Read(buf, 0, buf.Length)) > 0) Interlocked.Add(ref got, k); } catch { } }) { IsBackground = true }; rd.Start();
        Thread.Sleep(1500); long g1 = Interlocked.Read(ref got);
        Volatile.Write(ref boom, 1); Thread.Sleep(2000); long g2 = Interlocked.Read(ref got);
        Volatile.Write(ref boom, 0); Thread.Sleep(2500); long g3 = Interlocked.Read(ref got);
        run = false; th.Join(3000); l.Stop();
        long fb = 320 * 180 * 4;
        Check(feed.RenderErrors > 0, $"render exceptions were caught ({feed.RenderErrors})");
        Check((g2 - g1) / fb >= 50, $"frames kept flowing while every render threw ({(g2 - g1) / fb} frames in 2 s)");
        Check((g3 - g2) / fb >= 65, $"full rate after recovery ({(g3 - g2) / fb} frames in 2.5 s)");
        Check(logs.Count(x => x.Contains("render error")) is >= 1 and <= 2, $"error logged once (throttled): {logs.Count(x => x.Contains("render error"))}");
        Check(es.Count + es.Overflow == 20, $"pit reset keeps everyone (back in the queue): {es.Count} + {es.Overflow}");

        // ---- auto degrade on a slow renderer, recovery when fast ----
        var ds = new BallPitScene(new BallPitSettings { Mode = "Source" }, 6);
        for (int i = 0; i < 20; i++) ds.Join("d" + i, announce: false); ds.SpawnAllNow();
        int slow = 1; ds.Overlay = (_, _, _) => { if (Volatile.Read(ref slow) == 1) Thread.Sleep(40); };
        var df = new BallPitFeed(ds, 320, 180, 30, false); var dl = new List<string>(); df.Log += m => { lock (dl) dl.Add(m); };
        var l2 = new TcpListener(IPAddress.Loopback, 0); l2.Start(); int p2 = ((IPEndPoint)l2.LocalEndpoint).Port; bool run2 = true;
        var t2 = new Thread(() => df.Run(p2, () => run2)) { IsBackground = true }; t2.Start();
        using var c3 = l2.AcceptTcpClient(); var ns2 = c3.GetStream(); long got2 = 0;
        new Thread(() => { var b = new byte[1 << 16]; try { int k; while ((k = ns2.Read(b, 0, b.Length)) > 0) Interlocked.Add(ref got2, k); } catch { } }) { IsBackground = true }.Start();
        sw.Restart(); while (df.Level < 2 && sw.Elapsed.TotalSeconds < 12) Thread.Sleep(100);
        Check(df.Level >= 2, $"slow frames raise the degrade level ({df.Level} after {sw.Elapsed.TotalSeconds:0} s)");
        double wall = sw.Elapsed.TotalSeconds; long frames = Interlocked.Read(ref got2) / (320 * 180 * 4);
        Check(frames >= (wall + 1) * 30 * 0.9 - 30, $"output stayed at 30 fps while slow (repeats fill in): {frames} frames");
        Volatile.Write(ref slow, 0); sw.Restart(); while (df.Level > 0 && sw.Elapsed.TotalSeconds < 50) Thread.Sleep(200);
        Check(df.Level == 0, $"quality recovers when frames are fast again ({sw.Elapsed.TotalSeconds:0} s)");
        Check(df.RaiseLevel("test") && df.Level <= 1, "the engine can request a level (no encoder restart)");
        run2 = false; t2.Join(3000); l2.Stop();
        Check(dl.Any(x => x.Contains("quality level")), "level changes are logged");
        try { Directory.Delete(dir, true); } catch { }
        Console.WriteLine(fails == 0 ? "PIT LIMITS PASSED" : $"PIT LIMITS: {fails} FAILED");
        return fails == 0 ? 0 : 1;
    }
}
