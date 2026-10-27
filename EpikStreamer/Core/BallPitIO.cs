using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
namespace EpikStreamer.Core;

/// <summary>Avatar download + disk cache. Tries a plain HTTPS GET first; if that fails, the host's page fetch (the chat
/// WebView, with its cookies) is used. Only images ≤ 1.5 MB; data: URLs are decoded inline.
/// <para>2.8.3: at most <see cref="MaxConcurrent"/> loads/decodes at a time (a burst of 50 joins used to start 50 at once),
/// decoded straight to the 64² ball texture, kept in a byte-bounded LRU (<see cref="MaxBytes"/>), failures remembered for
/// 10 minutes, and <see cref="Ready"/> never blocks (the scene just queues the texture for the next physics step).</para></summary>
public sealed class AvatarCache
{
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 }) { Timeout = TimeSpan.FromSeconds(10) };
    readonly object _lk = new();
    readonly Dictionary<string, LinkedListNode<(string Url, uint[] Tex)>> _mem = new();
    readonly LinkedList<(string Url, uint[] Tex)> _lru = new();
    readonly ConcurrentDictionary<string, byte> _pending = new();
    readonly ConcurrentDictionary<string, DateTime> _failed = new();
    readonly SemaphoreSlim _gate;
    readonly string _dir;
    public const int MaxConcurrent = 3;
    public long MaxBytes { get; }
    public long Bytes { get; private set; }
    public int Count { get { lock (_lk) return _mem.Count; } }
    public int InFlight => _pending.Count;
    public long Loaded, Evictions, PeakInFlightDecodes; int _decoding;
    public Func<string, Task<byte[]?>>? PageFetch { get; set; }
    public event Action<string, uint[]>? Ready;
    public int Failed;
    public AvatarCache(string dir, long maxBytes = 8L << 20, int concurrency = MaxConcurrent)
    {
        _dir = dir; MaxBytes = Math.Max(1 << 16, maxBytes); _gate = new SemaphoreSlim(Math.Max(1, concurrency));
        try { Directory.CreateDirectory(dir); } catch { }
    }

    public uint[]? Get(string url)
    {
        lock (_lk) if (_mem.TryGetValue(url, out var node)) { _lru.Remove(node); _lru.AddFirst(node); return node.Value.Tex; }
        Request(url); return null;
    }
    /// <summary>Adds a texture (most recently used); evicts the least recently used ones over <see cref="MaxBytes"/>.</summary>
    public void Put(string url, uint[] tex)
    {
        lock (_lk)
        {
            if (_mem.TryGetValue(url, out var old)) { _lru.Remove(old); Bytes -= old.Value.Tex.Length * 4L; }
            var node = _lru.AddFirst((url, tex)); _mem[url] = node; Bytes += tex.Length * 4L;
            while (Bytes > MaxBytes && _lru.Count > 1) { var last = _lru.Last!; _lru.RemoveLast(); _mem.Remove(last.Value.Url); Bytes -= last.Value.Tex.Length * 4L; Evictions++; }
        }
    }
    public bool Contains(string url) { lock (_lk) return _mem.ContainsKey(url); }

    public void Request(string url)
    {
        if (string.IsNullOrEmpty(url) || Contains(url)) return;
        if (_failed.TryGetValue(url, out var until) && until > DateTime.UtcNow) return;
        if (!_pending.TryAdd(url, 0)) return;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var tex = await LoadAsync(url).ConfigureAwait(false);
                if (tex != null) { Put(url, tex); Interlocked.Increment(ref Loaded); _failed.TryRemove(url, out _); Ready?.Invoke(url, tex); }
                else Fail(url);
            }
            catch { Fail(url); }
            finally { _gate.Release(); _pending.TryRemove(url, out _); }
        });
    }
    void Fail(string url) { Interlocked.Increment(ref Failed); _failed[url] = DateTime.UtcNow.AddMinutes(10); }

    uint[]? Decode(byte[] data)
    {
        var d = Interlocked.Increment(ref _decoding);
        long peak; while (d > (peak = Interlocked.Read(ref PeakInFlightDecodes)) && Interlocked.CompareExchange(ref PeakInFlightDecodes, d, peak) != peak) { }
        try { return BallPitTextures.FromEncoded(data); } finally { Interlocked.Decrement(ref _decoding); }
    }

    string FileFor(string url) => Path.Combine(_dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20] + ".img");

    async Task<uint[]?> LoadAsync(string url)
    {
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            int c = url.IndexOf(";base64,", StringComparison.Ordinal); if (c < 0 || url.Length > 2_100_000) return null;
            return Decode(Convert.FromBase64String(url[(c + 8)..]));
        }
        var f = FileFor(url);
        if (File.Exists(f) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalDays < 7)
            if (Decode(await File.ReadAllBytesAsync(f).ConfigureAwait(false)) is { } cached) return cached;
        byte[]? data = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            if (u.Scheme == "file" && Environment.GetEnvironmentVariable("EPIK_TEST_ALLOW_FILE") == "1") data = await File.ReadAllBytesAsync(u.LocalPath).ConfigureAwait(false);
            else if (u.Scheme == "https" || u.Scheme == "http")
            {
                try
                {
                    using var r = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (r.IsSuccessStatusCode && (r.Content.Headers.ContentType?.MediaType ?? "image/").StartsWith("image/") && (r.Content.Headers.ContentLength ?? 0) <= 1_500_000)
                    { var b = await r.Content.ReadAsByteArrayAsync().ConfigureAwait(false); if (b.Length <= 1_500_000) data = b; }
                }
                catch { }
            }
        }
        var tex = data == null ? null : Decode(data);
        if (tex == null && PageFetch != null)
        {
            data = await PageFetch(url).ConfigureAwait(false);
            tex = data == null || data.Length > 1_500_000 ? null : Decode(data);
        }
        if (tex != null && data != null && !url.StartsWith("file:")) { try { await File.WriteAllBytesAsync(f, data).ConfigureAwait(false); } catch { } }
        return tex;
    }
}

