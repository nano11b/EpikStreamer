using System.IO;
using System.Numerics;
using SkiaSharp;
namespace EpikStreamer.Core;

/// <summary>Ball textures: avatar photos (square-cropped) or a generated initials disc. TexSize² BGRA, straight alpha.</summary>
public static class BallPitTextures
{
    internal static float Measure(SKFont f, string s) { using var p = new SKPaint(f); return p.MeasureText(s); }
    const int N = BallPitScene.TexSize;
    public static uint Hue(string name)
    {
        uint h = 2166136261; foreach (var c in name.ToLowerInvariant()) h = (h ^ c) * 16777619;
        var col = SKColor.FromHsl(h % 360, 65, 55); return (uint)((col.Red << 16) | (col.Green << 8) | col.Blue);
    }
    public static uint[] Initials(string name, uint tint = 0)
    {
        uint rgb = tint != 0 ? tint : Hue(name);
        using var bmp = new SKBitmap(new SKImageInfo(N, N, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        using (var c = new SKCanvas(bmp))
        {
            var bg = new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            c.Clear(bg);
            using var band = new SKPaint { Color = SKColors.White.WithAlpha(70) };
            c.DrawRect(0, N * 0.80f, N, N * 0.08f, band);   // a stripe so rolling is visible
            var ini = string.Concat(name.Where(char.IsLetterOrDigit).Take(2)).ToUpperInvariant(); if (ini.Length == 0) ini = "?";
            using var f = new SKFont(SKTypeface.Default, N * 0.42f) { Embolden = true };
            using var p = new SKPaint { Color = SKColors.White, IsAntialias = true };
            var w = Measure(f, ini);
            c.DrawText(ini, (N - w) / 2, N * 0.62f, f, p);
        }
        return Pixels(bmp);
    }
    /// <summary>Decodes an avatar (png/jpg/webp/gif first frame), centre-crops it square and scales to TexSize.</summary>
    public static uint[]? FromEncoded(byte[] data)
    {
        try
        {
            using var src = SKBitmap.Decode(data); if (src == null || src.Width < 2 || src.Height < 2) return null;
            int s = Math.Min(src.Width, src.Height);
            using var bmp = new SKBitmap(new SKImageInfo(N, N, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(SKColors.Gray);
                using var p = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                c.DrawBitmap(src, new SKRect((src.Width - s) / 2f, (src.Height - s) / 2f, (src.Width + s) / 2f, (src.Height + s) / 2f), new SKRect(0, 0, N, N), p);
            }
            return Pixels(bmp);
        }
        catch { return null; }
    }
    static uint[] Pixels(SKBitmap bmp)
    {
        var px = new uint[N * N];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), (int[])(object)px, 0, px.Length);
        for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000;   // opaque
        return px;
    }
}

/// <summary>
/// CPU (no GPU) perspective renderer: room (floor grid + far walls, cut-away near walls), floor shadows, per-pixel shaded
/// spheres with the avatar mapped onto them (rotating with the roll), depth sorted, then labels / pops / speech bubbles.
/// Output: BGRA premultiplied (Skia N32 on Windows and Linux), straight to ffmpeg rawvideo bgra.
/// </summary>
public sealed unsafe class BallPitRenderer : IDisposable
{
    static float Measure(SKFont f, string s) => BallPitTextures.Measure(f, s);
    public int Width { get; }
    public int Height { get; }
    public bool Transparent { get; set; }
    readonly SKBitmap _bmp; readonly SKCanvas _c;
    SKBitmap? _bgImg; string _bgPath = "";
    public SKBitmap Bitmap => _bmp;
    public BallPitRenderer(int w, int h)
    {
        Width = Math.Max(16, w & ~1); Height = Math.Max(16, h & ~1);
        _bmp = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _c = new SKCanvas(_bmp);
    }
    public void Dispose() { _c.Dispose(); _bmp.Dispose(); _bgImg?.Dispose(); }

