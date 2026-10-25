using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace EpikStreamer.Core;

public record StreamStats(double Fps, double BitrateKbps, double Speed, long Frames, long Dropped, long Duplicated,
                          TimeSpan Uptime, int Reconnects, int ItemIndex, int ItemCount, string ItemName, double ItemPosition);

public enum DestState { Connecting, Live, Failed, Stopped }

/// <summary>
/// v2.1 engine. One long-lived encoder ffmpeg owns every output (tee: N RTMP destinations + optional MKV recording).
/// The playlist is cut into "segments" (a body of one item, or an A→B crossfade transition) that short-lived
/// feeder ffmpegs render, unthrottled, to MPEG-TS on a single continuous timeline. The engine pipes those bytes through a
/// small bounded queue into the encoder (which paces with -re), so there is no gap between items and the encoder's
/// timestamps never reset. If the encoder dies it is restarted with backoff and playback resumes where the viewers left off.
/// </summary>
public sealed class StreamEngine
{
    public event Action<string>? Log;
    public event Action<StreamStats>? Stats;
    public event Action<string>? State;
    public event Action<int, DestState, string>? DestinationStatus;   // index into Destinations
    public event Action? Finished;

    readonly Profile _p; readonly string _ffmpeg; readonly string? _ffprobe;
    readonly List<CommandBuilder.Destination> _netDests; readonly List<string> _secrets;
    public IReadOnlyList<CommandBuilder.Destination> Destinations => _netDests;
    public List<string> Recordings { get; } = new();
    public string WorkDir { get; }
    public string PreviewPath => Path.Combine(WorkDir, CommandBuilder.PreviewFile);
    public bool IsRunning { get; private set; }
    /// <summary>Bytes of rendered MPEG-TS buffered ahead of the encoder (~2-4 s at 720p).</summary>
    public int QueueChunks { get; set; } = 64;

    // ---- v2.2 live features ----
    /// <summary>Draw chat-command responses and alerts on the stream (needs a font).</summary>
    public bool TextOverlays { get; set; }
    /// <summary>Allow runtime control (mute / overlay toggles) through ffmpeg's azmq filter when the build has it.</summary>
    public bool RuntimeControl { get; set; } = true;
    public bool MusicMuted { get; private set; }
    public bool MicMuted { get; private set; }
    public bool OverlaysHidden { get; private set; }
    public string CurrentMusic { get; private set; } = "";
    public bool ControlAvailable => _ctl != null;
    StreamControl? _ctl; int _musicGen;
    readonly object _txtLk = new();
    System.Threading.Timer? _alertTimer, _chatTimer;

    volatile bool _stop, _skip;
    Process? _enc, _feed;
    readonly object _lk = new();
    int _reconnects, _recPart; Stopwatch _uptime = new();
    DateTime _lastProgress;
    Thread? _thread;
    readonly Dictionary<string, MediaInfo> _probeCache = new();

    // playlist cursor (pump side)
    List<string> _order = new(); int _idx; double _pos;
    double _tl;                     // timeline position (s) where the next segment starts
    double _encTl0; double _outTime; // timeline at which the current encoder started, its progress
    readonly List<SegInfo> _segs = new();
    SegInfo? _pumpSeg;
    BlockingCollection<byte[]>? _q; CancellationTokenSource? _cts;
    readonly Dictionary<int, int> _cc = new();
    readonly FileStream? _dump = Environment.GetEnvironmentVariable("EPIK_DUMP_TS") is { Length: > 0 } d ? File.Create(d) : null; // debug only   // MPEG-TS continuity counters (rewritten so concatenation is clean)

    record SegInfo(double Tl0, List<string> Order, int Idx, string Item, double SrcStart, bool Transition, int NextIdx, List<string> NextOrder, double X)
    { public double Tl1 = double.MaxValue; }

    public StreamEngine(Profile profile, string ffmpeg, IEnumerable<CommandBuilder.Destination> destinations, IEnumerable<string> secretsForLogMasking, string? workDir = null)
    {
        _p = profile; _ffmpeg = ffmpeg; _ffprobe = FfmpegLocator.FindProbe(ffmpeg);
        _netDests = destinations.ToList(); _secrets = secretsForLogMasking.Where(s => !string.IsNullOrEmpty(s)).ToList();
        WorkDir = workDir ?? Path.Combine(FfmpegLocator.LocalAppDataDir, "session");
        Directory.CreateDirectory(WorkDir);
    }
    /// <summary>v2.0-compatible single-output constructor.</summary>
    public StreamEngine(Profile profile, string ffmpeg, string outputUrl, string? secretForLogMasking, string? workDir = null)
        : this(profile, ffmpeg, new[] { new CommandBuilder.Destination("Output", outputUrl) }, secretForLogMasking == null ? Array.Empty<string>() : new[] { secretForLogMasking }, workDir) { }

