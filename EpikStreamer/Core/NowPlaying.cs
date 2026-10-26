using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
namespace EpikStreamer.Core;

/// <summary>v2.3 now-playing card (per profile, Overlays tab).</summary>
public class NowPlayingSettings
{
    public bool Enabled { get; set; }
    public string Mode { get; set; } = "Auto";          // Auto = show for ShowSeconds on each track change; Always = always on
    public int ShowSeconds { get; set; } = 12;
    public Corner Corner { get; set; } = Corner.BottomLeft;
    public double WidthFraction { get; set; } = 0.42;   // card width as a fraction of the output width
    public string Background { get; set; } = "#101018";
    public double Opacity { get; set; } = 0.82;
    public string TextColor { get; set; } = "#FFFFFF";
    public string AccentColor { get; set; } = "#A855F7";
    public bool ShowThumbnail { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public bool ShowUpNext { get; set; } = true;
    public bool ShowRequester { get; set; } = true;
    public string Label { get; set; } = "NOW PLAYING";
}

public record NowPlayingInfo(string Title, string RequestedBy, string UpNext, double Position, double Duration, string Thumb);

public static class NowPlayingCard
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string CardFile = "np_card.png", TitleFile = "np_title.txt", SubFile = "np_sub.txt", NextFile = "np_next.txt", TimeFile = "np_time.txt", LabelFile = "np_label.txt";

    public static (int W, int H) Size(Profile p)
    {
        int w = (int)(p.Width * Math.Clamp(p.NowPlaying.WidthFraction, 0.2, 0.9)) & ~1;
        int h = Math.Max(64, (int)(w * 0.27)) & ~1;
        return (w, h);
    }

    /// <summary>Encoder input for the card: image2 re-reads the PNG every frame (-loop 1), paced by -re, so a rewritten card shows up within ~1 s.</summary>
    public static List<string> InputArgs() => new() { "-re", "-f", "image2", "-loop", "1", "-framerate", "5", "-i", CardFile };

    static string Hex(string c) => CommandBuilder.Color(c);
    static string F(double d) => d.ToString("0.###", Inv);

