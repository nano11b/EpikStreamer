using SkiaSharp;
namespace EpikStreamer.Core;

/// <summary>2.8.4 "Space" Ball Pit background: a deep-space sky (gradient, Milky Way band, nebulae, 2–4 galaxies, layered
/// starfield, vignette) rendered ONCE per (size, seed) into a raster image on a background thread, then only blitted per frame.
/// Animation is cheap: the cached layer drifts by whole pixels (no resampling), and High adds ~48 pre-rendered twinkle sprites.
/// Nothing procedural runs on the frame path.</summary>
public sealed class SpaceSky : IDisposable
{
    public const float Margin = 0.04f;     // extra border on each side for the drift
    public int W { get; }
    public int H { get; }
    public int Seed { get; }
    public SKImage Image { get; }
    public int Pad { get; }
    readonly (float X, float Y, float R, float Phase, float Speed, SKColor C)[] _tw;
    readonly SKImage _sprite;
    readonly SKPaint _p = new() { FilterQuality = SKFilterQuality.Low };
    public double BuildMs { get; }
    public int Galaxies { get; }
    public int Stars { get; }

    SpaceSky(int w, int h, int seed)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        W = w; H = h; Seed = seed; Pad = (int)Math.Ceiling(Math.Max(w, h) * Margin);
        int iw = w + 2 * Pad, ih = h + 2 * Pad; var rnd = new Random(seed);
        float k = Math.Max(iw, ih) / 1280f;   // sizes relative to 720p
        using var surf = SKSurface.Create(new SKImageInfo(iw, ih, SKColorType.Bgra8888, SKAlphaType.Premul));
        var c = surf.Canvas;
        // 1) deep gradient
        using (var g = new SKPaint { Shader = SKShader.CreateLinearGradient(new(0, 0), new(iw * 0.3f, ih), new[] { new SKColor(3, 4, 14), new SKColor(9, 12, 34), new SKColor(4, 5, 16) }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, iw, ih, g);
        // 2) Milky Way band: soft glow + dense faint stars along a tilted line
        float ang = (float)(rnd.NextDouble() * 50 - 25), cy = ih * (0.3f + 0.4f * (float)rnd.NextDouble());
        c.Save(); c.Translate(iw / 2f, cy); c.RotateDegrees(ang);
        using (var band = new SKPaint { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 60 * k) })
        {
            band.Color = new SKColor(70, 80, 140, 34); c.DrawOval(0, 0, iw * 0.75f, ih * 0.13f, band);
            band.Color = new SKColor(150, 140, 190, 22); c.DrawOval(0, 0, iw * 0.55f, ih * 0.05f, band);
        }
        using (var sp = new SKPaint { IsAntialias = true })
            for (int i = 0; i < (int)(1800 * k * k); i++)
            {
                float x = (float)((rnd.NextDouble() * 2 - 1) * iw * 0.75), y = (float)(Gauss(rnd) * ih * 0.06);
                sp.Color = new SKColor(220, 225, 255, (byte)rnd.Next(25, 90)); c.DrawCircle(x, y, 0.5f * k + (float)rnd.NextDouble() * 0.5f * k, sp);
            }
        c.Restore();
        // 3) nebulae: big soft radial clouds, additive
        SKColor[] neb = { new(120, 60, 200), new(40, 140, 200), new(200, 60, 140), new(60, 180, 160), new(90, 70, 220) };
        for (int i = 0, n = 3 + rnd.Next(3); i < n; i++)
        {
            float x = (float)rnd.NextDouble() * iw, y = (float)rnd.NextDouble() * ih, r = (float)(0.18 + rnd.NextDouble() * 0.22) * iw;
            var col = neb[rnd.Next(neb.Length)];
            for (int j = 0; j < 3; j++)
            {
                float ox = x + (float)Gauss(rnd) * r * 0.3f, oy = y + (float)Gauss(rnd) * r * 0.2f, rr = r * (0.5f + 0.5f * (float)rnd.NextDouble());
                using var np = new SKPaint { BlendMode = SKBlendMode.Plus, Shader = SKShader.CreateRadialGradient(new(ox, oy), rr, new[] { col.WithAlpha(38), col.WithAlpha(14), col.WithAlpha(0) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) };
                c.DrawCircle(ox, oy, rr, np);
            }
        }
        // 4) galaxies: two-arm log spirals of soft dots, tilted into ellipses, with a bright core
        Galaxies = 2 + rnd.Next(3);
        for (int gi = 0; gi < Galaxies; gi++)
        {
            float gx = (float)(0.1 + 0.8 * rnd.NextDouble()) * iw, gy = (float)(0.08 + 0.84 * rnd.NextDouble()) * ih;
            float size = (float)(gi == 0 ? 90 + rnd.NextDouble() * 50 : 30 + rnd.NextDouble() * 45) * k;
            float tilt = (float)(0.3 + 0.6 * rnd.NextDouble()), rot = (float)(rnd.NextDouble() * 360);
            var tint = rnd.Next(3) switch { 0 => new SKColor(200, 210, 255), 1 => new SKColor(255, 220, 190), _ => new SKColor(220, 200, 255) };
            c.Save(); c.Translate(gx, gy); c.RotateDegrees(rot); c.Scale(1, tilt);
            using (var halo = new SKPaint { BlendMode = SKBlendMode.Plus, Shader = SKShader.CreateRadialGradient(new(0, 0), size, new[] { tint.WithAlpha(60), tint.WithAlpha(16), tint.WithAlpha(0) }, new[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawCircle(0, 0, size, halo);
            using (var dot = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus })
            {
                int dots = (int)(size * 9); bool spiral = rnd.Next(4) != 0;   // a few are plain ellipticals
                for (int i = 0; i < dots; i++)
                {
                    float u = (float)rnd.NextDouble(), rr, th;
                    if (spiral) { rr = size * (0.08f + 0.92f * u); th = (float)(Math.Log(rr / size + 0.08) * 2.6 + (i % 2) * Math.PI) + (float)Gauss(rnd) * 0.28f; }
                    else { rr = size * u * u; th = (float)(rnd.NextDouble() * Math.PI * 2); }
                    byte a = (byte)Math.Clamp(110 * (1 - u) + 20, 0, 255);
                    dot.Color = (i % 7 == 0 ? new SKColor(150, 180, 255) : tint).WithAlpha(a);
                    c.DrawCircle(rr * MathF.Cos(th), rr * MathF.Sin(th), (0.6f + (float)rnd.NextDouble() * 0.9f) * k, dot);
                }
            }
            using (var core = new SKPaint { BlendMode = SKBlendMode.Plus, Shader = SKShader.CreateRadialGradient(new(0, 0), size * 0.22f, new[] { new SKColor(255, 248, 230, 200), new SKColor(255, 230, 200, 40), SKColors.Transparent }, new[] { 0f, 0.4f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawCircle(0, 0, size * 0.22f, core);
            c.Restore();
        }
        // 5) starfield: three layers (many faint, some medium, a few bright with glow/spikes); a few coloured
        SKColor StarCol() => rnd.Next(12) switch { 0 => new(170, 200, 255), 1 => new(255, 210, 160), 2 => new(255, 170, 150), _ => new(240, 244, 255) };
        int faint = (int)(iw * ih / 700f), mid = faint / 8, bright = 14 + rnd.Next(10); Stars = faint + mid + bright;
        using (var sp = new SKPaint { IsAntialias = true })
        {
            for (int i = 0; i < faint; i++) { sp.Color = StarCol().WithAlpha((byte)rnd.Next(40, 140)); c.DrawCircle((float)rnd.NextDouble() * iw, (float)rnd.NextDouble() * ih, (0.45f + (float)rnd.NextDouble() * 0.35f) * k, sp); }
            for (int i = 0; i < mid; i++) { sp.Color = StarCol().WithAlpha((byte)rnd.Next(140, 230)); c.DrawCircle((float)rnd.NextDouble() * iw, (float)rnd.NextDouble() * ih, (0.9f + (float)rnd.NextDouble() * 0.6f) * k, sp); }
            for (int i = 0; i < bright; i++) BrightStar(c, (float)rnd.NextDouble() * iw, (float)rnd.NextDouble() * ih, (1.4f + (float)rnd.NextDouble() * 1.4f) * k, StarCol(), rnd.Next(3) == 0);
        }
        // 6) vignette + a darker lower third so the floor, balls and names keep their contrast
        using (var v = new SKPaint { Shader = SKShader.CreateRadialGradient(new(iw / 2f, ih * 0.45f), Math.Max(iw, ih) * 0.72f, new[] { SKColors.Transparent, new SKColor(0, 0, 0, 40), new SKColor(0, 0, 0, 170) }, new[] { 0f, 0.6f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, iw, ih, v);
        using (var v = new SKPaint { Shader = SKShader.CreateLinearGradient(new(0, ih * 0.55f), new(0, ih), new[] { SKColors.Transparent, new SKColor(0, 0, 0, 110) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(0, 0, iw, ih, v);
        Image = surf.Snapshot().ToRasterImage();
        // twinkle sprites (High only): a soft star image drawn at a few positions with a varying alpha
        using (var ss = SKSurface.Create(new SKImageInfo(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            using var gp = new SKPaint { Shader = SKShader.CreateRadialGradient(new(8, 8), 8, new[] { SKColors.White, new SKColor(255, 255, 255, 90), SKColors.Transparent }, new[] { 0f, 0.25f, 1f }, SKShaderTileMode.Clamp) };
            ss.Canvas.Clear(SKColors.Transparent); ss.Canvas.DrawCircle(8, 8, 8, gp);
            _sprite = ss.Snapshot().ToRasterImage();
        }
        _tw = new (float, float, float, float, float, SKColor)[48];
        for (int i = 0; i < _tw.Length; i++) _tw[i] = ((float)rnd.NextDouble() * iw, (float)rnd.NextDouble() * ih * 0.8f, (3 + (float)rnd.NextDouble() * 4) * k, (float)(rnd.NextDouble() * 6.3), 0.8f + (float)rnd.NextDouble() * 2.2f, StarCol());
        BuildMs = sw.Elapsed.TotalMilliseconds;
    }

    static double Gauss(Random r) { double u = 1 - r.NextDouble(), v = r.NextDouble(); return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v); }
    static void BrightStar(SKCanvas c, float x, float y, float r, SKColor col, bool spikes)
    {
        using var g = new SKPaint { BlendMode = SKBlendMode.Plus, Shader = SKShader.CreateRadialGradient(new(x, y), r * 5, new[] { col.WithAlpha(120), col.WithAlpha(20), SKColors.Transparent }, new[] { 0f, 0.3f, 1f }, SKShaderTileMode.Clamp) };
        c.DrawCircle(x, y, r * 5, g);
        using var p = new SKPaint { IsAntialias = true, Color = SKColors.White }; c.DrawCircle(x, y, r * 0.7f, p);
        if (!spikes) return;
        using var l = new SKPaint { IsAntialias = true, StrokeWidth = Math.Max(1, r * 0.35f), BlendMode = SKBlendMode.Plus, Shader = SKShader.CreateRadialGradient(new(x, y), r * 7, new[] { col.WithAlpha(150), SKColors.Transparent }, null, SKShaderTileMode.Clamp) };
        c.DrawLine(x - r * 7, y, x + r * 7, y, l); c.DrawLine(x, y - r * 7, x, y + r * 7, l);
    }

    /// <summary>Draws the sky: whole-pixel drift (anim ≥ 1), twinkle sprites (anim 2). Cost ≈ one image copy + ≤ 48 tiny sprites.</summary>
    public void Draw(SKCanvas c, double t, int anim)
    {
        int dx = 0, dy = 0;
        if (anim >= 1) { dx = (int)Math.Round(Pad * 0.9 * Math.Sin(t * 0.011)); dy = (int)Math.Round(Pad * 0.6 * Math.Sin(t * 0.0083 + 1.3)); }
        c.DrawImage(Image, -Pad + dx, -Pad + dy);
        if (anim < 2) return;
        foreach (var s in _tw)
        {
            float a = 0.5f + 0.5f * MathF.Sin((float)t * s.Speed + s.Phase); a *= a;
            if (a < 0.05f) continue;
            _p.Color = SKColors.White.WithAlpha((byte)(a * 220));   // paint alpha fades the sprite
            float x = s.X - Pad + dx, y = s.Y - Pad + dy, r = s.R * (0.7f + 0.3f * a);
            c.DrawImage(_sprite, new SKRect(x - r, y - r, x + r, y + r), _p);
        }
    }

    public void Dispose() { Image.Dispose(); _sprite.Dispose(); _p.Dispose(); }

    // ---- cache: built off the frame path; the renderer draws a plain dark fill until it's ready ----
    static readonly object Lk = new();
    static readonly Dictionary<(int, int, int), SpaceSky> Ready = new();     // ≤ 3 (stream feed + in-app preview + one spare)
    static readonly HashSet<(int, int, int)> Building = new();
    static readonly List<(int, int, int)> Order = new();
    public static long Builds;
    /// <summary>The sky for this size/seed if it's ready (else starts building it on its own thread and returns null).</summary>
    public static SpaceSky? Get(int w, int h, int seed)
    {
        var key = (w, h, seed);
        lock (Lk)
        {
            if (Ready.TryGetValue(key, out var s)) { Order.Remove(key); Order.Add(key); return s; }
            if (!Building.Add(key)) return null;
        }
        new Thread(() =>
        {
            SpaceSky? n = null;
            try { n = new SpaceSky(w, h, seed); } catch { }
            lock (Lk)
            {
                Building.Remove(key); if (n == null) return;
                Ready[key] = n; Order.Add(key); Builds++;
                while (Order.Count > 3) { Ready.Remove(Order[0]); Order.RemoveAt(0); }   // evicted skies are left to the GC (may be mid-draw)
            }
        }) { IsBackground = true, Name = "ball pit sky", Priority = ThreadPriority.BelowNormal }.Start();
        return null;
    }
    /// <summary>Builds synchronously (tests, stills).</summary>
    public static SpaceSky BuildNow(int w, int h, int seed) => new(w, h, seed);
}