/// <summary>Steps + renders the ball pit at a fixed rate and writes raw BGRA frames to the encoder's ball-pit input
/// (a local TCP socket, like the music feed). Wall-clock paced; if rendering falls behind, the last frame is repeated so
/// the stream stays real-time. Also keeps a preview copy.</summary>
public sealed class BallPitFeed
{
    readonly BallPitScene _scene; readonly int _w, _h, _fps; readonly bool _transparent;
    public BallPitFeed(BallPitScene scene, int w, int h, int fps, bool transparent) { _scene = scene; _w = w & ~1; _h = h & ~1; _fps = Math.Clamp(fps, 1, 60); _transparent = transparent; }
    public double RenderFps { get; private set; }
    public double Scale { get; private set; } = 1;
    public long Frames, Repeats, Written, Skipped; public double WriteSec;
    public event Action<string>? Log;

    /// <summary>Renders one frame for the in-app preview while streaming (latest frame, downscaled by the caller).</summary>
    public static readonly object PreviewLock = new();
    public static byte[]? PreviewFrame; public static int PreviewW, PreviewH; public static long PreviewSeq;

    /// <summary>v2.6.1: the now-playing card composited into the frames (instead of a separate -re PNG input in ffmpeg).
    /// CardPath = the PNG the card controller rewrites; CardCorner/CardScale place it like the old overlay did.</summary>
    public string? CardPath; public Corner CardCorner = Corner.BottomRight; public double CardScale = 1; public Func<bool>? CardVisible;
    /// <summary>v2.6.1 graceful degradation without an encoder restart: render every 2nd frame (the other one is repeated).</summary>
    public volatile bool HalfRate;
    /// <summary>How far behind the wall clock the feed is (s). Persistent lag = the encoder isn't reading in real time.</summary>
    public double Lag { get; private set; }
    public long CardDraws;