    /// <summary>One-shot ffmpeg that renders the card PNG (texts come from files next to it, so nothing needs escaping).</summary>
    public static List<string> RenderArgs(Profile p, NowPlayingInfo i, string outFile, bool hasThumb)
    {
        var s = p.NowPlaying; var (W, H) = Size(p);
        int pad = Math.Max(6, H / 12), bar = s.ShowProgress ? Math.Max(4, H / 24) : 0;
        int th = H - 2 * pad - bar, tw = hasThumb ? th * 16 / 9 : 0;
        int tx = pad + (hasThumb ? tw + pad : 0);
        int fsLabel = Math.Max(10, H / 9), fsTitle = Math.Max(14, H / 5), fsSmall = Math.Max(10, H / 8);
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"color=c={Hex(s.Background)}@{F(Math.Clamp(s.Opacity, 0, 1))}:s={W}x{H},format=rgba" };
        if (hasThumb) { a.Add("-i"); a.Add(i.Thumb); }
        var g = new List<string>();
        string cur = "[0:v]";
        if (hasThumb) { g.Add($"[1:v]scale={tw}:{th}:force_original_aspect_ratio=decrease,pad={tw}:{th}:(ow-iw)/2:(oh-ih)/2:color=black,format=rgba[th]"); g.Add($"{cur}[th]overlay={pad}:{pad}[c0]"); cur = "[c0]"; }
        string dt(string file, int fs, string color, string x, string y) =>
            $"drawtext=fontfile={CommandBuilder.FontName}:textfile={file}:expansion=none:fontsize={fs}:fontcolor={color}:x={x}:y={y}";
        var chain = new List<string>
        {
            dt(LabelFile, fsLabel, Hex(s.AccentColor), tx.ToString(), pad.ToString()),
            dt(TitleFile, fsTitle, Hex(s.TextColor), tx.ToString(), (pad + fsLabel + pad / 2).ToString()),
            dt(SubFile, fsSmall, Hex(s.TextColor) + "@0.85", tx.ToString(), (pad + fsLabel + fsTitle + pad).ToString()),
            dt(NextFile, fsSmall, Hex(s.TextColor) + "@0.7", tx.ToString(), (H - bar - pad - fsSmall).ToString()),
        };
        if (s.ShowProgress)
        {
            chain.Add(dt(TimeFile, fsSmall, Hex(s.TextColor) + "@0.85", $"w-tw-{pad}", pad.ToString()));
            double frac = i.Duration > 0 ? Math.Clamp(i.Position / i.Duration, 0, 1) : 0;
            chain.Add($"drawbox=x=0:y={H - bar}:w={W}:h={bar}:color=white@0.25:t=fill");
            int bw = (int)Math.Round(W * frac); if (bw >= 1) chain.Add($"drawbox=x=0:y={H - bar}:w={bw}:h={bar}:color={Hex(s.AccentColor)}:t=fill");
        }
        g.Add(cur + string.Join(",", chain) + ",format=rgba[out]");
        a.AddRange(new[] { "-filter_complex", string.Join(";", g), "-map", "[out]", "-frames:v", "1", "-update", "1", "-f", "image2", "-c:v", "png", outFile });
        return a;
    }

    public static List<string> BlankArgs(Profile p, string outFile)
    {
        var (W, H) = Size(p);
        return new() { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c=black@0:s={W}x{H},format=rgba",
            "-frames:v", "1", "-update", "1", "-f", "image2", "-c:v", "png", outFile };
    }

    public static string TimeText(NowPlayingInfo i) => i.Duration > 0 ? $"{LibraryItem.FormatTime(i.Position)} / {LibraryItem.FormatTime(i.Duration)}" : LibraryItem.FormatTime(i.Position);
    static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    public static void WriteTexts(string dir, Profile p, NowPlayingInfo i)
    {
        var s = p.NowPlaying; var (W, H) = Size(p);
        int fsTitle = Math.Max(14, H / 5), chars = Math.Max(12, (int)((W - (s.ShowThumbnail ? H * 16 / 9 : 0)) / (fsTitle * 0.55)));
        var utf8 = new UTF8Encoding(false);
        void W1(string f, string t) => File.WriteAllText(Path.Combine(dir, f), t.Replace("\r", "").Replace("\n", " "), utf8);
        W1(LabelFile, s.Label);
        W1(TitleFile, Clip(i.Title, chars));
        W1(SubFile, s.ShowRequester && i.RequestedBy.Length > 0 ? "Requested by " + Clip(i.RequestedBy, 30) : " ");
        W1(NextFile, s.ShowUpNext && i.UpNext.Length > 0 ? "Up next: " + Clip(i.UpNext, chars + 8) : " ");
        W1(TimeFile, TimeText(i));
    }

    /// <summary>Renders to a temp file then atomically replaces the card the encoder reads.</summary>
    public static bool Render(string ffmpeg, string dir, Profile p, NowPlayingInfo? i)
    {
        var tmp = Path.Combine(dir, "np_card.tmp.png");
        List<string> args;
        if (i == null) args = BlankArgs(p, tmp);
        else
        {
            WriteTexts(dir, p, i);
            bool thumb = p.NowPlaying.ShowThumbnail && i.Thumb.Length > 0 && File.Exists(i.Thumb);
            args = RenderArgs(p, i, tmp, thumb);
        }
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var x in args) psi.ArgumentList.Add(x);
        try
        {
            using var pr = Process.Start(psi)!; var err = pr.StandardError.ReadToEndAsync();
            if (!pr.WaitForExit(10000)) { try { pr.Kill(true); } catch { } return false; }
            if (pr.ExitCode != 0 || !File.Exists(tmp)) { LastError = err.Result.Trim(); return false; }
            for (int k = 0; k < 10; k++)
            {
                try { File.Move(tmp, Path.Combine(dir, CardFile), true); return true; }
                catch (IOException) { Thread.Sleep(30); } catch (UnauthorizedAccessException) { Thread.Sleep(30); }
            }
        }
        catch (Exception ex) { LastError = ex.Message; }
        return false;
    }
    public static string LastError { get; private set; } = "";
}

/// <summary>Decides when the card is visible and re-renders it (track change, then every second while visible for the progress bar).</summary>
public sealed class NowPlayingController : IDisposable
{
    readonly string _ffmpeg, _dir; readonly Profile _p; readonly Func<NowPlayingInfo?> _info;
    readonly System.Threading.Timer _timer; int _busy; DateTime _showUntil = DateTime.MinValue; bool _shown; string _track = "";
    public bool Hidden { get; set; }
    public int Renders { get; private set; }
    public event Action<string>? Log;
    public NowPlayingController(string ffmpeg, string dir, Profile p, Func<NowPlayingInfo?> info)
    {
        _ffmpeg = ffmpeg; _dir = dir; _p = p; _info = info;
        NowPlayingCard.Render(ffmpeg, dir, p, null);          // the encoder needs the file before it starts
        _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }
    /// <summary>Show the card now (track change or the host's "Show now playing" button).</summary>
    public void TrackChanged(string key) { _track = key; _showUntil = DateTime.UtcNow.AddSeconds(Math.Max(2, _p.NowPlaying.ShowSeconds)); Tick(); }
    public void ShowNow() => TrackChanged(_track);
    public bool Visible => !Hidden && (_p.NowPlaying.Mode == "Always" || DateTime.UtcNow < _showUntil);
    public void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var vis = Visible; var info = vis ? _info() : null;
            if (vis && info != null) { if (NowPlayingCard.Render(_ffmpeg, _dir, _p, info)) { Renders++; _shown = true; } else Log?.Invoke("Now-playing card render failed: " + NowPlayingCard.LastError); }
            else if (_shown) { NowPlayingCard.Render(_ffmpeg, _dir, _p, null); _shown = false; }
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
    public void Dispose() { _timer.Dispose(); }
}