    // camera
    Vector3 _cam, _right, _up, _fwd; float _f; float _cx, _cy;
    void Camera(BallPitSettings s, double time, double shake)
    {
        double yaw = (s.CameraYaw + (s.Orbit ? time * s.OrbitSpeed : 0)) * Math.PI / 180, pitch = Math.Clamp(s.CameraPitch, 0, 75) * Math.PI / 180;
        float dist = (float)(19.5 / Math.Clamp(s.CameraZoom, 0.4, 3));
        var target = new Vector3(0, 2.6f, 0);
        if (shake > 0) target += new Vector3((float)Math.Sin(time * 70) * 0.25f * (float)shake, (float)Math.Cos(time * 53) * 0.2f * (float)shake, 0);
        _cam = target + dist * new Vector3((float)(Math.Sin(yaw) * Math.Cos(pitch)), (float)Math.Sin(pitch), (float)(Math.Cos(yaw) * Math.Cos(pitch)));
        _fwd = Vector3.Normalize(target - _cam); _right = Vector3.Normalize(Vector3.Cross(_fwd, Vector3.UnitY)); _up = Vector3.Cross(_right, _fwd);
        _f = (float)(Height / 2 / Math.Tan(50 * Math.PI / 360)); _cx = Width / 2f; _cy = Height / 2f;
    }
    Vector3 View(Vector3 p) { var d = p - _cam; return new(Vector3.Dot(d, _right), Vector3.Dot(d, _up), Vector3.Dot(d, _fwd)); }
    bool Proj(Vector3 p, out SKPoint s) { var v = View(p); if (v.Z < 0.2f) { s = default; return false; } s = new(_cx + _f * v.X / v.Z, _cy - _f * v.Y / v.Z); return true; }

    static SKColor Col(string hex, SKColor fallback) => BallPitScene.ParseColor(hex) is uint c ? new SKColor((byte)(c >> 16), (byte)(c >> 8), (byte)c) : fallback;

    /// <summary>Renders the scene's current state (does not step it).</summary>
    public void Render(BallPitScene sc)
    {
        lock (sc.Sync)
        {
            var s = sc.Settings; double t = sc.Time;
            Camera(s, t, Math.Max(0, sc.ShakeUntil - t));
            if (Transparent) _c.Clear(SKColors.Transparent);
            else
            {
                _c.Clear(Col(s.BackgroundColor, new SKColor(15, 23, 42)));
                if (!string.IsNullOrWhiteSpace(s.BackgroundImage))
                {
                    if (_bgPath != s.BackgroundImage) { _bgImg?.Dispose(); _bgImg = null; _bgPath = s.BackgroundImage; try { if (File.Exists(_bgPath)) _bgImg = SKBitmap.Decode(_bgPath); } catch { } }
                    if (_bgImg != null) { using var bp = new SKPaint { FilterQuality = SKFilterQuality.Medium }; _c.DrawBitmap(_bgImg, new SKRect(0, 0, Width, Height), bp); }
                }
                if (s.ShowRoom) Room(s);
            }
            if (sc.Race != null) RaceTrack(sc.Race);
            var alive = sc.Balls;
            Shadows(alive);
            // depth sort, far → near
            var order = alive.Select(b => (b, z: View(b.Pos).Z)).Where(x => x.z > 0.3f).OrderByDescending(x => x.z).ToList();
            _c.Flush();
            _pulse = sc.BeatStrength > 0 ? sc.Pulse : 0;
            Spheres(order, t);
            Particles(sc, t);
            Overlays(order, s, t);
            if (s.ShowCount) Hud(sc.Balls.Count(b => !b.Leaving), sc.Waiting);
            if (sc.RadioCard is { } rc) RadioCard(rc);
            sc.Overlay?.Invoke(_c, Width, Height);   // v2.8 Engage cards (poll, leaderboard, race, toasts)
            _c.Flush();
        }
    }

