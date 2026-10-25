using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
namespace EpikStreamer.Core;

public record MediaInfo(double Duration, bool HasAudio, bool HasVideo);

public static class CommandBuilder
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string FontName = "font.ttf", TextFile = "overlay_text.txt", ClockFile = "clock.txt", PreviewFile = "preview.jpg";
    const int M = 20; // overlay margin in px

    public static string JoinUrl(string server, string key)
    {
        server = server.Trim().TrimEnd('/');
        key = key.Trim().TrimStart('/');
        return key.Length == 0 ? server : server + "/" + key;
    }

    /// <summary>Escape a path for use as a filter option value (only needed if files can't be referenced relatively).</summary>
    public static string FilterPath(string p) => "'" + p.Replace('\\', '/').Replace(":", "\\:") + "'";

    public static string DefaultFont() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf")
        : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";

    public static string PixFmt(string encoder) => encoder == "h264_qsv" ? "nv12" : "yuv420p";

    public static string Color(string c)
    {
        c = (c ?? "").Trim();
        if (Regex.IsMatch(c, "^#[0-9A-Fa-f]{6}$")) return "0x" + c[1..];
        if (Regex.IsMatch(c, "^(0x)?[0-9A-Fa-f]{6}$")) return c.StartsWith("0x") ? c : "0x" + c;
        if (Regex.IsMatch(c, "^[A-Za-z]+$")) return c;
        return "white";
    }

    static (string x, string y) TextPos(Corner c) => c switch
    {
        Corner.TopLeft => ($"{M}", $"{M}"),
        Corner.TopCenter => ("(w-tw)/2", $"{M}"),
        Corner.TopRight => ($"w-tw-{M}", $"{M}"),
        Corner.Center => ("(w-tw)/2", "(h-th)/2"),
        Corner.BottomLeft => ($"{M}", $"h-th-{M}"),
        Corner.BottomCenter => ("(w-tw)/2", $"h-th-{M}"),
        _ => ($"w-tw-{M}", $"h-th-{M}"),
    };
    static (string x, string y) OverlayPos(Corner c) => c switch
    {
        Corner.TopLeft => ($"{M}", $"{M}"),
        Corner.TopCenter => ("(main_w-overlay_w)/2", $"{M}"),
        Corner.TopRight => ($"main_w-overlay_w-{M}", $"{M}"),
        Corner.Center => ("(main_w-overlay_w)/2", "(main_h-overlay_h)/2"),
        Corner.BottomLeft => ($"{M}", $"main_h-overlay_h-{M}"),
        Corner.BottomCenter => ("(main_w-overlay_w)/2", $"main_h-overlay_h-{M}"),
        _ => ($"main_w-overlay_w-{M}", $"main_h-overlay_h-{M}"),
    };

    public static readonly string[] Transitions = { "fade", "fadeblack", "fadewhite", "dissolve", "slideleft", "slideright", "slideup", "slidedown",
        "wipeleft", "wiperight", "circleopen", "circleclose", "radial", "smoothleft", "pixelize", "distance" };

    public const int SegAudioRate = 48000;
    static string Sec(double d) => d.ToString("0.######", Inv);

    /// <summary>One source inside a segment: file, start offset, optional exact length (null = play to end).</summary>
    public record SegSource(string File, MediaInfo Info, double Start, double? Length);

    /// <summary>
    /// Segment "feeder": decodes one body (single source, exact length) or one transition (source A tail xfaded into
    /// source B head) and writes MPEG-TS (x264 intra-friendly + lossless s302m PCM, so no AAC priming drift) to stdout,
    /// with timestamps offset to the continuous playlist timeline.
    /// </summary>
    public static List<string> SegmentArgs(Profile p, SegSource a, SegSource? b, double crossfade, string transition, double tsOffset)
    {
        int W = p.Width, H = p.Height, F = p.Fps;
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "warning", "-nostats" };
        var srcs = b == null ? new[] { a } : new[] { a, b };
        int nextInput = 0; var aIns = new List<string>();
        var inputs = new List<int>();
        foreach (var s in srcs)
        {
            if (s.Start > 0.0005) { args.Add("-ss"); args.Add(Sec(s.Start)); }
            if (s.Length is double L) { args.Add("-t"); args.Add(Sec(L + 1)); }
            args.Add("-i"); args.Add(s.File); inputs.Add(nextInput++);
        }
        for (int k = 0; k < srcs.Length; k++)
        {
            if (srcs[k].Info.HasAudio) aIns.Add($"[{inputs[k]}:a:0]");
            else { args.AddRange(new[] { "-f", "lavfi", "-i", $"anullsrc=r={SegAudioRate}:cl=stereo" }); aIns.Add($"[{nextInput++}:a]"); }
        }
        var g = new List<string>();
        for (int k = 0; k < srcs.Length; k++)
        {
            var s = srcs[k];
            var v = $"[{inputs[k]}:v:0]scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={F},format=yuv420p," +
                    "setparams=range=tv:color_primaries=bt709:color_trc=bt709:colorspace=bt709";
            var au = $"{aIns[k]}aresample={SegAudioRate},aformat=sample_fmts=s16:channel_layouts=stereo,apad";
            if (s.Length is double L)
            {   // exact length: pad short sources by cloning the last frame / silence, then cut precisely
                v += $",tpad=stop_mode=clone:stop_duration={Sec(L)},trim=duration={Sec(L)},setpts=PTS-STARTPTS,fps={F}";
                au += $",atrim=duration={Sec(L)},asetpts=PTS-STARTPTS";
            }
            g.Add(v + $"[v{k}]"); g.Add(au + $"[a{k}]");
        }
        if (b == null) { g.Add("[a0]asetnsamples=n=1024:p=0,aformat=sample_fmts=s16:channel_layouts=stereo[a]"); g[0] = g[0].Replace("[v0]", "[v]"); }
        else
        {
            g.Add($"[v0][v1]xfade=transition={transition}:duration={Sec(crossfade)}:offset=0,format=yuv420p[v]");
            g.Add($"[a0][a1]acrossfade=d={Sec(crossfade)},asetnsamples=n=1024:p=0,aformat=sample_fmts=s16:channel_layouts=stereo[a]");
        }
        args.AddRange(new[] { "-filter_complex", string.Join(";", g), "-map", "[v]", "-map", "[a]" });
        if (a.Length == null) args.Add("-shortest");
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", "16", "-g", F.ToString(), "-bf", "0",
            "-c:a", "s302m", "-strict", "-2", "-ar", SegAudioRate.ToString(), "-ac", "2",
            "-f", "mpegts", "-muxdelay", "0", "-muxpreload", "0", "-output_ts_offset", Sec(tsOffset), "pipe:1" });
        return args;
    }

    public static string EncoderGraph(Profile p, bool hasLogo)
    {
        var o = p.Overlays; var f = new List<string>(); string cur = "[v0]";
        f.Add($"[0:v]setpts=PTS-STARTPTS,fps={p.Fps}[v0]");  // timestamp based: robust to partial (skipped) segments
        int n = 1;
        string Next() => $"[v{n++}]";
        if (hasLogo)
        {
            int lw = Math.Max(2, (int)(p.Width * Math.Clamp(o.ImageScale, 0.01, 1.0)));
            var (x, y) = OverlayPos(o.ImageCorner);
            f.Add($"[1:v]scale={lw}:-1,format=rgba,colorchannelmixer=aa={Math.Clamp(o.ImageOpacity, 0, 1).ToString("0.00", Inv)}[logo]");
            var nx = Next(); f.Add($"{cur}[logo]overlay=x={x}:y={y}{nx}"); cur = nx;
        }
        if (o.TextEnabled && !string.IsNullOrWhiteSpace(o.Text))
        {
            var (x, y) = TextPos(o.TextCorner);
            if (o.Ticker) x = $"'w-mod(t*{Math.Max(10, o.TickerSpeed)}\\,w+tw)'";
            var box = o.TextBackground ? ":box=1:boxcolor=0x000000@0.5:boxborderw=8" : ":borderw=2:bordercolor=0x000000";
            var nx = Next();
            f.Add($"{cur}drawtext=fontfile={FontName}:textfile={TextFile}:expansion=none:fontsize={o.FontSize}:fontcolor={Color(o.FontColor)}:x={x}:y={y}{box}{nx}"); cur = nx;
        }
        if (o.ClockEnabled)
        {
            var (x, y) = TextPos(o.ClockCorner);
            var nx = Next();
            f.Add($"{cur}drawtext=fontfile={FontName}:textfile={ClockFile}:fontsize={o.ClockFontSize}:fontcolor=white:x={x}:y={y}:box=1:boxcolor=0x000000@0.5:boxborderw=6{nx}"); cur = nx;
        }
        f.Add($"{cur}split=2[vs][pvs]");
        f.Add($"[vs]format={PixFmt(p.Encoder)}[vout]");
        f.Add("[pvs]fps=1,scale=320:-2,format=yuvj420p[pv]");
        f.Add("[0:a]asetpts=PTS-STARTPTS,aresample=44100:async=1000:first_pts=0[aout]");
        return string.Join(";", f);
    }

    public static List<string> VideoEncoderArgs(Profile p)
    {
        string v = p.VideoKbps + "k", max = p.VideoKbps + "k", buf = (p.VideoKbps * 2) + "k", g = (p.Fps * Math.Max(1, p.KeyframeSeconds)).ToString();
        var a = p.Encoder switch
        {
            "h264_nvenc" => new List<string> { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-rc", "cbr", "-profile:v", "high" },
            "h264_qsv" => new List<string> { "-c:v", "h264_qsv", "-preset", "veryfast", "-profile:v", "high" },
            "h264_amf" => new List<string> { "-c:v", "h264_amf", "-usage", "lowlatency", "-quality", "speed", "-rc", "cbr" },
            _ => new List<string> { "-c:v", "libx264", "-preset", "veryfast", "-sc_threshold", "0" },
        };
        a.AddRange(new[] { "-b:v", v, "-maxrate", max, "-bufsize", buf, "-g", g, "-keyint_min", g, "-r", p.Fps.ToString() });
        return a;
    }

    /// <summary>A tee slave: a network destination or the local recording.</summary>
    public record Destination(string Name, string Target, bool IsRecording = false);

    /// <summary>Escape a tee slave filename (tee treats \ | [ ] specially).</summary>
    public static string TeeEscape(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s) { if (c is '\\' or '|' or '[' or ']') sb.Append('\\'); sb.Append(c); }
        return sb.ToString();
    }

    public static string TeeSpec(IReadOnlyList<Destination> dests) => string.Join("|", dests.Select(d => d.IsRecording
        ? "[f=matroska:onfail=ignore]" + TeeEscape(d.Target.Replace('\\', '/'))
        : "[f=flv:onfail=ignore:flvflags=no_duration_filesize]" + TeeEscape(d.Target)));

    /// <summary>Long-lived encoder: reads continuous MPEG-TS on stdin, applies overlays, sends FLV to every destination
    /// (tee muxer, a failing destination does not stop the others) + optional MKV recording + preview JPEG.</summary>
    public static List<string> EncoderArgs(Profile p, IReadOnlyList<Destination> dests, bool hasLogo)
    {
        var a = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1", "-stats_period", "1",
            "-thread_queue_size", "1024", "-reinit_filter", "0", "-re", "-f", "mpegts", "-i", "pipe:0" };
        if (hasLogo) { a.Add("-i"); a.Add(p.Overlays.ImagePath); }
        a.AddRange(new[] { "-filter_complex", EncoderGraph(p, hasLogo), "-map", "[vout]", "-map", "[aout]" });
        a.AddRange(VideoEncoderArgs(p));
        a.AddRange(new[] { "-c:a", "aac", "-b:a", p.AudioKbps + "k", "-ar", "44100", "-ac", "2" });
        if (dests.Count == 1 && !dests[0].IsRecording)
            a.AddRange(new[] { "-f", "flv", "-flvflags", "no_duration_filesize", dests[0].Target });
        else
            a.AddRange(new[] { "-flags", "+global_header", "-f", "tee", TeeSpec(dests) });
        a.AddRange(new[] { "-map", "[pv]", "-c:v", "mjpeg", "-q:v", "5", "-f", "image2", "-update", "1", "-atomic_writing", "1", PreviewFile });
        return a;
    }

    /// <summary>Expand the recording filename pattern ({profile} {date} {time} {part}); returns a full .mkv path.</summary>
    public static string RecordingPath(Profile p, int part, DateTime now)
    {
        var folder = string.IsNullOrWhiteSpace(p.RecordFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "EpikStreamer") : p.RecordFolder;
        var pat = string.IsNullOrWhiteSpace(p.RecordPattern) ? "{profile}_{date}_{time}" : p.RecordPattern;
        var name = pat.Replace("{profile}", p.Name).Replace("{date}", now.ToString("yyyy-MM-dd", Inv))
                      .Replace("{time}", now.ToString("HH-mm-ss", Inv)).Replace("{part}", part.ToString(Inv));
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '|', '[', ']', ':', '\\', '/' })) name = name.Replace(c, '_');
        if (part > 1 && !pat.Contains("{part}")) name += "_part" + part;
        return Path.Combine(folder, name + ".mkv");
    }

    /// <summary>Remux an MKV recording to MP4 (stream copy, faststart).</summary>
    public static List<string> RemuxArgs(string mkv, string mp4) =>
        new() { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", mkv, "-map", "0", "-c", "copy", "-movflags", "+faststart", mp4 };

    /// <summary>Hide every secret (raw and tee-escaped) in a log line or command line.</summary>
    public static string Mask(string s, IEnumerable<string> secrets)
    {
        foreach (var k in secrets.Where(k => k.Length >= 3).OrderByDescending(k => k.Length))
        { s = s.Replace(k, "****"); var e = TeeEscape(k); if (e != k) s = s.Replace(e, "****"); }
        return s;
    }

    public static string Display(IEnumerable<string> args, string? secret) => Display(args, secret == null ? Array.Empty<string>() : new[] { secret });
    public static string Display(IEnumerable<string> args, IReadOnlyCollection<string> secrets)
    {
        return string.Join(' ', args.Select(x =>
        {
            x = Mask(x, secrets);
            return x.Contains(' ') || x.Contains(';') ? "\"" + x + "\"" : x;
        }));
    }
}