    SkiaSharp.SKBitmap? _card; DateTime _cardStamp; double _cardCheck = -1;
    void Card(BallPitRenderer r, double now)
    {
        if (CardPath == null) return;
        if (now - _cardCheck >= 0.25)
        {
            _cardCheck = now;
            try
            {
                var fi = new FileInfo(CardPath);
                if (fi.Exists && fi.LastWriteTimeUtc != _cardStamp)
                {
                    var bytes = File.ReadAllBytes(CardPath);   // the controller writes a temp file and renames it: always complete
                    var bmp = SkiaSharp.SKBitmap.Decode(bytes);
                    if (bmp != null) { _card?.Dispose(); _card = bmp; _cardStamp = fi.LastWriteTimeUtc; }
                }
            }
            catch { /* being replaced; keep the previous one */ }
        }
        if (_card == null || CardVisible?.Invoke() == false) return;
        float k = (float)CardScale, cw = _card.Width * k, ch = _card.Height * k, m = 20 * k, W = r.Width, H = r.Height;
        float x = CardCorner switch { Corner.TopLeft or Corner.BottomLeft => m, Corner.TopCenter or Corner.Center or Corner.BottomCenter => (W - cw) / 2, _ => W - cw - m };
        float y = CardCorner switch { Corner.TopLeft or Corner.TopCenter or Corner.TopRight => m, Corner.Center => (H - ch) / 2, _ => H - ch - m };
        using var c = new SkiaSharp.SKCanvas(r.Bitmap);
        using var paint = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.Medium };
        c.DrawBitmap(_card, new SkiaSharp.SKRect(x, y, x + cw, y + ch), paint); CardDraws++;
    }

    // ---- 2.8.3: render thread + paced writer ----
    // The writer sends a frame every 1/fps of wall-clock time no matter what; it always sends the newest finished frame (the
    // previous one again if the renderer is late). The render thread steps + draws as fast as the quality level allows. A render
    // exception is logged, the pit is reset and drawing continues — it can no longer end the feed (and so the stream).
    readonly object _swap = new(); byte[]? _front; long _frontSeq;
    /// <summary>Frame times (ms) of the last second of renders, and the derived numbers shown in Stats / diagnostics.</summary>
    public double FrameMsAvg { get; private set; }
    public double FrameMsP95 { get; private set; }
    public double FrameMsMax { get; private set; }
    public int Level { get; private set; }
    public long RenderErrors, LateFrames, LevelChanges;
    public string LastError { get; private set; } = "";
    public string ExitReason { get; private set; } = "";
    /// <summary>Test hook: overrides the frame budget used by the auto degrade (ms).</summary>
    public double? BudgetMsOverride;
    int _forceLevel = -1;
    /// <summary>Asks for at least one more degrade level (the engine calls this instead of restarting the encoder).</summary>
    public bool RaiseLevel(string why)
    {
        int cur = Level; if (cur >= BallPitQuality.MaxLevel) return false;
        _forceLevel = cur + 1; Log?.Invoke($"⚠ Ball Pit: lowering quality to level {cur + 1} ({BallPitQuality.LevelNames[cur + 1]}) — {why}."); return true;
    }
    public string StatsLine => $"pit {FrameMsAvg:0.0}/{FrameMsP95:0.0} ms (avg/p95), {_scene.Count} balls" + (_scene.Waiting > 0 ? $" +{_scene.Waiting} more" : "") + $", level {Level}" + (RenderErrors > 0 ? $", {RenderErrors} render errors" : "");

    public void Run(int port, Func<bool> alive)
    {
        System.Net.Sockets.TcpClient? tc = null;
        for (int i = 0; i < 300 && alive(); i++)
        {
            try { tc = new System.Net.Sockets.TcpClient { NoDelay = true, SendBufferSize = 1 << 22 }; tc.Connect(System.Net.IPAddress.Loopback, port); break; }
            catch { tc?.Dispose(); tc = null; Thread.Sleep(100); }
        }
        if (tc == null) { if (alive()) Log?.Invoke("● Could not connect the Ball Pit feed to the encoder."); ExitReason = "no encoder connection"; return; }
        using var _tc = tc; var net = tc.GetStream();
        int size = _w * _h * 4;
        var stop = false; _front = null;
        var rt = new Thread(() => RenderLoop(() => !stop && alive())) { IsBackground = true, Name = "ball pit render", Priority = ThreadPriority.AboveNormal };
        rt.Start();
        // wait (≤ 3 s) for the first frame; a black frame if the renderer can't even make one
        var sw0 = Stopwatch.StartNew(); while (Interlocked.Read(ref _frontSeq) == 0 && sw0.Elapsed.TotalSeconds < 3 && alive()) Thread.Sleep(5);
        var cur = new byte[size]; long sent = -1;
        var sw = Stopwatch.StartNew(); long n = 0;
        try
        {
            while (alive())
            {
                double due = (double)n / _fps;
                double now = sw.Elapsed.TotalSeconds;
                if (now < due) { double wait = due - now; if (wait > 0.002) Thread.Sleep(Math.Max(1, (int)(wait * 1000) - 1)); else Thread.SpinWait(200); continue; }
                Lag = n > 0 ? now - due : 0;
                lock (_swap) if (_front != null && _frontSeq != sent) { Buffer.BlockCopy(_front, 0, cur, 0, size); sent = _frontSeq; } else if (n > 0) Repeats++;
                var tw = sw.Elapsed.TotalSeconds; net.Write(cur, 0, size); WriteSec += sw.Elapsed.TotalSeconds - tw; Written++; n++; Frames = n;
                if (n == 1) sw.Restart();   // the encoder only starts reading once all its inputs are open: start the clock there
                // way behind (machine stalled / encoder not reading): skip ahead instead of bursting. The encoder stamps this input
                // with the wall clock (use_wallclock_as_timestamps), so a skip becomes repeated frames, not a slower timeline.
                if (Lag > 1.0) { var nn = (long)(sw.Elapsed.TotalSeconds * _fps); Skipped += nn - n; n = nn; }
            }
            ExitReason = "stopped";
        }
        catch (Exception e) { ExitReason = "encoder input closed (" + e.GetType().Name + ")"; /* encoder went away; the next encoder start begins a new feed */ }
        finally { stop = true; rt.Join(3000); }
        if (alive()) Log?.Invoke($"● Ball Pit feed ended: {ExitReason}.");
    }

    void RenderLoop(Func<bool> alive)
    {
        BallPitRenderer? r = null; byte[] back = new byte[_w * _h * 4];
        var sw = Stopwatch.StartNew(); double simT = 0, lastLog = -99, nextStats = 1, nextLog = 60;
        var times = new double[256]; int nt = 0; double renderSec = 0; int renderCount = 0;
        double hotSince = -1, coolSince = -1; var preset = _scene.Settings.Quality;
        void SetLevel(int lv, string why)
        {
            lv = Math.Clamp(lv, 0, BallPitQuality.MaxLevel); if (lv == Level) return;
            bool up = lv > Level; Level = lv; LevelChanges++;
            lock (_scene.Sync) _scene.Quality = BallPitQuality.For(preset, lv);
            Log?.Invoke(up ? $"⚠ Ball Pit {(why.Length > 0 ? why : "is slow")}: quality level {lv} ({BallPitQuality.LevelNames[lv]})."
                           : $"● Ball Pit has headroom again: quality level {lv} ({BallPitQuality.LevelNames[lv]}).");
        }
        lock (_scene.Sync) _scene.Quality = BallPitQuality.For(preset, 0);
        try
        {
            long k = 0;
            while (alive())
            {
                var q = _scene.Quality;
                double step = (HalfRate || q.HalfRate ? 2.0 : 1.0) / _fps;
                double now = sw.Elapsed.TotalSeconds;
                if (now < simT) { double wait = simT - now; if (wait > 0.002) Thread.Sleep(Math.Max(1, (int)(wait * 1000) - 1)); else Thread.SpinWait(200); continue; }
                if (now - simT > 0.5) { simT = now; LateFrames++; }   // fell far behind: don't try to catch up
                var t0 = sw.Elapsed.TotalSeconds;
                try
                {
                    r ??= new BallPitRenderer(_w, _h) { Transparent = _transparent };
                    _scene.Step(step); r.Render(_scene); Card(r, t0);
                    r.Bytes.CopyTo(back);
                    lock (_swap) { (back, _front) = (_front ?? new byte[back.Length], back); _frontSeq++; }
                    if (k++ % Math.Max(1, _fps / 10) == 0) SetPreview(r);
                }
                catch (Exception e)
                {
                    RenderErrors++; LastError = e.GetType().Name + ": " + e.Message;
                    if (t0 - lastLog > 10) { lastLog = t0; Log?.Invoke($"⚠ Ball Pit render error ({e.GetType().Name}: {e.Message}) — resetting the pit; the stream keeps going."); }
                    try { _scene.ResetAfterError(); } catch { }
                    try { r?.Dispose(); } catch { }
                    r = null; Thread.Sleep(50);
                }
                simT += step;
                double ms = (sw.Elapsed.TotalSeconds - t0) * 1000;
                times[nt++ & 255] = ms; renderSec += ms / 1000; renderCount++;
                if (_forceLevel > Level) { SetLevel(_forceLevel, ""); _forceLevel = -1; hotSince = coolSince = -1; }
                if (t0 >= nextStats)
                {
                    nextStats = t0 + 1;
                    if (t0 >= nextLog) { nextLog = t0 + 300; if (t0 > 1) Log?.Invoke("● Ball Pit: " + StatsLine + "."); }
                    int c = Math.Min(nt, 256); if (c == 0) continue;
                    var w = new double[c]; Array.Copy(times, w, c); Array.Sort(w); nt = 0;
                    double avg = 0; foreach (var x in w) avg += x; avg /= c;
                    FrameMsAvg = avg; FrameMsP95 = w[Math.Min(c - 1, (int)(c * 0.95))]; FrameMsMax = w[c - 1];
                    RenderFps = renderCount / Math.Max(1e-6, renderSec); renderSec = 0; renderCount = 0;
                    double p90 = w[Math.Min(c - 1, (int)(c * 0.9))], budget = BudgetMsOverride ?? 1000.0 / _fps;
                    if (p90 > 0.8 * budget) { coolSince = -1; if (hotSince < 0) hotSince = t0; else if (t0 - hotSince >= 2 && Level < BallPitQuality.MaxLevel) { SetLevel(Level + 1, $"frames take {p90:0} ms (budget {budget:0} ms)"); hotSince = -1; } }
                    else if (p90 < 0.4 * budget) { hotSince = -1; if (coolSince < 0) coolSince = t0; else if (t0 - coolSince >= 15 && Level > 0) { SetLevel(Level - 1, ""); coolSince = -1; } }
                    else { hotSince = -1; coolSince = -1; }
                }
            }
        }
        finally { r?.Dispose(); _card?.Dispose(); _card = null; }
    }
    static void SetPreview(BallPitRenderer r)
    {
        lock (PreviewLock)
        {
            var b = r.Bytes; if (PreviewFrame == null || PreviewFrame.Length != b.Length) PreviewFrame = new byte[b.Length];
            b.CopyTo(PreviewFrame); PreviewW = r.Width; PreviewH = r.Height; PreviewSeq++;
        }
    }
}
