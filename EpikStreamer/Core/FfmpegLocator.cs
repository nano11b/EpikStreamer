using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
namespace EpikStreamer.Core;

public static class FfmpegLocator
{
    public const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    static string Exe(string n) => OperatingSystem.IsWindows() ? n + ".exe" : n;
    public static string LocalAppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EpikStreamer");
    public static string DownloadDir => Path.Combine(LocalAppDataDir, "ffmpeg");

    /// <summary>Order: configured path, .\ffmpeg\ next to exe, next to exe, %LOCALAPPDATA%\EpikStreamer\ffmpeg, PATH.</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var b = AppContext.BaseDirectory;
        foreach (var c in new[] { Path.Combine(b, "ffmpeg", Exe("ffmpeg")), Path.Combine(b, Exe("ffmpeg")), Path.Combine(DownloadDir, Exe("ffmpeg")) })
            if (File.Exists(c)) return c;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var p = Path.Combine(dir.Trim('"'), Exe("ffmpeg")); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    public static string? FindProbe(string ffmpeg)
    {
        var p = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", Exe("ffprobe"));
        return File.Exists(p) ? p : null;
    }

    /// <summary>Downloads the BtbN win64 GPL build and extracts ffmpeg.exe + ffprobe.exe into DownloadDir.</summary>
    public static async Task<string> DownloadAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DownloadDir);
        var zipPath = Path.Combine(DownloadDir, "download.zip");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EpikStreamer/2.0");
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buf = new byte[1 << 16]; long got = 0; int n; var sw = Stopwatch.StartNew();
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct); got += n;
                if (sw.ElapsedMilliseconds > 300)
                {
                    sw.Restart();
                    progress.Report(total > 0 ? $"Downloading ffmpeg… {got * 100 / total}% ({got >> 20} / {total >> 20} MB)" : $"Downloading ffmpeg… {got >> 20} MB");
                }
            }
        }
        progress.Report("Extracting ffmpeg…");
        await Task.Run(() =>
        {
            using var z = ZipFile.OpenRead(zipPath);
            foreach (var e in z.Entries)
            {
                var name = e.Name.ToLowerInvariant();
                if ((name == "ffmpeg.exe" || name == "ffprobe.exe") && e.FullName.Replace('\\', '/').Contains("/bin/"))
                    e.ExtractToFile(Path.Combine(DownloadDir, e.Name), true);
            }
        }, ct);
        File.Delete(zipPath);
        var exe = Path.Combine(DownloadDir, "ffmpeg.exe");
        if (!File.Exists(exe)) throw new IOException("ffmpeg.exe was not found in the downloaded archive.");
        progress.Report("ffmpeg downloaded to " + DownloadDir);
        return exe;
    }

    public static readonly (string Id, string Label)[] Encoders =
    {
        ("libx264", "x264 (CPU)"), ("h264_nvenc", "NVIDIA NVENC"), ("h264_qsv", "Intel QuickSync"), ("h264_amf", "AMD AMF"),
    };

    /// <summary>Test-encodes a tiny null clip with each encoder; returns ids that worked.</summary>
    public static async Task<List<string>> DetectEncodersAsync(string ffmpeg)
    {
        var ok = new List<string>();
        foreach (var (id, _) in Encoders)
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "color=black:s=640x360:r=30", "-t", "0.5",
                                      "-pix_fmt", CommandBuilder.PixFmt(id), "-c:v", id, "-f", "null", "-" }) psi.ArgumentList.Add(a);
            try
            {
                using var p = Process.Start(psi)!;
                _ = p.StandardError.ReadToEndAsync(); _ = p.StandardOutput.ReadToEndAsync();
                var done = await Task.Run(() => p.WaitForExit(15000));
                if (!done) { try { p.Kill(true); } catch { } }
                else if (p.ExitCode == 0) ok.Add(id);
            }
            catch { }
        }
        return ok;
    }
}
