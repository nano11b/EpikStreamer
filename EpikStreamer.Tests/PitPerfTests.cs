using System.Diagnostics;
using System.Globalization;
using EpikStreamer.Core;
/// <summary>2.8.3 Ball Pit performance: dotnet T pitbench [out.md]  (renderer + physics, 10..500 people, avatars on/off)</summary>
static class PitPerfTests
{
    static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    public static int Bench(string outPath)
    {
        var sb = new System.Text.StringBuilder();
        var proc = Process.GetCurrentProcess();
        sb.AppendLine($"Ball Pit bench, 1280×720 @30 fps budget 33.3 ms, {Environment.ProcessorCount} cores, background {Environment.GetEnvironmentVariable("BG") ?? "Classic"}, quality {Environment.GetEnvironmentVariable("QUALITY") ?? "Medium"}, {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("| people | avatars | balls | step ms | render ms avg | p95 | max | CPU cores | alloc KB/frame | Gen0 GCs/100f |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var avatars in new[] { false, true })
            foreach (var n in new[] { 10, 50, 100, 200, 500 })
            {
                var s = new BallPitSettings { Mode = "Source", MaxBalls = 1000, Background = Environment.GetEnvironmentVariable("BG") ?? "Classic", Quality = Environment.GetEnvironmentVariable("QUALITY") ?? "Medium" };
                var sc = BallPitTests.DemoScene(0, 7, s);
                var tf = sc.TextureFor; if (!avatars) sc.TextureFor = _ => null;
                for (int i = 0; i < n; i++) sc.Join("User" + i, "fake://avatar/" + i, null, announce: false);
                sc.SpawnAllNow();
                for (int i = 0; i < 90; i++) sc.Step(1 / 30.0);
                using var r = new BallPitRenderer(1280, 720);
                sc.Quality = BallPitQuality.For(s.Quality, 0);
                for (int i = 0; i < 10 || (s.IsSpace && r.Sky == null && i < 600); i++) { sc.Step(1 / 30.0); r.Render(sc); if (r.Sky == null && s.IsSpace) Thread.Sleep(10); }
                int frames = 100; var st = new List<double>(); var rt = new List<double>();
                GC.Collect(); long a0 = GC.GetTotalAllocatedBytes(true); int g0 = GC.CollectionCount(0); proc.Refresh(); var c0 = proc.TotalProcessorTime; var w = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    var t0 = w.Elapsed.TotalMilliseconds; sc.Step(1 / 30.0); var t1 = w.Elapsed.TotalMilliseconds; r.Render(sc); var t2 = w.Elapsed.TotalMilliseconds;
                    st.Add(t1 - t0); rt.Add(t2 - t1);
                }
                proc.Refresh(); var cpu = (proc.TotalProcessorTime - c0).TotalMilliseconds / w.Elapsed.TotalMilliseconds;
                long alloc = GC.GetTotalAllocatedBytes(true) - a0; int gcs = GC.CollectionCount(0) - g0;
                rt.Sort();
                sb.AppendLine($"| {n} | {(avatars ? "on" : "off")} | {sc.Count} | {F(st.Average())} | {F(rt.Average())} | {F(rt[(int)(frames * 0.95)])} | {F(rt[^1])} | {F(cpu)} | {alloc / 1024 / frames} | {gcs} |");
            }
        Console.Write(sb); if (outPath.Length > 0) File.WriteAllText(outPath, sb.ToString());
        return 0;
    }
}