    public void Start()
    {
        if (IsRunning) return;
        if (_p.Playlist.Count == 0) throw new InvalidOperationException("Playlist is empty.");
        if (_netDests.Count == 0 && !_p.RecordEnabled) throw new InvalidOperationException("No enabled output and recording is off.");
        PrepareOverlayFiles();
        _stop = false; IsRunning = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "EpikStreamer engine" };
        _thread.Start();
    }

    /// <summary>Jump to the next item (hard cut). Note: up to a few seconds of already-rendered video still plays first.</summary>
    public void Skip()
    {
        var onAir = OnAir(); var ps = _pumpSeg;
        if (onAir != null && ps != null && !ReferenceEquals(onAir, ps) && !(ps.Transition && onAir.Item == ps.Item && onAir.Idx == ps.Idx))
        { Emit("Next item is already starting."); return; }
        _skip = true; KillFeeder();
    }
    public void Stop() { _stop = true; _cts?.Cancel(); KillFeeder(); Interlocked.Increment(ref _musicGen); }

    // ---- runtime control (v2.2) ----
    bool MusicActive => _p.Audio.MusicEnabled && _p.Audio.MusicPlaylist.Any(File.Exists);
    bool MicActive => _p.Audio.MicEnabled && !string.IsNullOrWhiteSpace(_p.Audio.MicDevice);
    public bool HasMusic => MusicActive;
    public bool HasMic => MicActive;

    /// <summary>True if this ffmpeg build has the azmq filter (BtbN/gyan builds do).</summary>
    public static bool SupportsAzmq(string ffmpeg) => FfmpegCaps.Probe(ffmpeg).HasFilter("azmq");

    bool Ctl(string target, string cmd, string arg)
    {
        var c = _ctl; if (c == null) { Emit("Runtime control is unavailable with this ffmpeg build (needs azmq)."); return false; }
        var r = c.Send(target, cmd, arg);
        if (r == null || !r.StartsWith("0 ")) { Emit($"Control '{target} {cmd} {arg}' failed: {r ?? "no reply"}"); return false; }
        return true;
    }
    public bool SetMusicMuted(bool m) { MusicMuted = m; return !MusicActive || !IsRunning || Ctl("volume@music", "volume", m ? "0" : Inv(_p.Audio.MusicVolume)); }
    public bool SetMicMuted(bool m) { MicMuted = m; return !MicActive || !IsRunning || Ctl("volume@mic", "volume", m ? "0" : Inv(_p.Audio.MicVolume)); }
    public bool SetMusicVolume(double v) { _p.Audio.MusicVolume = v; return MusicMuted || !MusicActive || !IsRunning || Ctl("volume@music", "volume", Inv(v)); }
    public bool SetMicVolume(double v) { _p.Audio.MicVolume = v; return MicMuted || !MicActive || !IsRunning || Ctl("volume@mic", "volume", Inv(v)); }
    /// <summary>Hide/show logo, text and clock overlays (alerts/chat responses stay visible).</summary>
    public bool SetOverlaysHidden(bool h)
    {
        OverlaysHidden = h; if (!IsRunning) return true; bool ok = true; var o = _p.Overlays;
        if (o.ImageEnabled && File.Exists(o.ImagePath)) ok &= Ctl("colorchannelmixer@logo", "aa", h ? "0" : Inv(Math.Clamp(o.ImageOpacity, 0, 1)));
        if (o.TextEnabled && !string.IsNullOrWhiteSpace(o.Text)) ok &= Ctl("drawtext@text", "enable", h ? "0" : "1");
        if (o.ClockEnabled) ok &= Ctl("drawtext@clock", "enable", h ? "0" : "1");
        return ok;
    }
    static string Inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Shows an alert banner on stream for <paramref name="seconds"/> (drawtext reload=1 picks it up next frame).</summary>
    public void ShowAlert(string text, double seconds) => ShowText(CommandBuilder.AlertFile, text, seconds, ref _alertTimer);
    public void ShowChatResponse(string text, double seconds) => ShowText(CommandBuilder.ChatFile, text, seconds, ref _chatTimer);
    void ShowText(string file, string text, double seconds, ref System.Threading.Timer? timer)
    {
        if (!TextOverlays) return;
        WriteOverlay(file, text);
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => WriteOverlay(file, ""), null, TimeSpan.FromSeconds(Math.Max(1, seconds)), Timeout.InfiniteTimeSpan);
    }
    /// <summary>Atomic replace so drawtext never reads a half-written file (it keeps the old text if a read fails).</summary>
    public void WriteOverlay(string file, string text)
    {
        lock (_txtLk)
        {
            var path = Path.Combine(WorkDir, file); var tmp = path + ".tmp";
            text = (text ?? "").Replace("\r", "");
            for (int i = 0; i < 5; i++)
            {
                try { File.WriteAllText(tmp, text, new UTF8Encoding(false)); File.Move(tmp, path, true); return; }
                catch (IOException) { Thread.Sleep(20); }
                catch (UnauthorizedAccessException) { Thread.Sleep(20); }
            }
        }
    }

    // ---- background music pump (v2.2) ----
    void StartMusic(int port)
    {
        int gen = Interlocked.Increment(ref _musicGen);
        new Thread(() => MusicLoop(port, gen)) { IsBackground = true, Name = "music pump" }.Start();
    }
    void MusicLoop(int port, int gen)
    {
        System.Net.Sockets.TcpClient? tc = null;
        for (int i = 0; i < 200 && gen == _musicGen && !_stop; i++)
        {
            try { tc = new System.Net.Sockets.TcpClient(); tc.Connect(System.Net.IPAddress.Loopback, port); break; }
            catch { tc?.Dispose(); tc = null; Thread.Sleep(100); }
        }
        if (tc == null) { if (gen == _musicGen && !_stop) Emit("♪ Could not connect the music feed to the encoder."); return; }
        using var _tc = tc; var net = tc.GetStream(); var buf = new byte[1 << 16];
        var files = _p.Audio.MusicPlaylist.Where(File.Exists).ToList(); int k = 0; int fails = 0;
        try
        {
            while (gen == _musicGen && !_stop && files.Count > 0)
            {
                if (k % files.Count == 0 && _p.Audio.MusicShuffle) files = files.OrderBy(_ => Random.Shared.Next()).ToList();
                var f = files[k++ % files.Count];
                CurrentMusic = Path.GetFileNameWithoutExtension(f);
                var psi = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in CommandBuilder.MusicFeederArgs(f)) psi.ArgumentList.Add(a);
                using var pr = Process.Start(psi)!;
                pr.ErrorDataReceived += (_, e) => { if (e.Data != null) Emit("[music] " + e.Data); }; pr.BeginErrorReadLine();
                long total = 0; int n;
                try
                {
                    var so = pr.StandardOutput.BaseStream;
                    while (gen == _musicGen && !_stop && (n = so.Read(buf, 0, buf.Length)) > 0) { net.Write(buf, 0, n); total += n; }
                }
                finally { try { if (!pr.HasExited) pr.Kill(true); } catch { } }
                if (total == 0) { if (++fails >= files.Count) { Emit("♪ No playable music files."); break; } } else fails = 0;
            }
        }
        catch (Exception) { /* encoder went away; the next encoder start begins a new pump */ }
        CurrentMusic = "";
    }

    public bool Wait(int ms) => _thread?.Join(ms) ?? true;

    /// <summary>Test hook: kill the encoder to simulate a dropped connection.</summary>
    public void SimulateDrop() { try { _enc?.Kill(true); } catch { } }

    void Emit(string s) => Log?.Invoke(CommandBuilder.Mask(s, _secrets));

    void PrepareOverlayFiles()
    {
        var o = _p.Overlays;
        if (o.TextEnabled || o.ClockEnabled)
        {
            var font = string.IsNullOrWhiteSpace(o.FontFile) ? CommandBuilder.DefaultFont() : o.FontFile;
            if (!File.Exists(font)) throw new FileNotFoundException("Font file not found: " + font);
            File.Copy(font, Path.Combine(WorkDir, CommandBuilder.FontName), true); // referenced relatively => no path escaping issues
        }
        var utf8 = new UTF8Encoding(false);
        if (TextOverlays && !(o.TextEnabled || o.ClockEnabled))
        {
            var font = string.IsNullOrWhiteSpace(o.FontFile) ? CommandBuilder.DefaultFont() : o.FontFile;
            if (File.Exists(font)) File.Copy(font, Path.Combine(WorkDir, CommandBuilder.FontName), true);
            else { Emit("No font for on-stream alerts/chat responses; they're disabled. " + font); TextOverlays = false; }
        }
        File.WriteAllText(Path.Combine(WorkDir, CommandBuilder.TextFile), o.Text ?? "", utf8);
        File.WriteAllText(Path.Combine(WorkDir, CommandBuilder.AlertFile), "", utf8);
        File.WriteAllText(Path.Combine(WorkDir, CommandBuilder.ChatFile), "", utf8);
        var fmt = string.IsNullOrWhiteSpace(o.ClockFormat) ? "" : ":" + o.ClockFormat;
        File.WriteAllText(Path.Combine(WorkDir, CommandBuilder.ClockFile), "%{localtime" + fmt + "}", utf8);
        try { File.Delete(PreviewPath); } catch { }
    }

    void NewOrder()
    {
        _order = new List<string>(_p.Playlist);
        if (_p.Shuffle) { var r = Random.Shared; for (int i = _order.Count - 1; i > 0; i--) { int j = r.Next(i + 1); (_order[i], _order[j]) = (_order[j], _order[i]); } }
    }

    void Run()
    {
        _uptime = Stopwatch.StartNew(); int attempt = 0;
        NewOrder(); _idx = 0; _pos = 0; _tl = 0;
        try
        {
            while (!_stop)
            {
                var encStart = DateTime.UtcNow;
                try { StartEncoder(); }
                catch (Exception ex) { Emit("Failed to start ffmpeg: " + ex.Message); State?.Invoke("Error: " + ex.Message); break; }
                State?.Invoke(_reconnects > 0 ? $"Live (reconnected {_reconnects}x)" : "Live");
                bool finished = PumpPlaylist();
                if (_stop || finished)
                {
                    if (finished && !_stop) DrainAndClose(); else StopEncoderGracefully();
                    State?.Invoke(finished && !_stop ? "Playlist finished" : "Stopped");
                    break;
                }
                // encoder died unexpectedly (connection dropped, server closed, every destination failed, ...)
                KillFeeder();
                try { _enc?.WaitForExit(3000); Emit($"Encoder exited unexpectedly (code {SafeExit(_enc)})."); } catch { }
                int? code = null; try { if (_enc is { HasExited: true }) code = _enc.ExitCode; } catch { }
                if (!_stop && IsFatalOptionError(code, _encProgress, _encFatal))
                {
                    var line = CommandBuilder.Mask(_encFatal!, _secrets);
                    Emit("✖ ffmpeg rejected the command line, not retrying: " + line);
                    Emit("  Your ffmpeg: " + FfmpegCaps.Probe(_ffmpeg).Describe() + ". Try the bundled ffmpeg (Output tab → Download ffmpeg) or report this line.");
                    State?.Invoke("Error: ffmpeg rejected the command — " + line);
                    break;
                }
                ComputeResume();
                if ((DateTime.UtcNow - encStart).TotalSeconds > 60) attempt = 0;
                attempt++;
                if (attempt > _p.MaxRetries) { State?.Invoke($"Failed after {_p.MaxRetries} retries"); Emit("Giving up."); break; }
                _reconnects++;
                int delay = Math.Min(60, 2 << Math.Min(attempt - 1, 5)); // 2,4,8,16,32,60s
                Emit($"Reconnecting in {delay}s (attempt {attempt}/{_p.MaxRetries}), resuming item {_idx + 1} at {_pos:0}s…");
                for (int i = 0; i < delay * 10 && !_stop; i++)
                {
                    if (i % 10 == 0) State?.Invoke($"Reconnecting in {delay - i / 10}s (attempt {attempt}/{_p.MaxRetries})");
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex) { Emit("Engine error: " + ex); State?.Invoke("Error: " + ex.Message); }
        finally
        {
            KillFeeder(); Interlocked.Increment(ref _musicGen); try { if (_enc is { HasExited: false }) _enc.Kill(true); } catch { }
            _ctl?.Dispose(); _ctl = null; _alertTimer?.Dispose(); _chatTimer?.Dispose();
            for (int i = 0; i < _netDests.Count; i++) DestinationStatus?.Invoke(i, DestState.Stopped, "");
            RemuxRecordings();
            IsRunning = false; Finished?.Invoke();
        }
    }

    bool _capsLogged;
    volatile bool _encProgress; volatile string? _encFatal;
    /// <summary>ffmpeg lines that mean the command line itself is wrong (retrying can't help).</summary>
    public static readonly Regex OptionError = new(
        @"Error parsing options|cannot be applied to input url|cannot be applied to output url|Unrecognized option|Option not found|Error splitting the argument list|" +
        @"Error parsing (a )?filter|No such filter|Error initializing (complex )?filters?|Error binding filtergraph|Invalid argument.*filter|" +
        @"Error reinitializing filters|Unknown encoder|Undefined constant or missing|Option .* not found|Error applying option|Error opening input files: Invalid argument|Unknown input format",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>True if the encoder died before producing any output with a command-line/option error (exit -22 = EINVAL).</summary>
    public static bool IsFatalOptionError(int? exitCode, bool hadProgress, string? errLine) =>
        !hadProgress && errLine != null && exitCode != 0;

    static string SafeExit(Process? p) { try { return p?.ExitCode.ToString() ?? "?"; } catch { return "?"; } }

    static readonly Regex SlaveFailed = new(@"Slave muxer #(\d+) failed: (.*?)(, continuing with|$)", RegexOptions.Compiled);

    void StartEncoder()
    {
        bool hasLogo = _p.Overlays.ImageEnabled && File.Exists(_p.Overlays.ImagePath);
        var dests = new List<CommandBuilder.Destination>(_netDests);
        if (_p.RecordEnabled)
        {
            var path = CommandBuilder.RecordingPath(_p, ++_recPart, DateTime.Now);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            dests.Add(new CommandBuilder.Destination("Recording", path, true));
            Recordings.Add(path); Emit("● Recording to " + path);
        }
        var a = _p.Audio;
        var caps = FfmpegCaps.Probe(_ffmpeg);
        if (!_capsLogged)
        {
            _capsLogged = true; Emit("ffmpeg: " + caps.Describe());
            if (RuntimeControl && !caps.HasFilter("azmq")) Emit("⚠ This ffmpeg build has no azmq filter: live mute/volume/overlay toggles are disabled (they apply on the next start).");
            if (a.Normalize != "Off" && !caps.HasFilter(a.Normalize.ToLowerInvariant())) Emit($"⚠ {a.Normalize} filter missing in this ffmpeg build: normalization disabled.");
        }
        int ctlPort = RuntimeControl && caps.HasFilter("azmq") ? StreamControl.FreePort() : 0;
        int musicPort = MusicActive ? StreamControl.FreePort() : 0;
        var lo = new CommandBuilder.LiveOptions(ctlPort, musicPort, MicActive ? a.MicDevice : null, TextOverlays,
            MusicMuted ? 0 : a.MusicVolume, MicMuted ? 0 : a.MicVolume, a.MainVolume);
        _ctl?.Dispose(); _ctl = ctlPort > 0 ? new StreamControl(ctlPort) : null;
        var args = CommandBuilder.EncoderArgs(_p, dests, hasLogo, lo, caps);
        Emit("> ffmpeg " + CommandBuilder.Display(args, _secrets));
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = WorkDir,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var x in args) psi.ArgumentList.Add(x);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var kv = new Dictionary<string, string>();
        var failed = new HashSet<int>(); bool anyLive = false;
        _encProgress = false; _encFatal = null;
        for (int i = 0; i < _netDests.Count; i++) DestinationStatus?.Invoke(i, DestState.Connecting, "");
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            int i = e.Data.IndexOf('='); if (i <= 0) return;
            var k = e.Data[..i]; var v = e.Data[(i + 1)..].Trim();
            kv[k] = v;
            if (k == "progress")
            {
                _lastProgress = DateTime.UtcNow; _encProgress = true;
                _outTime = D(kv, "out_time_us") / 1e6;
                if (!anyLive && _outTime > 0.5)
                {   // the muxer is writing => every destination that hasn't failed is connected
                    anyLive = true;
                    for (int d = 0; d < _netDests.Count; d++) if (!failed.Contains(d)) DestinationStatus?.Invoke(d, DestState.Live, "");
                }
                PublishStats(kv);
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Emit(e.Data);
            if (!_encProgress && _encFatal == null && OptionError.IsMatch(e.Data)) _encFatal = e.Data.Trim();
            var m = SlaveFailed.Match(e.Data);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var idx))
            {
                failed.Add(idx);
                if (idx < _netDests.Count) DestinationStatus?.Invoke(idx, DestState.Failed, CommandBuilder.Mask(m.Groups[2].Value, _secrets));
                else Emit("⚠ Recording output failed: " + m.Groups[2].Value);
                // if every network destination failed, restart the encoder (=> reconnect with backoff); the recording alone doesn't count
                if (_netDests.Count > 0 && Enumerable.Range(0, _netDests.Count).All(failed.Contains))
                { Emit("All destinations failed; reconnecting."); try { p.Kill(true); } catch { } }
            }
        };
        p.Exited += (_, _) => { _cts?.Cancel(); KillFeeder(); }; // unblocks the pump so we can reconnect
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        if (musicPort > 0) StartMusic(musicPort);
        lock (_lk) _enc = p;
        _lastProgress = DateTime.UtcNow; _outTime = 0; _encTl0 = _tl;

        // bounded queue + writer thread: decouples feeders (fast) from the encoder (real time)
        _cts = new CancellationTokenSource();
        var q = new BlockingCollection<byte[]>(QueueChunks); _q = q;
        var stdin = p.StandardInput.BaseStream; var ct = _cts.Token; var cts = _cts;
        new Thread(() =>
        {
            try { foreach (var chunk in q.GetConsumingEnumerable(ct)) stdin.Write(chunk, 0, chunk.Length); stdin.Flush(); }
            catch (Exception) { }
            if (q.IsAddingCompleted && !ct.IsCancellationRequested) { try { stdin.Close(); } catch { } }
            else cts.Cancel();
        }) { IsBackground = true, Name = "encoder writer" }.Start();
        new Thread(() => Watchdog(p)) { IsBackground = true }.Start();
    }

    void Watchdog(Process p)
    {
        while (!p.HasExited && !_stop)
        {
            Thread.Sleep(1000);
            if (!p.HasExited && (DateTime.UtcNow - _lastProgress).TotalSeconds > 25)
            {
                Emit("No progress from encoder for 25s, restarting it.");
                try { p.Kill(true); } catch { }
                return;
            }
        }
    }

    static double D(Dictionary<string, string> kv, string k)
    {
        if (!kv.TryGetValue(k, out var v)) return 0;
        v = v.Replace("kbits/s", "").Replace("x", "").Trim();
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    SegInfo? OnAir()
    {
        double t = _encTl0 + _outTime;
        lock (_segs) { for (int i = _segs.Count - 1; i >= 0; i--) if (_segs[i].Tl0 <= t + 1e-6) return _segs[i]; }
        return null;
    }

    void PublishStats(Dictionary<string, string> kv)
    {
        var s = OnAir(); double t = _encTl0 + _outTime;
        string name = ""; int idx = 0, count = _p.Playlist.Count; double pos = 0;
        if (s != null)
        {
            idx = s.Idx; count = s.Order.Count; name = Path.GetFileName(s.Item); pos = s.SrcStart + (t - s.Tl0);
            if (s.Transition) name += " → " + Path.GetFileName(s.NextOrder[s.NextIdx]);
        }
        Stats?.Invoke(new StreamStats(D(kv, "fps"), D(kv, "bitrate"), D(kv, "speed"), (long)D(kv, "frame"),
            (long)D(kv, "drop_frames"), (long)D(kv, "dup_frames"), _uptime.Elapsed, _reconnects, idx, count, name, pos));
    }

    /// <summary>After an encoder death: continue from what viewers last saw (minus 2 s), not from where the pump got to.</summary>
    void ComputeResume()
    {
        var s = OnAir(); if (s == null) return;
        double t = _encTl0 + _outTime;
        if (s.Transition) { _order = s.NextOrder; _idx = s.NextIdx; _pos = 0; }
        else { _order = s.Order; _idx = s.Idx; _pos = Math.Max(0, s.SrcStart + (t - s.Tl0) - 2); }
        lock (_segs) _segs.RemoveAll(x => x.Tl0 > t);
    }

    MediaInfo Probe(string file)
    {
        if (_probeCache.TryGetValue(file, out var c)) return c;
        var info = new MediaInfo(0, true, true);
        if (_ffprobe != null)
        {
            try
            {
                var psi = new ProcessStartInfo(_ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", file }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!;
                var json = p.StandardOutput.ReadToEnd(); p.WaitForExit(10000);
                using var doc = JsonDocument.Parse(json);
                double dur = 0; bool aud = false, vid = false;
                if (doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d))
                    double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
                if (doc.RootElement.TryGetProperty("streams", out var ss))
                    foreach (var s in ss.EnumerateArray())
                    {
                        var t = s.GetProperty("codec_type").GetString();
                        if (t == "audio") aud = true; else if (t == "video") vid = true;
                    }
                info = new MediaInfo(dur, aud, vid);
            }
            catch (Exception ex) { Emit("ffprobe failed for " + file + ": " + ex.Message); }
        }
        _probeCache[file] = info;
        return info;
    }

    (List<string> order, int idx)? PeekNext(List<string> order, int idx)
    {
        if (idx + 1 < order.Count) return (order, idx + 1);
        if (!_p.LoopPlaylist) return null;
        var saveO = _order; NewOrder(); var n = _order; _order = saveO;
        if (n.Count > 1 && n[0] == order[idx]) (n[0], n[1]) = (n[1], n[0]); // shuffle: don't repeat the same item back-to-back
        return (n, 0);
    }

    bool Playable(string item, out MediaInfo info)
    {
        info = new MediaInfo(0, false, false);
        if (!File.Exists(item)) { Emit("Missing file, skipping: " + item); return false; }
        info = Probe(item);
        if (!info.HasVideo) { Emit("No video stream, skipping: " + item); return false; }
        return true;
    }

    double Frames(double sec) => Math.Floor(sec * _p.Fps + 1e-6) / _p.Fps;  // snap to whole frames (keeps A/V lengths exact)

    /// <summary>Returns true if the playlist ended (no loop), false if stopped or the encoder died.</summary>
    bool PumpPlaylist()
    {
        int failsInRow = 0;
        while (!_stop)
        {
            if (_idx >= _order.Count)
            {
                if (!_p.LoopPlaylist) return true;
                NewOrder(); _idx = 0; _pos = 0;
            }
            var item = _order[_idx];
            if (!Playable(item, out var info)) { _idx++; _pos = 0; if (++failsInRow >= _order.Count) return true; continue; }
            double dA = info.Duration;
            if (dA > 0 && _pos >= dA - 0.5) _pos = 0;

            // plan the transition into the next playable item
            var nx = PeekNext(_order, _idx); MediaInfo infoB = new(0, false, false);
            while (nx != null && !Playable(nx.Value.order[nx.Value.idx], out infoB))
            { var n2 = PeekNext(nx.Value.order, nx.Value.idx); if (n2 == null || ReferenceEquals(n2.Value.order, nx.Value.order) == false && n2.Value.idx == 0 && nx.Value.idx == 0) { nx = n2; break; } nx = n2; }
            double X = 0;
            if (_p.CrossfadeSeconds > 0 && nx != null && dA > 0 && infoB.Duration > 0)
            {
                X = Math.Min(Math.Min(_p.CrossfadeSeconds, 5), Math.Min((dA - _pos) / 2, infoB.Duration / 2));
                X = Frames(X); if (X < 2.0 / _p.Fps) X = 0;
            }
            double? bodyLen = dA > 0 ? Frames(dA - _pos - X) : null;

            // --- body of the current item ---
            _skip = false;
            if (bodyLen is null || bodyLen >= 1.0 / _p.Fps)
            {
                var seg = new SegInfo(_tl, _order, _idx, item, _pos, false, 0, _order, 0);
                Emit($"▶ [{_idx + 1}/{_order.Count}] {Path.GetFileName(item)}" + (_pos > 0 ? $" from {_pos:0.#}s" : ""));
                var r = RunSegment(seg, CommandBuilder.SegmentArgs(_p, new(item, info, _pos, bodyLen), null, 0, _p.Transition, _tl), bodyLen);
                if (r == SegResult.EncoderGone) return false;
                if (r == SegResult.Failed) { _idx++; _pos = 0; if (++failsInRow >= _order.Count) return true; continue; }
                failsInRow = 0;
                if (r == SegResult.Skipped) { _skip = false; _idx++; _pos = 0; continue; }   // hard cut to the next item
            }
            if (nx == null) { _idx = _order.Count; continue; }

            // --- transition A tail -> B head ---
            if (X > 0)
            {
                var (bo, bi) = nx.Value; var bItem = bo[bi];
                var seg = new SegInfo(_tl, _order, _idx, item, dA - X, true, bi, bo, X);
                Emit($"⤳ {_p.Transition} {X:0.##}s → {Path.GetFileName(bItem)}");
                var r = RunSegment(seg, CommandBuilder.SegmentArgs(_p, new(item, info, dA - X, X), new(bItem, infoB, 0, X), X, _p.Transition, _tl), X);
                if (r == SegResult.EncoderGone) return false;
                _skip = false;
                _order = bo; _idx = bi; _pos = r == SegResult.Completed ? X : 0;
                if (r == SegResult.Failed) _pos = 0;
            }
            else { var (bo, bi) = nx.Value; _order = bo; _idx = bi; _pos = 0; }
        }
        return false;
    }

    enum SegResult { Completed, Skipped, Failed, EncoderGone }

    SegResult RunSegment(SegInfo seg, List<string> args, double? exactLen)
    {
        lock (_segs) { _segs.Add(seg); if (_segs.Count > 500) _segs.RemoveRange(0, 100); }
        _pumpSeg = seg;
        var psi = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = WorkDir };
        foreach (var x in args) psi.ArgumentList.Add(x);
        var feed = new Process { StartInfo = psi };
        feed.ErrorDataReceived += (_, e) => { if (e.Data != null) Emit("[source] " + e.Data); };
        feed.Start(); feed.BeginErrorReadLine();
        lock (_lk) _feed = feed;
        if (_skip) KillFeeder();

        var pts = new TsTimeline(_cc);
        var src = feed.StandardOutput.BaseStream; var q = _q!; var ct = _cts!.Token;
        var buf = new byte[188 * 348]; int have = 0; bool gone = false;
        try
        {
            int n;
            while ((n = src.Read(buf, have, buf.Length - have)) > 0)
            {
                have += n;
                int whole = have / 188 * 188;
                if (whole == 0) continue;
                var chunk = new byte[whole]; Buffer.BlockCopy(buf, 0, chunk, 0, whole);
                pts.Process(chunk);
                if (_dump != null) lock (_dump) _dump.Write(chunk);
                q.Add(chunk, ct);
                Buffer.BlockCopy(buf, whole, buf, 0, have - whole); have -= whole; // keep the partial packet
            }
        }
        catch (OperationCanceledException) { gone = true; }
        catch (InvalidOperationException) { gone = true; }
        catch (IOException) { }
        feed.WaitForExit(3000);
        if (gone || (_enc?.HasExited ?? true) || _stop) { KillFeeder(); return SegResult.EncoderGone; }
        int code = feed.HasExited ? feed.ExitCode : -1;
        double end = pts.End(_p.Fps);
        bool skipped = _skip;
        if (!skipped && code == 0 && exactLen is double L) _tl = seg.Tl0 + L;          // exact, no drift
        else _tl = Math.Max(_tl, end > 0 ? end : _tl);                                // partial: continue right after what was sent
        seg.Tl1 = _tl;
        if (skipped) return SegResult.Skipped;
        if (code != 0 && pts.Packets == 0) { Emit($"Source failed (code {code}), skipping."); return SegResult.Failed; }
        return SegResult.Completed;
    }

    /// <summary>Tracks the last PTS seen per stream type and rewrites continuity counters so back-to-back feeder outputs form one valid TS.</summary>
    sealed class TsTimeline
    {
        readonly Dictionary<int, int> _cc; double _v = -1, _a = -1; public long Packets;
        public TsTimeline(Dictionary<int, int> cc) => _cc = cc;
        public void Process(byte[] b)
        {
            for (int i = 0; i + 188 <= b.Length; i += 188)
            {
                if (b[i] != 0x47) continue;
                Packets++;
                int pid = ((b[i + 1] & 0x1F) << 8) | b[i + 2];
                int afc = (b[i + 3] >> 4) & 3;
                if ((afc & 1) != 0 && pid != 0x1FFF)
                {
                    int c = _cc.TryGetValue(pid, out var prev) ? (prev + 1) & 0xF : (b[i + 3] & 0xF);
                    _cc[pid] = c; b[i + 3] = (byte)((b[i + 3] & 0xF0) | c);
                }
                if ((b[i + 1] & 0x40) == 0 || (afc & 1) == 0) continue;
                int o = i + 4 + ((afc & 2) != 0 ? 1 + b[i + 4] : 0);
                if (o + 14 > i + 188 || b[o] != 0 || b[o + 1] != 0 || b[o + 2] != 1) continue;
                int sid = b[o + 3];
                if (((b[o + 7] >> 6) & 2) == 0) continue;
                long p = ((long)((b[o + 9] >> 1) & 7) << 30) | ((long)b[o + 10] << 22) | ((long)(b[o + 11] >> 1) << 15) | ((long)b[o + 12] << 7) | ((long)b[o + 13] >> 1);
                double t = p / 90000.0;
                if (sid >= 0xE0 && sid <= 0xEF) _v = Math.Max(_v, t); else _a = Math.Max(_a, t);
            }
        }
        public double End(int fps) => Math.Max(_v >= 0 ? _v + 1.0 / fps : 0, _a >= 0 ? _a + 1024.0 / CommandBuilder.SegAudioRate : 0);
    }

    void KillFeeder()
    {
        Process? f; lock (_lk) f = _feed;
        try { if (f is { HasExited: false }) f.Kill(true); } catch { }
    }

    /// <summary>Playlist ended: let the queued video play out, then EOF the encoder so it closes the streams cleanly.</summary>
    void DrainAndClose()
    {
        var e = _enc; if (e == null) return;
        _q?.CompleteAdding();
        while (!e.HasExited && !_stop) e.WaitForExit(200);
        if (_stop) StopEncoderGracefully();
    }

    void StopEncoderGracefully()
    {
        KillFeeder();
        var e = _enc; if (e == null) return;
        _q?.CompleteAdding();
        _cts?.Cancel();                                // drop what's queued; stop now
        try { e.StandardInput.Close(); } catch { }   // EOF on stdin => ffmpeg flushes and closes outputs cleanly
        if (!e.WaitForExit(5000)) { Emit("Encoder did not exit in 5s, killing."); try { e.Kill(true); } catch { } }
    }

    void RemuxRecordings()
    {
        if (!_p.RemuxToMp4) return;
        foreach (var mkv in Recordings.ToList())
        {
            try
            {
                if (!File.Exists(mkv) || new FileInfo(mkv).Length < 1024) continue;
                var mp4 = Path.ChangeExtension(mkv, ".mp4");
                var psi = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var x in CommandBuilder.RemuxArgs(mkv, mp4)) psi.ArgumentList.Add(x);
                using var p = Process.Start(psi)!; var err = p.StandardError.ReadToEnd(); p.WaitForExit();
                if (p.ExitCode == 0 && File.Exists(mp4) && new FileInfo(mp4).Length > 0)
                { File.Delete(mkv); Recordings[Recordings.IndexOf(mkv)] = mp4; Emit("Recording saved: " + mp4); }
                else Emit("Remux to MP4 failed, keeping MKV: " + mkv + " " + err.Trim());
            }
            catch (Exception ex) { Emit("Remux failed: " + ex.Message); }
        }
    }
}
