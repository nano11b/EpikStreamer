using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
namespace EpikStreamer.Core;

public record MediaInfo(double Duration, bool HasAudio, bool HasVideo);

public static class CommandBuilder
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string FontName = "font.ttf", TextFile = "overlay_text.txt", ClockFile = "clock.txt", PreviewFile = "preview.jpg";
    public const string AlertFile = "alert.txt", ChatFile = "chat_overlay.txt";

    /// <summary>v2.2 live features of one encoder run. ControlPort 0 = no runtime control (azmq).</summary>
    public record LiveOptions(int ControlPort = 0, int MusicPort = 0, string? MicDevice = null, bool TextOverlays = false,
                              double MusicVolume = 0.25, double MicVolume = 1.0, double MainVolume = 1.0, bool Card = false,
                              int BallPitPort = 0, string BallPitMode = "", int BallPitW = 0, int BallPitH = 0,
                              int RadioPort = 0, double RadioVolume = 0.35);

    /// <summary>v2.5 Ball Pit input: raw BGRA (premultiplied) frames the app renders and pushes over a local TCP socket.</summary>
    public static List<string> BallPitInputArgs(int port, int w, int h, int fps, FfmpegCaps? caps = null)
    {
        var a = new List<string>();
        if ((caps ?? FfmpegCaps.All).InputThreadQueue) a.AddRange(new[] { "-thread_queue_size", "16" });
        a.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "bgra", "-video_size", $"{w}x{h}", "-framerate", fps.ToString(Inv),
            "-i", $"tcp://127.0.0.1:{port}?listen=1&listen_timeout=20000" });
        return a;
    }

    /// <summary>Input args for the mic. "lavfi:&lt;graph&gt;" is a test hook; anything else is a dshow device name.</summary>
    public static List<string> MicInputArgs(string device, FfmpegCaps? caps = null)
    {
        if (device.StartsWith("lavfi:", StringComparison.Ordinal)) return new() { "-f", "lavfi", "-i", device[6..] };
        if (!Os.Windows)   // Linux: PulseAudio / PipeWire-pulse source name ("default" = system default)
        {
            if (device.StartsWith("alsa:", StringComparison.Ordinal)) return new() { "-f", "alsa", "-i", device[5..] };
            return new() { "-f", "pulse", "-fragment_size", "4096", "-i", device };
        }
        var a = new List<string> { "-f", "dshow" };
        if ((caps ?? FfmpegCaps.All).DshowBufferSize) a.AddRange(new[] { "-audio_buffer_size", "50" });
        a.AddRange(new[] { "-i", "audio=" + device });
        return a;
    }

    /// <summary>Input args for the music feed: raw PCM the engine pushes over a local TCP socket.</summary>
    public static List<string> MusicInputArgs(int port, FfmpegCaps? caps = null)
    {
        var a = new List<string>();
        if ((caps ?? FfmpegCaps.All).InputThreadQueue) a.AddRange(new[] { "-thread_queue_size", "64" });
        a.AddRange(new[] { "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", $"tcp://127.0.0.1:{port}?listen=1&listen_timeout=20000" });
        return a;
    }

    /// <summary>Feeder for one music file: decode → raw s16le 48k stereo on stdout, unthrottled (TCP backpressure paces it).</summary>
    public static List<string> MusicFeederArgs(string file) => new()
        { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", file, "-vn", "-sn", "-dn", "-af", "aresample=48000", "-f", "s16le", "-ar", "48000", "-ac", "2", "pipe:1" };

    static string V(double v) => v.ToString("0.###", Inv);

    /// <summary>Audio chain: main [+ mic] → (sidechain) → [+ music, ducked] → normalization → 44.1k.</summary>
    public static List<string> AudioGraph(Profile p, LiveOptions? lo, int musicIdx, int micIdx, FfmpegCaps? caps = null, int radioIdx = -1)
    {
        var c = caps ?? FfmpegCaps.All;
        var a = p.Audio; var f = new List<string>();
        var zmq = lo is { ControlPort: > 0 } && c.HasFilter("azmq") ? $"azmq=bind_address=tcp\\\\://127.0.0.1\\\\:{lo.ControlPort}," : "";
        double mainVol = lo?.MainVolume ?? a.MainVolume;
        f.Add($"[0:a]{zmq}asetpts=PTS-STARTPTS,aresample=48000:async=1000:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo,volume@main={V(mainVol)}[am]");
        string voice = "[am]";
        if (micIdx >= 0)
        {
            var gate = a.MicNoiseGate && c.HasFilter("agate") ? $"agate@gate=threshold={V(Math.Clamp(a.GateThreshold, 0.0001, 1))}:ratio=4:attack=5:release=200," : "";
            f.Add($"[{micIdx}:a]aresample=48000:async=1000,aformat=sample_fmts=fltp:channel_layouts=stereo,{gate}volume@mic={V(lo?.MicVolume ?? a.MicVolume)}[amic]");
            f.Add("[am][amic]amix=inputs=2:duration=first:normalize=0:dropout_transition=0[avo]"); voice = "[avo]";
        }
        string mix = voice;
        // background bed: music feed and/or v2.6 radio (each with its own live volume), ducked under the voice together
        string? bg = null;
        if (musicIdx >= 0) { f.Add($"[{musicIdx}:a]aformat=sample_fmts=fltp:channel_layouts=stereo,volume@music={V(lo?.MusicVolume ?? a.MusicVolume)}[amu]"); bg = "[amu]"; }
        if (radioIdx >= 0)
        {
            var rn = p.Radio.Normalize && c.HasFilter("loudnorm") ? "loudnorm=I=-20:TP=-2:LRA=11,aresample=48000," : "";
            f.Add($"[{radioIdx}:a]aformat=sample_fmts=fltp:channel_layouts=stereo,{rn}volume@radio={V(lo?.RadioVolume ?? p.Radio.Volume)}[ara]");
            if (bg == null) bg = "[ara]";
            else { f.Add($"{bg}[ara]amix=inputs=2:duration=first:normalize=0:dropout_transition=0[abg]"); bg = "[abg]"; }
        }
        if (bg != null)
        {
            string mu = bg;
            bool duck = musicIdx >= 0 ? a.Ducking || (radioIdx >= 0 && p.Radio.Duck) : p.Radio.Duck;
            if (duck && c.HasFilter("sidechaincompress"))
            {
                f.Add($"{voice}asplit=2[avm][asc]"); voice = "[avm]";
                f.Add($"{bg}[asc]sidechaincompress@duck=threshold={V(Math.Clamp(a.DuckThreshold, 0.001, 1))}:ratio={V(Math.Clamp(a.DuckRatio, 1, 20))}:attack=20:release=500[amd]"); mu = "[amd]";
            }
            f.Add($"{voice}{mu}amix=inputs=2:duration=first:normalize=0:dropout_transition=0[amx]"); mix = "[amx]";
        }
        var norm = a.Normalize switch
        {
            "Loudnorm" when c.HasFilter("loudnorm") => $"loudnorm=I={V(Math.Clamp(a.TargetLufs, -70, -5))}:TP=-1.5:LRA=11,",
            "Dynaudnorm" when c.HasFilter("dynaudnorm") => "dynaudnorm=f=250:g=15:p=0.9,",
            _ => "",
        };
        f.Add($"{mix}{norm}aresample=44100[aout]");
        return f;
    }

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

    public static string PixFmt(string encoder) => encoder is "h264_qsv" or "h264_vaapi" ? "nv12" : "yuv420p";

    /// <summary>VAAPI render node: $EPIK_VAAPI_DEVICE, else the first /dev/dri/renderD*.</summary>
    public static string VaapiDevice()
    {
        var env = Environment.GetEnvironmentVariable("EPIK_VAAPI_DEVICE");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try { var d = Directory.GetFiles("/dev/dri", "renderD*").OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault(); if (d != null) return d; } catch { }
        return "/dev/dri/renderD128";
    }

    /// <summary>Global args a hardware encoder needs before the inputs (VAAPI: device + filter device for hwupload).</summary>
    public static List<string> HwInitArgs(string encoder) => encoder == "h264_vaapi"
        ? new() { "-init_hw_device", "vaapi=va:" + VaapiDevice(), "-filter_hw_device", "va" } : new();

    /// <summary>Final video filter for the encoder's input.</summary>
    public static string EncoderFormat(string encoder) => "format=" + PixFmt(encoder) + (encoder == "h264_vaapi" ? ",hwupload" : "");

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

    public static string EncoderGraph(Profile p, bool hasLogo, LiveOptions? lo = null, int musicIdx = -1, int micIdx = -1, FfmpegCaps? caps = null, int cardIdx = -1, int pitIdx = -1, int radioIdx = -1)
    {
        var o = p.Overlays; var f = new List<string>(); string cur = "[v0]";
        bool pitScale = lo != null && (lo.BallPitW != p.Width || lo.BallPitH != p.Height);
        string pitIn = pitIdx < 0 ? "" : $"[{pitIdx}:v]setpts=PTS-STARTPTS" + (pitScale ? $",scale={p.Width}:{p.Height}:flags=bilinear" : "");
        if (pitIdx >= 0 && lo?.BallPitMode == "Source")
        {   // v2.5: the ball pit IS the picture; the playlist (or the silent filler) only provides audio + the timeline
            f.Add("[0:v]nullsink");
            f.Add($"{pitIn},fps={p.Fps},format=yuv420p,setsar=1[v0]");
        }
        else f.Add($"[0:v]setpts=PTS-STARTPTS,fps={p.Fps}[v0]");  // timestamp based: robust to partial (skipped) segments
        int n = 1;
        string Next() => $"[v{n++}]";
        if (pitIdx >= 0 && lo?.BallPitMode == "Overlay")
        {   // v2.5: transparent ball pit on top of the playlist video (under the logo/text/clock)
            f.Add($"{pitIn},format=gbrap,unpremultiply=inplace=1[pit]");   // renderer writes premultiplied BGRA; overlay blends straight alpha correctly in YUV
            var nx = Next(); f.Add($"{cur}[pit]overlay@pit=x=0:y=0:eof_action=pass:format=auto{nx}"); cur = nx;
        }
        if (hasLogo)
        {
            int lw = Math.Max(2, (int)(p.Width * Math.Clamp(o.ImageScale, 0.01, 1.0)));
            var (x, y) = OverlayPos(o.ImageCorner);
            f.Add($"[1:v]scale={lw}:-1,format=rgba,colorchannelmixer@logo=aa={Math.Clamp(o.ImageOpacity, 0, 1).ToString("0.00", Inv)}[logo]");
            var nx = Next(); f.Add($"{cur}[logo]overlay=x={x}:y={y}{nx}"); cur = nx;
        }
        if (o.TextEnabled && !string.IsNullOrWhiteSpace(o.Text))
        {
            var (x, y) = TextPos(o.TextCorner);
            if (o.Ticker) x = $"'w-mod(t*{Math.Max(10, o.TickerSpeed)}\\,w+tw)'";
            var box = o.TextBackground ? ":box=1:boxcolor=0x000000@0.5:boxborderw=8" : ":borderw=2:bordercolor=0x000000";
            var nx = Next();
            f.Add($"{cur}drawtext@text=fontfile={FontName}:textfile={TextFile}:expansion=none:fontsize={o.FontSize}:fontcolor={Color(o.FontColor)}:x={x}:y={y}{box}{nx}"); cur = nx;
        }
        if (o.ClockEnabled)
        {
            var (x, y) = TextPos(o.ClockCorner);
            var nx = Next();
            f.Add($"{cur}drawtext@clock=fontfile={FontName}:textfile={ClockFile}:fontsize={o.ClockFontSize}:fontcolor=white:x={x}:y={y}:box=1:boxcolor=0x000000@0.5:boxborderw=6{nx}"); cur = nx;
        }
        if (cardIdx >= 0)
        {   // v2.3 now-playing card: a PNG the app re-renders (transparent when hidden)
            var (x, y) = OverlayPos(p.NowPlaying.Corner);
            f.Add($"[{cardIdx}:v]format=rgba[npc]");
            var nx = Next(); f.Add($"{cur}[npc]overlay@np=x={x}:y={y}:eof_action=repeat:format=auto{nx}"); cur = nx;
        }
        if (lo is { TextOverlays: true })
        {   // chat-command responses (bottom-left) and alerts (top-centre), refreshed every frame from files the app rewrites
            var nx = Next();
            f.Add($"{cur}drawtext@chat=fontfile={FontName}:textfile={ChatFile}:reload=1:expansion=none:fontsize={Math.Max(16, p.Height / 30)}:fontcolor=white:x=24:y=h-th-24:box=1:boxcolor=0x000000@0.6:boxborderw=10{nx}"); cur = nx;
            nx = Next();
            f.Add($"{cur}drawtext@alert=fontfile={FontName}:textfile={AlertFile}:reload=1:expansion=none:fontsize={Math.Max(20, p.Height / 16)}:fontcolor=yellow:x=(w-tw)/2:y=h/8:borderw=3:bordercolor=0x000000:box=1:boxcolor=0x4B0082@0.7:boxborderw=16{nx}"); cur = nx;
        }
        f.Add($"{cur}split=2[vs][pvs]");
        f.Add($"[vs]{EncoderFormat(p.Encoder)}[vout]");
        f.Add("[pvs]fps=1,scale=320:-2,format=yuvj420p[pv]");
        f.AddRange(AudioGraph(p, lo, musicIdx, micIdx, caps, radioIdx));
        return string.Join(";", f);
    }

    public static List<string> VideoEncoderArgs(Profile p, FfmpegCaps? caps = null)
    {
        var c = caps ?? FfmpegCaps.All;
        string v = p.VideoKbps + "k", max = p.VideoKbps + "k", buf = (p.VideoKbps * 2) + "k", g = (p.Fps * Math.Max(1, p.KeyframeSeconds)).ToString();
        var a = p.Encoder switch
        {
            "h264_nvenc" => new List<string> { "-c:v", "h264_nvenc", "-preset", "p4", "-tune", "ll", "-rc", "cbr", "-profile:v", "high" },
            "h264_qsv" => new List<string> { "-c:v", "h264_qsv", "-preset", "veryfast", "-profile:v", "high" },
            "h264_vaapi" => new List<string> { "-c:v", "h264_vaapi", "-rc_mode", "CBR", "-profile:v", "high" },
            "h264_amf" => new List<string> { "-c:v", "h264_amf", "-usage", "lowlatency", "-quality", "speed", "-rc", "cbr" },
            _ => new List<string> { "-c:v", "libx264", "-preset", "veryfast", "-sc_threshold", "0" },
        };
        // drop encoder-private options this build doesn't know (they were renamed/removed across AMF/NVENC/QSV versions)
        var enc = a[1];
        for (int i = 2; i + 1 < a.Count;)
            if (!c.EncoderHas(enc, a[i].TrimStart('-').Split(':')[0]) && a[i] != "-profile:v") a.RemoveRange(i, 2); else i += 2;
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

    public static string TeeSpec(IReadOnlyList<Destination> dests, FfmpegCaps? caps = null) => string.Join("|", dests.Select(d => d.IsRecording
        ? "[f=matroska:onfail=ignore]" + TeeEscape(d.Target.Replace('\\', '/'))
        : ((caps ?? FfmpegCaps.All).FlvFlags ? "[f=flv:onfail=ignore:flvflags=no_duration_filesize]" : "[f=flv:onfail=ignore]") + TeeEscape(d.Target)));

    /// <summary>Long-lived encoder: reads continuous MPEG-TS on stdin, applies overlays, sends FLV to every destination
    /// (tee muxer, a failing destination does not stop the others) + optional MKV recording + preview JPEG.</summary>
    public static List<string> EncoderArgs(Profile p, IReadOnlyList<Destination> dests, bool hasLogo, LiveOptions? lo = null, FfmpegCaps? caps = null)
    {
        var c = caps ?? FfmpegCaps.All;
        // global options first, then each input's own options immediately before its -i (ffmpeg master rejects misplaced ones)
        var a = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1" };
        if (c.StatsPeriod) a.AddRange(new[] { "-stats_period", "1" });
        if (c.InputThreadQueue) a.AddRange(new[] { "-thread_queue_size", "1024" });
        if (c.ReinitFilter) a.AddRange(new[] { "-reinit_filter", "0" });
        a.AddRange(HwInitArgs(p.Encoder));
        a.AddRange(new[] { "-re", "-f", "mpegts", "-i", "pipe:0" });
        int next = 1;
        if (hasLogo) { a.Add("-i"); a.Add(p.Overlays.ImagePath); next++; }
        int musicIdx = -1, micIdx = -1;
        if (lo is { MusicPort: > 0 }) { a.AddRange(MusicInputArgs(lo.MusicPort, c)); musicIdx = next++; }
        if (!string.IsNullOrWhiteSpace(lo?.MicDevice)) { a.AddRange(MicInputArgs(lo.MicDevice!, c)); micIdx = next++; }
        int radioIdx = -1;
        if (lo is { RadioPort: > 0 }) { a.AddRange(MusicInputArgs(lo.RadioPort, c)); radioIdx = next++; }   // same raw-PCM TCP input as music
        int cardIdx = -1;
        if (lo is { Card: true }) { a.AddRange(NowPlayingCard.InputArgs()); cardIdx = next++; }
        int pitIdx = -1;
        if (lo is { BallPitPort: > 0 }) { a.AddRange(BallPitInputArgs(lo.BallPitPort, lo.BallPitW, lo.BallPitH, p.Fps, c)); pitIdx = next++; }
        a.AddRange(new[] { "-filter_complex", EncoderGraph(p, hasLogo, lo, musicIdx, micIdx, c, cardIdx, pitIdx, radioIdx), "-map", "[vout]", "-map", "[aout]" });
        a.AddRange(VideoEncoderArgs(p, c));
        a.AddRange(new[] { "-c:a", "aac", "-b:a", p.AudioKbps + "k", "-ar", "44100", "-ac", "2" });
        if (dests.Count == 1 && !dests[0].IsRecording)
            a.AddRange(c.FlvFlags ? new[] { "-f", "flv", "-flvflags", "no_duration_filesize", dests[0].Target } : new[] { "-f", "flv", dests[0].Target });
        else
            a.AddRange(new[] { "-flags", "+global_header", "-f", "tee", TeeSpec(dests, c) });
        a.AddRange(new[] { "-map", "[pv]", "-c:v", "mjpeg", "-q:v", "5", "-f", "image2", "-update", "1" });
        if (c.AtomicWriting) a.AddRange(new[] { "-atomic_writing", "1" });
        a.Add(PreviewFile);
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
