using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
namespace EpikStreamer.Core;

/// <summary>What a particular ffmpeg build accepts. Probed once per binary (path + size + mtime) and cached,
/// so version-sensitive options/filters are only emitted when the build understands them.</summary>
public sealed class FfmpegCaps
{
    public string Version { get; init; } = "";
    /// <summary>-thread_queue_size as a per-INPUT option. Removed for inputs in ffmpeg master (2026): passing it before -i fails with EINVAL.</summary>
    public bool InputThreadQueue { get; init; } = true;
    public bool ReinitFilter { get; init; } = true;
    public bool StatsPeriod { get; init; } = true;
    public bool AtomicWriting { get; init; } = true;
    public bool FlvFlags { get; init; } = true;
    public bool DshowBufferSize { get; init; } = true;
    /// <summary>http protocol -reconnect/-reconnect_streamed, -reconnect_delay_max, and the generic -rw_timeout (v2.6 radio relay).</summary>
    public bool HttpReconnect { get; init; } = true;
    public bool HttpReconnectDelayMax { get; init; } = true;
    public bool RwTimeout { get; init; } = true;
    /// <summary>null = unknown/assume everything is present.</summary>
    public HashSet<string>? Filters { get; init; }
    public HashSet<string>? Encoders { get; init; }
    public Dictionary<string, HashSet<string>>? EncoderOptions { get; init; }
    public List<string> Notes { get; } = new();

    /// <summary>Assume-everything caps (tests, and the fallback if probing fails entirely).</summary>
    public static readonly FfmpegCaps All = new();

    public bool HasFilter(string f) => Filters == null || Filters.Contains(f);
    public bool HasEncoder(string e) => Encoders == null || Encoders.Contains(e);
    public bool EncoderHas(string enc, string opt) => EncoderOptions == null || !EncoderOptions.TryGetValue(enc, out var s) || s.Contains(opt);

    static readonly ConcurrentDictionary<string, FfmpegCaps> Cache = new();

    public static FfmpegCaps Probe(string ffmpeg)
    {
        string key;
        try { var fi = new FileInfo(ffmpeg); key = fi.Exists ? $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}" : ffmpeg; } catch { key = ffmpeg; }
        return Cache.GetOrAdd(key, _ => DoProbe(ffmpeg));
    }
    public static void ClearCache() => Cache.Clear();

    static (int code, string text) Run(string ffmpeg, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!; p.StandardInput.Close();
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-999, ""); }
            p.WaitForExit();
            return (p.ExitCode, o.Result + "\n" + e.Result);
        }
        catch (Exception ex) { return (-998, ex.Message); }
    }

    /// <summary>True if a 1-frame null run with these extra input-side options succeeds.</summary>
    static bool InputOptionWorks(string ffmpeg, params string[] opts)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        a.AddRange(opts); a.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "0.05", "-f", "null", "-" });
        return Run(ffmpeg, 10000, a.ToArray()).code == 0;
    }

    static HashSet<string> Names(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();

    static FfmpegCaps DoProbe(string ffmpeg)
    {
        var (vc, vt) = Run(ffmpeg, 10000, "-hide_banner", "-version");
        if (vc != 0) return All;   // can't even run it: emit the classic command line and let the real error surface
        var version = vt.Split('\n')[0].Trim();
        var full = Run(ffmpeg, 15000, "-hide_banner", "-h", "full").text;
        bool helpOk = full.Length > 1000;
        var image2 = Run(ffmpeg, 10000, "-hide_banner", "-h", "muxer=image2").text;
        var flv = Run(ffmpeg, 10000, "-hide_banner", "-h", "muxer=flv").text;
        var dshow = Run(ffmpeg, 10000, "-hide_banner", "-h", "demuxer=dshow").text;
        var http = Run(ffmpeg, 10000, "-hide_banner", "-h", "protocol=http").text;
        bool httpOk = http.Contains("reconnect");
        var filters = Names(Run(ffmpeg, 10000, "-hide_banner", "-filters").text, @"^\s*[A-Z.|]{2,4}\s+(\w+)\s");
        var encoders = Names(Run(ffmpeg, 10000, "-hide_banner", "-encoders").text, @"^\s*[VASFXBD.]{6}\s+(\w+)\s");
        var encOpts = new Dictionary<string, HashSet<string>>();
        foreach (var enc in new[] { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" })
            if (encoders.Contains(enc))
                encOpts[enc] = Names(Run(ffmpeg, 10000, "-hide_banner", "-h", "encoder=" + enc).text, @"^\s+-([\w:]+)\s");
        var c = new FfmpegCaps
        {
            Version = version,
            InputThreadQueue = InputOptionWorks(ffmpeg, "-thread_queue_size", "64"),
            ReinitFilter = !helpOk || full.Contains("-reinit_filter"),
            StatsPeriod = !helpOk || full.Contains("-stats_period"),
            AtomicWriting = !image2.Contains("image2") || image2.Contains("atomic_writing"),
            FlvFlags = !flv.Contains("flv") || flv.Contains("flvflags"),
            DshowBufferSize = !dshow.Contains("dshow") || dshow.Contains("audio_buffer_size"),
            HttpReconnect = !httpOk || http.Contains("reconnect_streamed"),
            HttpReconnectDelayMax = !httpOk || http.Contains("reconnect_delay_max"),
            RwTimeout = !helpOk || full.Contains("rw_timeout") || http.Contains("rw_timeout"),
            Filters = filters.Count > 50 ? filters : null,
            Encoders = encoders.Count > 10 ? encoders : null,
            EncoderOptions = encOpts.Count > 0 ? encOpts : null,
        };
        if (!c.InputThreadQueue) c.Notes.Add("-thread_queue_size is not accepted on inputs (omitted)");
        if (!c.ReinitFilter) c.Notes.Add("-reinit_filter not supported (omitted)");
        if (!c.StatsPeriod) c.Notes.Add("-stats_period not supported (omitted)");
        if (!c.AtomicWriting) c.Notes.Add("image2 atomic_writing not supported (omitted)");
        if (!c.FlvFlags) c.Notes.Add("flvflags not supported (omitted)");
        foreach (var f in new[] { "azmq", "loudnorm", "dynaudnorm", "sidechaincompress", "agate", "drawtext", "xfade", "acrossfade" })
            if (!c.HasFilter(f)) c.Notes.Add($"filter '{f}' missing");
        return c;
    }

    public string Describe() => (Version.Length > 0 ? Version : "ffmpeg") + (Notes.Count > 0 ? " — " + string.Join("; ", Notes) : "");
}
