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

    Profile _p; readonly string _ffmpeg; readonly string? _ffprobe;
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

    // ---- v2.3: song requests, track changes, now-playing card ----
    /// <summary>Request queue consulted at every track boundary (null = no requests).</summary>
    public SongRequestQueue? Requests { get; set; }
    /// <summary>Library lookup for titles/thumbnails (null = file names).</summary>
    public Func<string, LibraryItem?>? Library { get; set; }
    /// <summary>Fires (on a background thread) when a new item goes on air: path.</summary>
    public event Action<string>? TrackChanged;
    public NowPlayingController? Card => _np;
    NowPlayingController? _np; volatile string _onAir = "", _upNext = ""; int _trackNo; double _onAirPos;
    public string OnAirItem => _onAir;
    public string UpNextItem => _upNext;
    /// <summary>Changes whenever a new track starts (vote-skip votes are per track).</summary>
    public string TrackKey => _trackNo + "|" + _onAir;
    public string TitleOf(string path) => string.IsNullOrEmpty(path) ? "" : Library?.Invoke(path)?.Display is { Length: > 0 } t ? t : LibraryItem.TitleFromFile(path);
    public NowPlayingInfo? NowPlaying()
    {
        if (RadioActive && RadioInfo?.Invoke() is { } radio) return radio;   // v2.6: the radio track takes over the card
        var item = _onAir; if (item.Length == 0) return null;
        var lib = Library?.Invoke(item);
        double dur = lib?.Duration > 0 ? lib.Duration : (_probeCache.TryGetValue(item, out var mi) ? mi.Duration : 0);
        var req = Requests?.Playing; var by = req != null && string.Equals(req.Path, item, StringComparison.OrdinalIgnoreCase) ? req.User : "";
        var next = Requests?.PeekNext()?.Path ?? _upNext;
        return new NowPlayingInfo(TitleOf(item), by, TitleOf(next), _onAirPos, dur, lib?.Thumb ?? "");
    }

    /// <summary>Inserts the next approved request after position <paramref name="afterIdx"/> of the current order.</summary>
    bool InsertRequest(int afterIdx)
    {
        var rq = Requests; if (rq == null) return false;
        for (int guard = 0; guard < 20; guard++)
        {
            var r = rq.TakeNext(afterIdx + 1 >= _order.Count); if (r == null) return false;
            if (!File.Exists(r.Path)) { Emit("♪ Requested file is missing, skipping: " + r.Path); continue; }
            var n = new List<string>(_order); n.Insert(Math.Min(afterIdx + 1, n.Count), r.Path); _order = n;
            rq.Scheduled(r); Emit($"♪ Request from {r.User}: {r.Title} (plays next)");
            return true;
        }
        return false;
    }

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

    // ---- v2.6 Radio ----
    /// <summary>Resolved stream URLs (preference order) set by the host before Start; with Profile.Radio enabled a relay keeps the station connected for the whole run.</summary>
    public List<string>? RadioUrls { get; set; }
    /// <summary>Radio card info for the now-playing card (host-provided from the AzuraCast poller); null = use the playlist track.</summary>
    public Func<NowPlayingInfo?>? RadioInfo { get; set; }
    /// <summary>Called with each 20 ms chunk's loudness (0..1) — drives the Ball Pit beat pulse.</summary>
    public Action<float>? RadioLevel { get; set; }
    /// <summary>Test hook: rewrites the relay's input URL.</summary>
    public Func<string, string>? RadioUrlFilter { get; set; }
    public RadioRelay? Radio { get; private set; }
    public RadioFeed? RadioFeedNow { get; private set; }
    public bool RadioActive => Radio != null;
    public bool RadioMuted { get; private set; }
    public bool SetRadioMuted(bool m) { RadioMuted = m; return !RadioActive || !IsRunning || Ctl("volume@radio", "volume", m ? "0" : Inv(_p.Radio.Volume)); }
    public bool SetRadioVolume(double v) { _p.Radio.Volume = v; return RadioMuted || !RadioActive || !IsRunning || Ctl("volume@radio", "volume", Inv(v)); }
    /// <summary>Pops the now-playing card for a new radio song.</summary>
    public void RadioTrackChanged(string key) => _np?.TrackChanged("radio|" + key);
    void StartRadioFeed(int port)
    {
        RadioFeedNow?.Dispose();
        var f = new RadioFeed(Radio!, port); f.Log += Emit; f.OnLevel = RadioLevel; RadioFeedNow = f; f.Start();
    }
    void StopRadio() { RadioFeedNow?.Dispose(); RadioFeedNow = null; Radio?.Dispose(); Radio = null; }

    // ---- v2.5 Ball Pit ----
    /// <summary>The scene to stream (host-owned; also used for the in-app preview). Ignored when the profile's Ball Pit mode is Off.</summary>
    public BallPitScene? BallPit { get; set; }
    public BallPitFeed? PitFeed { get; private set; }
    int _pitGen;
    public bool PitActive => BallPit != null && _p.BallPit.Active;
    public const string FillerName = "Ball Pit.mkv";

    /// <summary>Source mode with an empty playlist: a silent, black 10-minute filler (looped) provides the audio timeline.</summary>
    string MakeFiller()
    {
        var f = Path.Combine(WorkDir, $"Ball Pit filler {_p.Fps}.mkv");
        if (File.Exists(f) && new FileInfo(f).Length > 1000) return f;
        var psi = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var x in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"color=c=0x101018:s=640x360:r={_p.Fps}:d=60,noise=alls=20:allf=t",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-t", "60", "-c:v", "libx264", "-preset", "ultrafast", "-b:v", "2000k", "-maxrate", "2000k", "-bufsize", "2000k", "-pix_fmt", "yuv420p", "-g", "60", "-c:a", "aac", "-b:a", "32k", "-ar", "48000", f + ".tmp.mkv" })
            psi.ArgumentList.Add(x);
        using var pr = Process.Start(psi)!; var err = pr.StandardError.ReadToEnd(); pr.WaitForExit(60000);
        if (pr.ExitCode != 0) throw new InvalidOperationException("Could not create the Ball Pit filler: " + err.Trim());
        File.Move(f + ".tmp.mkv", f, true);
        return f;
    }

    void StartPitFeed(int port, int w, int h, bool transparent)
    {
        int gen = Interlocked.Increment(ref _pitGen);
        _pitHalfRate |= PitFeed?.HalfRate == true;   // keep a degrade the previous feed chose by itself across encoder restarts
        var feed = new BallPitFeed(BallPit!, w, h, _p.Fps, transparent) { HalfRate = _pitHalfRate }; feed.Log += Emit; PitFeed = feed;
        if (_np != null) { feed.CardPath = Path.Combine(WorkDir, NowPlayingCard.CardFile); feed.CardCorner = _p.NowPlaying.Corner; feed.CardScale = (double)w / Math.Max(1, _p.Width); }
        new Thread(() => feed.Run(port, () => gen == _pitGen && !_stop)) { IsBackground = true, Name = "ball pit feed", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public void Start()
    {
        if (IsRunning) return;
        if (PitActive && _p.BallPit.Mode == "Source" && !_p.Playlist.Any(i => File.Exists(i) && Probe(i).HasVideo))   // empty, or only audio files
        {
            var filler = MakeFiller();
            var json = System.Text.Json.JsonSerializer.Serialize(_p, ProfileStore.Json);
            _p = System.Text.Json.JsonSerializer.Deserialize<Profile>(json, ProfileStore.Json)!;
            _p.Playlist = new() { filler }; _p.LoopPlaylist = true; _p.Shuffle = false; _p.CrossfadeSeconds = 0;
            Emit("● Ball Pit source: no video in the playlist, using a silent background (add music on the Audio tab).");
        }
        if (_p.Playlist.Count == 0) throw new InvalidOperationException("Playlist is empty.");
        if (_netDests.Count == 0 && !_p.RecordEnabled) throw new InvalidOperationException("No enabled output and recording is off.");
        PrepareOverlayFiles();
        StopRadio();
        if (_p.Radio.Active)
        {
            if (RadioUrls is { Count: > 0 } ru)
            {
                Radio = new RadioRelay(_ffmpeg, ru, FfmpegCaps.Probe(_ffmpeg)) { UrlFilter = RadioUrlFilter };
                Radio.Log += Emit; Radio.Start();
                Emit($"📻 Radio: {RadioRelayHost(ru[0])}{(ru.Count > 1 ? $" (+{ru.Count - 1} fallback)" : "")}, volume {Math.Round(_p.Radio.Volume * 100)}%.");
            }
            else Emit("📻 Radio is on but its URL could not be resolved; streaming without it.");
        }
        _onAir = ""; _upNext = ""; _np?.Dispose(); _np = null;
        if (_p.NowPlaying.Enabled)
        {
            if (File.Exists(Path.Combine(WorkDir, CommandBuilder.FontName)))
            {
                _np = new NowPlayingController(_ffmpeg, WorkDir, _p, NowPlaying) { Hidden = OverlaysHidden };
                _np.Log += Emit;
                if (!File.Exists(Path.Combine(WorkDir, NowPlayingCard.CardFile))) { Emit("Now-playing card disabled: " + NowPlayingCard.LastError); _np.Dispose(); _np = null; }
            }
            else Emit("Now-playing card disabled: no font file.");
        }
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
    static string RadioRelayHost(string u) => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host + x.AbsolutePath : u;
    public void Stop() { _stop = true; _cts?.Cancel(); KillFeeder(); Interlocked.Increment(ref _musicGen); Interlocked.Increment(ref _pitGen); }

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
        if (_np is { } np) { np.Hidden = h; Task.Run(np.Tick); }
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

    /// <summary>v2.4.1: awaited on the engine thread before each reconnect (after the backoff). May return replacement
    /// destination URLs (index → (url, secret)), e.g. when a new EpikChat live session issued a different key.</summary>
    public Func<Task<IReadOnlyDictionary<int, (string Url, string Secret)>?>>? BeforeReconnect { get; set; }
    public void ReplaceDestinations(IReadOnlyDictionary<int, (string Url, string Secret)> map)
    {
        lock (_lk)
            foreach (var (i, (url, secret)) in map)
            {
                if (i < 0 || i >= _netDests.Count) continue;
                if (secret.Length > 0 && !_secrets.Contains(secret)) _secrets.Add(secret);
                _netDests[i] = _netDests[i] with { Target = url };
                Emit($"Destination \"{_netDests[i].Name}\" now uses the new EpikChat live session's key.");
            }
    }
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
        if ((TextOverlays || _p.NowPlaying.Enabled) && !(o.TextEnabled || o.ClockEnabled))
        {
            var font = string.IsNullOrWhiteSpace(o.FontFile) ? CommandBuilder.DefaultFont() : o.FontFile;
            if (File.Exists(font)) File.Copy(font, Path.Combine(WorkDir, CommandBuilder.FontName), true);
            else { Emit("No font for on-stream alerts/chat responses/now-playing card; they're disabled. " + font); TextOverlays = false; }
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
                if (_plannedRestart)
                {   // v2.6.1 auto-degrade: restart at once with the smaller Ball Pit frame, no backoff, not counted as a failure
                    _plannedRestart = false; try { _enc?.WaitForExit(3000); } catch { }
                    ComputeResume(); _reconnects++; continue;
                }
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
                if (!_stop && BeforeReconnect is { } br)
                {
                    try
                    {
                        State?.Invoke("Checking the EpikChat live session…");
                        var t = br();
                        if (!t.Wait(TimeSpan.FromSeconds(120))) Emit("Go-live check timed out; reconnecting anyway.");
                        else if (t.Result is { } map) ReplaceDestinations(map);
                    }
                    catch (Exception ex) { Emit("Go-live check failed: " + ex.GetBaseException().Message); }
                }
            }
        }
        catch (Exception ex) { Emit("Engine error: " + ex); State?.Invoke("Error: " + ex.Message); }
        finally
        {
            KillFeeder(); Interlocked.Increment(ref _musicGen); Interlocked.Increment(ref _pitGen); try { if (_enc is { HasExited: false }) _enc.Kill(true); } catch { }
            _ctl?.Dispose(); _ctl = null; _alertTimer?.Dispose(); _chatTimer?.Dispose(); _np?.Dispose(); StopRadio();
            for (int i = 0; i < _netDests.Count; i++) DestinationStatus?.Invoke(i, DestState.Stopped, "");
            RemuxRecordings();
            IsRunning = false; Finished?.Invoke();
        }
    }

    // ---- v2.6.1 real-time watchdog + Ball Pit auto-degrade ----
    static readonly Regex ResumedLag = new(@"\[(\w+#\d+:\d+)/\w+ @[^\]]*\] Resumed reading at pts [\d.]+ .*after a lag of ([\d.]+)s", RegexOptions.Compiled);
    /// <summary>Speed / lag tracker of the current encoder run.</summary>
    public sealed class PaceMonitor
    {
        public readonly Stopwatch Run = new(); public int LagLines; public double SlowSince = -1, LastAction = -1e9;
        readonly Dictionary<string, List<(double t, double lag)>> _lags = new();
        readonly List<(double t, long written, long skipped)> _feed = new();
        readonly List<(double t, double outT)> _out = new();
        /// <summary>Real speed over the last ~10 s (ffmpeg's speed= is averaged since the encoder started).</summary>
        public double RecentSpeed(double outT, double cumulative)
        {
            double t = Run.Elapsed.TotalSeconds;
            lock (_out)
            {
                _out.Add((t, outT)); while (_out.Count > 2 && _out[0].t < t - 10) _out.RemoveAt(0);
                return _out.Count > 1 && t - _out[0].t >= 8 ? (_out[^1].outT - _out[0].outT) / (t - _out[0].t) : cumulative;
            }
        }
        public void Reset() { lock (_out) _out.Clear(); lock (_lags) { _lags.Clear(); _feed.Clear(); } Run.Restart(); LagLines = 0; SlowSince = -1; LastAction = -1e9; }
        /// <summary>Share of Ball Pit frames the feed had to skip over the last 20 s (the encoder didn't read them in time).</summary>
        public double SkipRatio(BallPitFeed? f)
        {
            if (f == null) return 0; double t = Run.Elapsed.TotalSeconds;
            lock (_lags)
            {
                _feed.Add((t, f.Written, f.Skipped)); while (_feed.Count > 2 && _feed[0].t < t - 20) _feed.RemoveAt(0);
                if (_feed.Count < 2 || t - _feed[0].t < 15) return 0;
                long w = _feed[^1].written - _feed[0].written, k = _feed[^1].skipped - _feed[0].skipped;
                return w + k <= 0 ? 0 : (double)k / (w + k);
            }
        }
        public void OnLag(string input, double lag) { lock (_lags) { if (!_lags.TryGetValue(input, out var l)) _lags[input] = l = new(); l.Add((Run.Elapsed.TotalSeconds, lag)); if (l.Count > 200) l.RemoveAt(0); } }
        /// <summary>Largest growth of any input's "Resumed reading … lag" over the last 20 s.</summary>
        public double LagGrowth()
        {
            double t = Run.Elapsed.TotalSeconds, g = 0;
            lock (_lags) foreach (var l in _lags.Values) { var w = l.Where(x => x.t > t - 20).ToList(); if (w.Count > 1) g = Math.Max(g, w[^1].lag - w[0].lag); }
            return g;
        }
    }
    readonly PaceMonitor _pace = new();
    public PaceMonitor Pace => _pace;
    double _pitDegrade = 1; bool _pitHalfRate, _lowestWarned; volatile bool _plannedRestart;
    public double PitDegrade => _pitDegrade;
    public int AutoDegrades { get; private set; }
    /// <summary>Raised with a user-facing warning when the encoder can't keep up (also logged).</summary>
    public event Action<string>? PerformanceWarning;
    /// <summary>Speed below 0.97x for 15 s, or the -re inputs' lag growing, after a 25 s warm-up: lower the Ball Pit load.</summary>
    void PaceCheck(double speed)
    {
        if (!PitActive || !_p.BallPit.AutoQuality || _plannedRestart) return;
        double t = _pace.Run.Elapsed.TotalSeconds; speed = _pace.RecentSpeed(_outTime, speed); if (t < 25 || speed <= 0) return;
        if (speed < 0.97) { if (_pace.SlowSince < 0) _pace.SlowSince = t; } else _pace.SlowSince = -1;
        bool slow = _pace.SlowSince >= 0 && t - _pace.SlowSince >= 15, lagging = _pace.LagGrowth() > 5;
        double skip = _pace.SkipRatio(PitFeed); bool starving = skip > 0.10;   // the stream is real time but the pit stutters
        if (!(slow || lagging || starving) || t - _pace.LastAction < 20) return;
        _pace.LastAction = t; _pace.SlowSince = -1; AutoDegrades++;
        var why = slow ? $"speed {speed:0.00}x" : lagging ? $"inputs falling behind by {_pace.LagGrowth():0} s per 20 s" : $"{skip:P0} of the Ball Pit frames dropped";
        if (PitFeed is { HalfRate: false } f) { f.HalfRate = _pitHalfRate = true; Warn($"⚠ Encoder can't keep up ({why}) — lowering Ball Pit quality: drawing every 2nd frame."); return; }
        double floor = 0.5 / Math.Max(0.25, _p.BallPit.RenderScale);
        if (_pitDegrade > floor * 1.12)   // a step worth an encoder restart (≥ ~10 % smaller)
        {
            // size the step from how far behind it is (pixels ∝ scale²), so one restart usually suffices
            double need = slow ? speed : starving ? 1 - skip : 0.75 * 0.75;
            double step = Math.Clamp(Math.Sqrt(Math.Max(0.05, need)) * 0.85, 0.5, 0.85);
            _pitDegrade = Math.Max(floor, _pitDegrade * step);
            Warn($"⚠ Encoder can't keep up ({why}) — lowering Ball Pit quality: render size {Math.Round(_p.BallPit.RenderScale * _pitDegrade * 100)} % (restarting the encoder).");
            _plannedRestart = true; try { _enc?.Kill(true); } catch { }
            return;
        }
        if (_lowestWarned) return; _lowestWarned = true;
        Warn($"⚠ Encoder can't keep up ({why}) even at the lowest Ball Pit quality. Try a lower resolution/fps, a faster encoder preset or the hardware encoder.");
    }
    void Warn(string m) { Emit(m); try { PerformanceWarning?.Invoke(m); } catch { } State?.Invoke("Live — " + m.TrimStart('⚠', ' ')); }

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
        List<CommandBuilder.Destination> dests; lock (_lk) dests = new List<CommandBuilder.Destination>(_netDests);
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
        int pitPort = PitActive ? StreamControl.FreePort() : 0;
        int radioPort = RadioActive ? StreamControl.FreePort() : 0;
        double rs = Math.Clamp(_p.BallPit.RenderScale * _pitDegrade, 0.25, 1);
        _pace.Reset();
        int pw = Math.Max(16, (int)(_p.Width * rs) & ~1), ph = Math.Max(16, (int)(_p.Height * rs) & ~1);
        var lo = new CommandBuilder.LiveOptions(ctlPort, musicPort, MicActive ? a.MicDevice : null, TextOverlays,
            MusicMuted ? 0 : a.MusicVolume, MicMuted ? 0 : a.MicVolume, a.MainVolume, _np != null,
            pitPort, PitActive ? _p.BallPit.Mode : "", pw, ph, radioPort, RadioMuted ? 0 : _p.Radio.Volume);
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
            if (ResumedLag.Match(e.Data) is { Success: true } lm) { _pace.OnLag(lm.Groups[1].Value, double.Parse(lm.Groups[2].Value, CultureInfo.InvariantCulture)); if (_pace.LagLines++ % 30 != 0) return; }   // keep the log readable
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
        if (pitPort > 0) StartPitFeed(pitPort, pw, ph, _p.BallPit.Mode == "Overlay");
        if (radioPort > 0) StartRadioFeed(radioPort);
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
            var cur = s.Transition ? s.NextOrder[s.NextIdx] : s.Item;
            _onAirPos = s.Transition ? (t - s.Tl0) : pos;
            if (cur != _onAir)
            {
                _onAir = cur; _trackNo++;
                var o = s.Transition ? s.NextOrder : s.Order; int i = s.Transition ? s.NextIdx : s.Idx;
                _upNext = i + 1 < o.Count ? o[i + 1] : (_p.LoopPlaylist && _p.Playlist.Count > 0 ? _p.Playlist[0] : "");
                Requests?.OnAir(cur);
                try { TrackChanged?.Invoke(cur); } catch { }
                _np?.TrackChanged(TrackKey);
            }
        }
        PaceCheck(D(kv, "speed"));
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
        if (ReferenceEquals(order, _order) && idx == _idx && InsertRequest(idx)) return (_order, idx + 1);
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
                if (r == SegResult.Skipped) { _skip = false; InsertRequest(_idx); _idx++; _pos = 0; continue; }   // hard cut to the next item
            }
            if (Requests != null && Requests.PeekNext() != null && !_stop && InsertRequest(_idx))
            {   // a request arrived while this item played: it goes next (re-plan the transition)
                var rn = (_order, _idx + 1);
                if (Playable(_order[_idx + 1], out var rInfo)) { nx = rn; infoB = rInfo; if (X > 0) { X = Frames(Math.Min(X, rInfo.Duration / 2)); if (X < 2.0 / _p.Fps) X = 0; } }
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
