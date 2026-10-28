using System.Diagnostics;
using EpikStreamer.Core;
using SkiaSharp;

/// <summary>2.8.4 Space background: settings/migration, quality mapping, cache/build off the frame path, overlay transparency,
/// determinism, contrast, frame cost vs Classic; writes a 1280×720 still. dotnet T sky [still.png]</summary>
public static class SkyTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

    public static int Run(string still)
    {
        fails = 0;
        // settings / migration
        var fresh = new BallPitSettings(); fresh.Migrate();
        Check(fresh.Background == "Space", "new installs default to Space");
        var v283 = new BallPitSettings { DefaultsVersion = 283 }; v283.Migrate();
        Check(v283.Background == "Space" && v283.DefaultsVersion == 284, "a 2.8.3 profile with the stock background gets Space");
        var col = new BallPitSettings { DefaultsVersion = 283, BackgroundColor = "#000000" }; col.Migrate();
        Check(col.Background == "Classic", "a customised background colour keeps Classic");
        var img = new BallPitSettings { DefaultsVersion = 283, BackgroundImage = "/x.png" }; img.Migrate();
        Check(img.Background == "Classic", "a background image keeps Classic");
        var flo = new BallPitSettings { DefaultsVersion = 283, FloorColor = "#ff0000" }; flo.Migrate();
        Check(flo.Background == "Classic", "a customised floor keeps Classic");
        var chosen = new BallPitSettings { DefaultsVersion = 284, Background = "Classic" }; chosen.Migrate();
        Check(chosen.Background == "Classic", "a choice made in 2.8.4 is never migrated again");
        var bad = new BallPitSettings { DefaultsVersion = 283, Background = "Nope" }; bad.Migrate();
        Check(bad.Background == "Space", "unknown value → Space");
        var j = System.Text.Json.JsonSerializer.Serialize(new BallPitSettings { Background = "Classic", SkySeed = 77 });
        var back = System.Text.Json.JsonSerializer.Deserialize<BallPitSettings>(j)!;
        Check(back.Background == "Classic" && back.SkySeed == 77, "Background + SkySeed persist (JSON round trip)");

        // quality → animation
        Check(BallPitQuality.For("Low", 0).SkyAnim == 0 && BallPitQuality.For("Medium", 0).SkyAnim == 1 && BallPitQuality.For("High", 0).SkyAnim == 2, "Low static, Medium drift, High drift + twinkle");
        Check(BallPitQuality.For("High", 1).SkyAnim == 0 && BallPitQuality.For("Medium", 1).SkyAnim == 0, "step-down level 1 stops the sky animation");

        // build cost, determinism, contrast
        var sw = Stopwatch.StartNew(); using var a = SpaceSky.BuildNow(1280, 720, 5); double build = sw.Elapsed.TotalMilliseconds;
        using var b = SpaceSky.BuildNow(1280, 720, 5); using var c = SpaceSky.BuildNow(1280, 720, 6);
        Check(build < 3000, $"720p sky builds in {build:0} ms (off the frame path)");
        Check(a.Galaxies is >= 2 and <= 4 && a.Stars > 1000, $"{a.Galaxies} galaxies, {a.Stars} stars");
        static byte[] Px(SKImage i) { using var bm = SKBitmap.FromImage(i); return bm.Bytes; }
        var pa = Px(a.Image); Check(pa.AsSpan().SequenceEqual(Px(b.Image)), "same seed → identical sky");
        Check(!pa.AsSpan().SequenceEqual(Px(c.Image)), "new seed (Randomize) → a different sky");
        double lum = 0; long bright = 0; for (int i = 0; i < pa.Length; i += 4) { double l = 0.0722 * pa[i] + 0.7152 * pa[i + 1] + 0.2126 * pa[i + 2]; lum += l; if (l > 200) bright++; }
        lum /= pa.Length / 4;
        Check(lum < 32, $"sky stays dark behind the balls (mean luminance {lum:0.0}/255)");
        Check(bright > 300, $"stars visible ({bright} bright pixels)");

        // renderer: a missing sky never blocks a frame; cache bounded
        var s = new BallPitSettings { Mode = "Source", Background = "Space", SkySeed = 991 };
        var sc = BallPitTests.DemoScene(45, 3, s); sc.Quality = BallPitQuality.For("High", 0);
        using var r = new BallPitRenderer(1280, 720);
        sw.Restart(); r.Render(sc); double first = sw.Elapsed.TotalMilliseconds;
        Check(first < 200 && r.Sky == null, $"first frame doesn't wait for the sky ({first:0} ms, dark fill)");
        sw.Restart(); while (r.Sky == null && sw.Elapsed.TotalSeconds < 10) { sc.Step(1 / 30.0); r.Render(sc); Thread.Sleep(5); }
        Check(r.Sky != null, $"sky appears after {sw.Elapsed.TotalMilliseconds:0} ms in the background");
        for (int i = 0; i < 90; i++) sc.Step(1 / 30.0);
        r.Render(sc);
        if (still.Length > 0) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(still))!); r.SavePng(still); Console.WriteLine("  still: " + still); }
        var px = r.Bitmap.GetPixel(4, 4);
        Check(px.Alpha == 255 && px.Red + px.Green + px.Blue < 200, $"Source mode: opaque dark sky in the corner ({px})");
        long b0 = SpaceSky.Builds; for (int i = 0; i < 20; i++) r.Render(sc);
        Check(SpaceSky.Builds == b0, "no rebuilds while size/seed are unchanged");

        // overlay mode stays transparent
        using (var ro = new BallPitRenderer(640, 360) { Transparent = true })
        {
            ro.Render(sc); Thread.Sleep(200); ro.Render(sc);
            Check(ro.Bitmap.GetPixel(2, 2).Alpha == 0 && ro.Sky == null, "Overlay mode: no sky, transparent");
        }

        // frame cost vs Classic (same scene, 960×540 = Medium's 75 %)
        double Cost(string bg, string q)
        {
            var ss = new BallPitSettings { Mode = "Source", Background = bg, SkySeed = 12 };
            var s2 = BallPitTests.DemoScene(60, 4, ss); s2.Quality = BallPitQuality.For(q, 0);
            using var rr = new BallPitRenderer(960, 540);
            for (int i = 0; i < 400 && (i < 20 || (ss.IsSpace && rr.Sky == null)); i++) { s2.Step(1 / 30.0); rr.Render(s2); if (ss.IsSpace && rr.Sky == null) Thread.Sleep(5); }
            var t = new List<double>(); var w = Stopwatch.StartNew();
            for (int i = 0; i < 150; i++) { s2.Step(1 / 30.0); var t0 = w.Elapsed.TotalMilliseconds; rr.Render(s2); t.Add(w.Elapsed.TotalMilliseconds - t0); }
            t.Sort(); return t.Take(142).Average();   // trim the top 5 %
        }
        foreach (var q in new[] { "Low", "Medium", "High" })
        {
            double cl = Cost("Classic", q), sp = Cost("Space", q);
            Console.WriteLine($"  {q}: Classic {cl:0.00} ms, Space {sp:0.00} ms per frame (60 balls, 960×540)");
            Check(sp - cl < 2.0, $"{q}: Space adds {sp - cl:+0.00;-0.00} ms per frame (< 2 ms)");
        }
        Console.WriteLine(fails == 0 ? "SKY PASSED" : $"SKY: {fails} FAILED");
        return fails == 0 ? 0 : 1;
    }
}
