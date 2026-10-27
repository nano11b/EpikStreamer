using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
namespace EpikStreamer.Core;

/// <summary>Avatar download + disk cache. Tries a plain HTTPS GET first; if that fails, the host's page fetch (the chat
/// WebView, with its cookies) is used. Only images ≤ 1.5 MB; data: URLs are decoded inline.</summary>
public sealed class AvatarCache
{
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 }) { Timeout = TimeSpan.FromSeconds(10) };
    readonly ConcurrentDictionary<string, uint[]> _mem = new();
    readonly ConcurrentDictionary<string, byte> _pending = new();
    readonly string _dir;
    public Func<string, Task<byte[]?>>? PageFetch { get; set; }
    public event Action<string, uint[]>? Ready;
    public int Failed;
    public AvatarCache(string dir) { _dir = dir; try { Directory.CreateDirectory(dir); } catch { } }

    public uint[]? Get(string url) { if (_mem.TryGetValue(url, out var t)) return t; Request(url); return null; }

    public void Request(string url)
    {
        if (string.IsNullOrEmpty(url) || _mem.ContainsKey(url) || !_pending.TryAdd(url, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var tex = await LoadAsync(url);
                if (tex != null) { _mem[url] = tex; Ready?.Invoke(url, tex); } else Interlocked.Increment(ref Failed);
            }
            catch { Interlocked.Increment(ref Failed); }
            finally { _pending.TryRemove(url, out _); }
        });
    }

    string FileFor(string url) => Path.Combine(_dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..20] + ".img");

    async Task<uint[]?> LoadAsync(string url)
    {
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            int c = url.IndexOf(";base64,", StringComparison.Ordinal); if (c < 0) return null;
            return BallPitTextures.FromEncoded(Convert.FromBase64String(url[(c + 8)..]));
        }
        var f = FileFor(url);
        if (File.Exists(f) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalDays < 7)
            if (BallPitTextures.FromEncoded(await File.ReadAllBytesAsync(f)) is { } cached) return cached;
        byte[]? data = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            if (u.Scheme == "file" && Environment.GetEnvironmentVariable("EPIK_TEST_ALLOW_FILE") == "1") data = await File.ReadAllBytesAsync(u.LocalPath);
            else if (u.Scheme == "https" || u.Scheme == "http")
            {
                try
                {
                    using var r = await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead);
                    if (r.IsSuccessStatusCode && (r.Content.Headers.ContentType?.MediaType ?? "image/").StartsWith("image/") && (r.Content.Headers.ContentLength ?? 0) <= 1_500_000)
                    { var b = await r.Content.ReadAsByteArrayAsync(); if (b.Length <= 1_500_000) data = b; }
                }
                catch { }
            }
        }
        var tex = data == null ? null : BallPitTextures.FromEncoded(data);
        if (tex == null && PageFetch != null)
        {
            data = await PageFetch(url);
            tex = data == null ? null : BallPitTextures.FromEncoded(data);
        }
        if (tex != null && data != null && !url.StartsWith("file:")) { try { await File.WriteAllBytesAsync(f, data); } catch { } }
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

    public void Run(int port, Func<bool> alive)
    {
        System.Net.Sockets.TcpClient? tc = null;
        for (int i = 0; i < 300 && alive(); i++)
        {
            try { tc = new System.Net.Sockets.TcpClient { NoDelay = true, SendBufferSize = 1 << 22 }; tc.Connect(System.Net.IPAddress.Loopback, port); break; }
            catch { tc?.Dispose(); tc = null; Thread.Sleep(100); }
        }
        if (tc == null) { if (alive()) Log?.Invoke("● Could not connect the Ball Pit feed to the encoder."); return; }
        using var _tc = tc; var net = tc.GetStream();
        using var r = new BallPitRenderer(_w, _h) { Transparent = _transparent };
        var sw = Stopwatch.StartNew(); long n = 0; double renderSec = 0; int renderCount = 0; bool slowLogged = false; int slowWindows = 0;
        try
        {
            while (alive())
            {
                double due = (double)n / _fps;
                double now = sw.Elapsed.TotalSeconds;
                if (now < due) { double wait = due - now; if (wait > 0.002) Thread.Sleep(Math.Max(1, (int)(wait * 1000) - 1)); else Thread.SpinWait(200); continue; }
                Lag = n > 0 ? now - due : 0;
                bool behind = Lag > 1.5 / _fps && n > 0;
                bool render = behind ? n % 3 == 0 : (!HalfRate || n % 2 == 0 || n == 0);
                if (render)
                {
                    var t0 = sw.Elapsed.TotalSeconds;
                    _scene.Step(1.0 / _fps); r.Render(_scene); Card(r, t0);
                    renderSec += sw.Elapsed.TotalSeconds - t0; renderCount++;
                    if (renderCount == 30)
                    {
                        RenderFps = renderCount / Math.Max(1e-6, renderSec); renderSec = 0; renderCount = 0;
                        // render-bound (slow CPU): render every 2nd frame, the stream stays at the full frame rate
                        slowWindows = RenderFps < _fps * 1.15 ? slowWindows + 1 : 0;
                        if (!HalfRate && slowWindows >= 3) { HalfRate = true; if (!slowLogged) { slowLogged = true; Log?.Invoke($"⚠ Ball Pit renders only {RenderFps:0} fps on this PC: drawing every 2nd frame (the stream stays at {_fps} fps)."); } }
                    }
                    if (n % Math.Max(1, _fps / 10) == 0) SetPreview(r);
                }
                else { _scene.Step(1.0 / _fps); Repeats++; }   // keep sim time = stream time; resend the last image
                var tw = sw.Elapsed.TotalSeconds; net.Write(r.Bytes); WriteSec += sw.Elapsed.TotalSeconds - tw; Written++; n++; Frames = n;
                if (n == 1) sw.Restart();   // the encoder only starts reading once all its inputs are open: start the clock there
                // way behind (machine stalled / encoder not reading): skip ahead instead of bursting. The encoder stamps this input
                // with the wall clock (use_wallclock_as_timestamps), so a skip becomes repeated frames, not a slower timeline.
                if (behind && Lag > 1.0) { var nn = (long)(sw.Elapsed.TotalSeconds * _fps); Skipped += nn - n; n = nn; }
            }
        }
        catch (Exception) { /* encoder went away; the next encoder start begins a new feed */ }
        finally { _card?.Dispose(); _card = null; }
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