    void Room(BallPitSettings s)
    {
        float X = BallPitScene.RoomX, Z = BallPitScene.RoomZ, H = BallPitScene.RoomH;
        var floor = Col(s.FloorColor, new SKColor(30, 41, 59)); var wall = Col(s.WallColor, new SKColor(38, 52, 74));
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        // far walls: inward normal faces the camera
        (Vector3 p, Vector3 n, Vector3[] q)[] walls =
        {
            (new(0, 0, -Z), new(0, 0, 1), new Vector3[] { new(-X, 0, -Z), new(X, 0, -Z), new(X, H, -Z), new(-X, H, -Z) }),
            (new(0, 0, Z), new(0, 0, -1), new Vector3[] { new(X, 0, Z), new(-X, 0, Z), new(-X, H, Z), new(X, H, Z) }),
            (new(-X, 0, 0), new(1, 0, 0), new Vector3[] { new(-X, 0, Z), new(-X, 0, -Z), new(-X, H, -Z), new(-X, H, Z) }),
            (new(X, 0, 0), new(-1, 0, 0), new Vector3[] { new(X, 0, -Z), new(X, 0, Z), new(X, H, Z), new(X, H, -Z) }),
        };
        Poly(new Vector3[] { new(-X, 0, -Z), new(X, 0, -Z), new(X, 0, Z), new(-X, 0, Z) }, floor, fill);
        using var grid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = SKColors.White.WithAlpha(22) };
        for (float x = -X; x <= X + 0.01f; x += 1) Line(new(x, 0, -Z), new(x, 0, Z), grid);
        for (float z = -Z; z <= Z + 0.01f; z += 1) Line(new(-X, 0, z), new(X, 0, z), grid);
        foreach (var (p, n, q) in walls)
        {
            if (Vector3.Dot(n, _cam - p) <= 0) continue;   // the camera is outside this wall: cut away (near wall)
            float shade = 0.85f + 0.15f * Math.Abs(n.X);
            Poly(q, new SKColor((byte)(wall.Red * shade), (byte)(wall.Green * shade), (byte)(wall.Blue * shade)), fill);
            using var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.White.WithAlpha(40) };
            Line(q[0], q[1], edge); Line(q[2], q[3], edge); Line(q[0], q[3], edge); Line(q[1], q[2], edge);
        }
    }
    void Poly(Vector3[] pts, SKColor c, SKPaint p)
    {
        using var path = new SKPath(); bool first = true;
        foreach (var v in pts) { if (!Proj(v, out var s)) return; if (first) path.MoveTo(s); else path.LineTo(s); first = false; }
        path.Close(); p.Color = c; _c.DrawPath(path, p);
    }
    void Line(Vector3 a, Vector3 b, SKPaint p) { if (Proj(a, out var s1) && Proj(b, out var s2)) _c.DrawLine(s1, s2, p); }

    void Shadows(List<Ball> balls)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var path = new SKPath();
        foreach (var b in balls)
        {
            float r = b.Radius, hgt = Math.Max(0, b.Pos.Y - r);
            float k = Math.Clamp(1 - hgt / 9f, 0.15f, 1), rr = r * (1 + hgt * 0.08f);
            byte a = (byte)(110 * k * (b.Leaving ? 0.4f : 1));
            for (int ring = 0; ring < 2; ring++)
            {
                path.Reset(); float rad = ring == 0 ? rr * 1.15f : rr * 0.8f; bool ok = true;
                for (int i = 0; i < 14; i++)
                {
                    double ang = i * Math.PI * 2 / 14;
                    if (!Proj(new Vector3(b.Pos.X + rad * (float)Math.Cos(ang), 0.01f, b.Pos.Z + rad * (float)Math.Sin(ang)), out var sp)) { ok = false; break; }
                    if (i == 0) path.MoveTo(sp); else path.LineTo(sp);
                }
                if (!ok) break;
                path.Close(); p.Color = new SKColor(0, 0, 0, (byte)(a / 2)); _c.DrawPath(path, p);
            }
        }
    }

    struct SphereJob { public float Cx, Cy, Rho, Alpha; public Vector3 A0, A1, A2; public uint[] Tex; public uint Tint; }
    static readonly Vector3 LightW = Vector3.Normalize(new Vector3(-0.45f, 0.85f, 0.4f));

    void Spheres(List<(Ball b, float z)> order, double t)
    {
        var jobs = new List<SphereJob>(order.Count);
        foreach (var (b, z) in order)
        {
            if (!Proj(b.Pos, out var sp)) continue;
            float scale = 1, alpha = 1;
            if (b.Leaving) { float u = (float)Math.Clamp((t - b.LeaveAt) / 0.45, 0, 1); scale = 1 + 0.35f * u; alpha = 1 - u; }
            else if (t - b.JoinedAt < 0.25) scale = (float)(0.4 + 0.6 * (t - b.JoinedAt) / 0.25);
            float rho = _f * b.Radius * scale * (1 + 0.08f * _pulse) / z;
            if (rho < 0.8f || sp.X + rho < 0 || sp.X - rho > Width || sp.Y + rho < 0 || sp.Y - rho > Height) continue;
            // view-space normal n=(nx,ny,nz) (nz toward camera) → world: right*nx + up*ny - fwd*nz → object: dot with each axis
            Vector3 Row(Vector3 ax) => new(Vector3.Dot(ax, _right), Vector3.Dot(ax, _up), -Vector3.Dot(ax, _fwd));
            jobs.Add(new SphereJob { Cx = sp.X, Cy = sp.Y, Rho = rho, Alpha = alpha, A0 = Row(b.AX), A1 = Row(b.AY), A2 = Row(b.AZ), Tex = b.Texture ?? BallPitTextures.Initials(b.Name), Tint = b.HasAvatar ? b.Tint : 0 });
        }
        var lv = new Vector3(Vector3.Dot(LightW, _right), Vector3.Dot(LightW, _up), -Vector3.Dot(LightW, _fwd));
        var hv = Vector3.Normalize(lv + new Vector3(0, 0, 1));
        var basePtr = (uint*)_bmp.GetPixels(); int W = Width, H = Height;
        int bands = Math.Clamp(Environment.ProcessorCount, 1, 16), bh = (H + bands - 1) / bands;
        Parallel.For(0, bands, bi =>
        {
            int y0 = bi * bh, y1 = Math.Min(H, y0 + bh);
            foreach (var j in jobs) Shade(basePtr, W, y0, y1, j, lv, hv);
        });
    }

    static void Shade(uint* dst, int W, int by0, int by1, in SphereJob j, Vector3 lv, Vector3 hv)
    {
        int y0 = Math.Max(by0, (int)MathF.Floor(j.Cy - j.Rho - 1)), y1 = Math.Min(by1, (int)MathF.Ceiling(j.Cy + j.Rho + 1));
        int x0 = Math.Max(0, (int)MathF.Floor(j.Cx - j.Rho - 1)), x1 = Math.Min(W, (int)MathF.Ceiling(j.Cx + j.Rho + 1));
        if (y0 >= y1 || x0 >= x1) return;
        float inv = 1 / j.Rho; const int N = BallPitScene.TexSize; var tex = j.Tex;
        float tr = ((j.Tint >> 16) & 255) / 255f, tg = ((j.Tint >> 8) & 255) / 255f, tb = (j.Tint & 255) / 255f; bool tint = j.Tint != 0;
        for (int y = y0; y < y1; y++)
        {
            float ny = -((y + 0.5f) - j.Cy) * inv; uint* row = dst + (long)y * W;
            for (int x = x0; x < x1; x++)
            {
                float nx = ((x + 0.5f) - j.Cx) * inv, d2 = nx * nx + ny * ny;
                if (d2 >= 1.0f + 2 * inv) continue;
                float d = MathF.Sqrt(d2), cov = Math.Clamp((1 - d) * j.Rho + 0.5f, 0, 1) * j.Alpha;
                if (cov <= 0.003f) continue;
                if (d2 > 1) { nx /= d; ny /= d; d2 = 1; }
                float nz = MathF.Sqrt(1 - d2);
                float ox = j.A0.X * nx + j.A0.Y * ny + j.A0.Z * nz, oy = j.A1.X * nx + j.A1.Y * ny + j.A1.Z * nz, oz = j.A2.X * nx + j.A2.Y * ny + j.A2.Z * nz;
                float u = oz >= 0 ? 0.5f + 0.5f * ox : 0.5f - 0.5f * ox, v = 0.5f - 0.5f * oy;   // avatar decal on front and back
                int tu = Math.Clamp((int)(u * N), 0, N - 1), tvv = Math.Clamp((int)(v * N), 0, N - 1);
                uint tc = tex[tvv * N + tu];
                float r = ((tc >> 16) & 255) / 255f, g = ((tc >> 8) & 255) / 255f, b = (tc & 255) / 255f;
                if (tint) { r = r * 0.6f + tr * 0.4f; g = g * 0.6f + tg * 0.4f; b = b * 0.6f + tb * 0.4f; }
                float diff = Math.Max(0, lv.X * nx + lv.Y * ny + lv.Z * nz);
                float sh = 0.32f + 0.78f * diff;
                float rim = 1 - 0.25f * d2 * d2;
                float spec = hv.X * nx + hv.Y * ny + hv.Z * nz; spec = spec > 0 ? MathF.Pow(spec, 40) * 0.75f : 0;
                r = Math.Min(1, r * sh * rim + spec); g = Math.Min(1, g * sh * rim + spec); b = Math.Min(1, b * sh * rim + spec);
                // premultiplied "over"
                uint dp = row[x]; float ia = 1 - cov;
                uint ob = (uint)(b * 255 * cov + (dp & 255) * ia), og = (uint)(g * 255 * cov + ((dp >> 8) & 255) * ia),
                     orr = (uint)(r * 255 * cov + ((dp >> 16) & 255) * ia), oa = (uint)(255 * cov + (dp >> 24) * ia);
                row[x] = (Math.Min(255, oa) << 24) | (Math.Min(255, orr) << 16) | (Math.Min(255, og) << 8) | Math.Min(255, ob);
            }
        }
    }

    void Particles(BallPitScene sc, double t)
    {
        using var p = new SKPaint { IsAntialias = true };
        foreach (var q in sc.Particles)
        {
            if (!Proj(q.P, out var s)) continue; var z = View(q.P).Z;
            float life = (float)Math.Clamp((q.Until - t) / 0.8, 0, 1);
            p.Color = new SKColor((byte)(q.C >> 16), (byte)(q.C >> 8), (byte)q.C, (byte)(255 * life));
            _c.DrawCircle(s, Math.Max(1.5f, _f * 0.09f / z), p);
        }
    }

    SKFont? _font, _bold;
    void Overlays(List<(Ball b, float z)> order, BallPitSettings s, double t)
    {
        _font ??= new SKFont(SKTypeface.Default, 14); _bold ??= new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default, 18);
        using var text = new SKPaint { IsAntialias = true };
        using var bg = new SKPaint { IsAntialias = true };
        bool many = order.Count > 120;
        foreach (var (b, z) in order)   // far → near so close labels win
        {
            if (b.Leaving || !Proj(b.Pos, out var sp)) continue;
            float rho = _f * b.Radius / z, top = sp.Y - rho;
            if (s.Labels && rho >= (many ? 11 : 7))
            {
                _font.Size = Math.Clamp(rho * 0.55f, 11, 20);
                var name = b.Name.Length > 18 ? b.Name[..17] + "…" : b.Name;
                float w = Measure(_font, name), hgt = _font.Size + 6;
                var rc = new SKRect(sp.X - w / 2 - 6, top - hgt - 4, sp.X + w / 2 + 6, top - 4);
                bg.Color = new SKColor(0, 0, 0, 140); _c.DrawRoundRect(rc, hgt / 2, hgt / 2, bg);
                text.Color = new SKColor((byte)(b.LabelColor >> 16), (byte)(b.LabelColor >> 8), (byte)b.LabelColor);
                _c.DrawText(name, sp.X - w / 2, rc.Bottom - 5 - (hgt - 6 - _font.Size * 0.75f) / 2, _font, text);
                top = rc.Top;
            }
            if (b.Bubble != null && t < b.BubbleUntil) Bubble(b.Bubble, sp.X, top - 6, (float)Math.Clamp((b.BubbleUntil - t) / 0.4, 0, 1));
            if (b.PopText != null && t < b.PopUntil)
            {
                float age = (float)(t - (b.PopUntil - 2.8)), a = Math.Clamp((float)((b.PopUntil - t) / 0.6), 0, 1);
                _bold.Size = Math.Clamp(Height / 26f, 16, 34) * (age < 0.2f ? 0.6f + 2f * age : 1);
                float w = Measure(_bold, b.PopText);
                float y = Math.Max(_bold.Size + 4, top - 10 - age * 28);
                text.Color = new SKColor(0, 0, 0, (byte)(200 * a)); _c.DrawText(b.PopText, sp.X - w / 2 + 2, y + 2, _bold, text);
                text.Color = new SKColor(253, 224, 71, (byte)(255 * a)); _c.DrawText(b.PopText, sp.X - w / 2, y, _bold, text);
            }
        }
    }
    void Bubble(string msg, float x, float bottom, float a)
    {
        _font!.Size = Math.Clamp(Height / 40f, 12, 20);
        float w = Measure(_font, msg), h = _font.Size + 12;
        x = Math.Clamp(x, w / 2 + 10, Width - w / 2 - 10);
        var rc = new SKRect(x - w / 2 - 10, bottom - h - 8, x + w / 2 + 10, bottom - 8);
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(235 * a)) };
        _c.DrawRoundRect(rc, 10, 10, p);
        using var tail = new SKPath(); tail.MoveTo(x - 7, rc.Bottom - 1); tail.LineTo(x + 7, rc.Bottom - 1); tail.LineTo(x, bottom); tail.Close(); _c.DrawPath(tail, p);
        p.Color = new SKColor(17, 24, 39, (byte)(255 * a)); _c.DrawText(msg, rc.Left + 10, rc.Bottom - 6 - (h - 12 - _font.Size * 0.75f) / 2, _font, p);
    }
    float _pulse; SKBitmap? _art; string _artKey = "";
    static string Fmt(double s) { var t = TimeSpan.FromSeconds(Math.Max(0, s)); return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss"); }
    string Fit(SKFont f, string s, float w)
    {
        if (Measure(f, s) <= w) return s;
        while (s.Length > 1 && Measure(f, s + "…") > w) s = s[..^1];
        return s + "…";
    }
    /// <summary>Radio card, top-left: album art, ON AIR · station, title, artist, progress, up next, listeners.</summary>
    void RadioCard(RadioCardInfo r)
    {
        _bold ??= new SKFont(SKTypeface.Default, 18); _font ??= new SKFont(SKTypeface.Default, 16);
        float H = Math.Clamp(Height * 0.17f, 70, 200), W = Math.Clamp(Width * 0.42f, 260, 760), pad = H * 0.09f, x0 = 16, y0 = 16;
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(10, 12, 24, 200) };
        _c.DrawRoundRect(new SKRect(x0, y0, x0 + W, y0 + H), 14, 14, p);
        float art = H - 2 * pad, tx = x0 + pad;
        if (r.ArtKey != _artKey) { _art?.Dispose(); _art = null; _artKey = r.ArtKey; try { if (r.Art != null) _art = SKBitmap.Decode(r.Art); } catch { } }
        var ar = new SKRect(tx, y0 + pad, tx + art, y0 + pad + art);
        if (_art != null)
        {
            using var ip = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
            _c.Save(); using (var clip = new SKRoundRect(ar, 8, 8)) _c.ClipRoundRect(clip, antialias: true); _c.DrawBitmap(_art, ar, ip); _c.Restore();
        }
        else { p.Color = new SKColor(60, 70, 110); _c.DrawRoundRect(ar, 8, 8, p); p.Color = SKColors.White; _bold.Size = art * 0.5f; _c.DrawText("♪", ar.MidX - Measure(_bold, "♪") / 2, ar.MidY + art * 0.18f, _bold, p); }
        tx += art + pad; float tw = x0 + W - pad - tx, y = y0 + pad;
        // ON AIR · station  (+ listeners right-aligned)
        _bold.Size = H * 0.13f; y += _bold.Size;
        p.Color = r.Online ? new SKColor(239, 68, 68) : new SKColor(120, 120, 130);
        _c.DrawCircle(tx + _bold.Size * 0.35f, y - _bold.Size * 0.35f, _bold.Size * (0.28f + 0.08f * _pulse), p);
        var label = (r.Online ? "ON AIR" : "OFF AIR") + (r.Station.Length > 0 ? " · " + r.Station : "");
        p.Color = new SKColor(250, 204, 21); float lx = tx + _bold.Size * 0.9f;
        string lis = r.Listeners is int n ? $"{n} listening" : "";
        _font.Size = H * 0.11f; float lw = lis.Length > 0 ? Measure(_font, lis) + pad : 0;
        _c.DrawText(Fit(_bold, label, tw - (lx - tx) - lw), lx, y, _bold, p);
        if (lis.Length > 0) { p.Color = new SKColor(203, 213, 225); _c.DrawText(lis, x0 + W - pad - Measure(_font, lis), y, _font, p); }
        // title, artist
        _bold.Size = H * 0.19f; y += _bold.Size * 1.15f; p.Color = SKColors.White;
        _c.DrawText(Fit(_bold, r.Title.Length > 0 ? r.Title : "—", tw), tx, y, _bold, p);
        _font.Size = H * 0.13f;
        if (r.Artist.Length > 0) { y += _font.Size * 1.25f; p.Color = new SKColor(226, 232, 240); _c.DrawText(Fit(_font, r.Artist, tw), tx, y, _font, p); }
        // progress + time
        float by = y0 + H - pad - H * 0.04f, small = H * 0.10f; _font.Size = small;
        if (r.Duration > 0)
        {
            var pos = r.PositionAt(DateTime.UtcNow); var tt = $"{Fmt(pos)} / {Fmt(r.Duration)}"; float ttw = Measure(_font, tt);
            float bw = tw - ttw - pad; var track = new SKRect(tx, by - H * 0.035f, tx + bw, by);
            p.Color = new SKColor(255, 255, 255, 60); _c.DrawRoundRect(track, 3, 3, p);
            p.Color = new SKColor(250, 204, 21); _c.DrawRoundRect(new SKRect(track.Left, track.Top, track.Left + bw * (float)(pos / r.Duration), track.Bottom), 3, 3, p);
            p.Color = new SKColor(203, 213, 225); _c.DrawText(tt, x0 + W - pad - ttw, by + 1, _font, p);
            by -= H * 0.06f;
        }
        if (r.Next.Length > 0) { p.Color = new SKColor(148, 163, 184); _c.DrawText(Fit(_font, "Up next: " + r.Next, tw), tx, by - (r.Duration > 0 ? 2 : 0), _font, p); }
    }

    /// <summary>v2.8 race lanes + chequered finish line on the floor.</summary>
    void RaceTrack(RaceGame race)
    {
        int n = race.Racers.Count; float y = 0.01f;
        using var p = new SKPaint { IsAntialias = true, StrokeWidth = 2, Style = SKPaintStyle.Stroke, Color = new SKColor(255, 255, 255, 70) };
        float half = n <= 1 ? BallPitScene.RoomZ : (2 * BallPitScene.RoomZ - 1.8f) / (n - 1) / 2;
        for (int i = 0; i <= n; i++)
        {
            float z = i < n ? BallPitScene.LaneZ(i, n) - half : BallPitScene.LaneZ(n - 1, n) + half;
            if (Proj(new Vector3(BallPitScene.RaceStartX, y, z), out var a) && Proj(new Vector3(BallPitScene.RaceFinishX, y, z), out var b)) _c.DrawLine(a, b, p);
        }
        p.Style = SKPaintStyle.Fill;
        float z0 = BallPitScene.LaneZ(0, n) - half, z1 = BallPitScene.LaneZ(n - 1, n) + half; int cells = 16; float fx = BallPitScene.RaceFinishX + BallPitScene.RoomX * 0.0f;
        for (int r = 0; r < 2; r++)
            for (int k = 0; k < cells; k++)
            {
                float za = z0 + (z1 - z0) * k / cells, zb = z0 + (z1 - z0) * (k + 1) / cells, xa = fx + r * 0.35f, xb = xa + 0.35f;
                if (!Proj(new Vector3(xa, y, za), out var q0) || !Proj(new Vector3(xb, y, za), out var q1) || !Proj(new Vector3(xb, y, zb), out var q2) || !Proj(new Vector3(xa, y, zb), out var q3)) continue;
                p.Color = (k + r) % 2 == 0 ? new SKColor(255, 255, 255, 220) : new SKColor(10, 10, 10, 220);
                using var path = new SKPath(); path.MoveTo(q0); path.LineTo(q1); path.LineTo(q2); path.LineTo(q3); path.Close(); _c.DrawPath(path, p);
            }
    }

    void Hud(int n, int waiting)
    {
        _bold ??= new SKFont(SKTypeface.Default, 18); _bold.Size = Math.Clamp(Height / 34f, 12, 26);
        var s = $"{n} in the ball pit" + (waiting > 0 ? $" (+{waiting} waiting)" : "");
        float w = Measure(_bold, s);
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 130) };
        var rc = new SKRect(Width - w - 34, Height - _bold.Size - 30, Width - 14, Height - 14);
        _c.DrawRoundRect(rc, 8, 8, p); p.Color = SKColors.White; _c.DrawText(s, rc.Left + 10, rc.Bottom - 10, _bold, p);
    }

    /// <summary>Copies the frame bytes (BGRA premultiplied, stride = Width*4).</summary>
    public ReadOnlySpan<byte> Bytes => new((void*)_bmp.GetPixels(), Width * Height * 4);
    public void SavePng(string path) { using var img = SKImage.FromBitmap(_bmp); using var d = img.Encode(SKEncodedImageFormat.Png, 90); using var f = File.Create(path); d.SaveTo(f); }
}
