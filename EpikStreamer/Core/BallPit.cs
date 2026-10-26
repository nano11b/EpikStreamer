using System.IO;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
namespace EpikStreamer.Core;

/// <summary>v2.5 "Ball Pit": every room member becomes a bouncy, avatar-textured ball in a CPU-rendered 3D room.</summary>
public class BallPitSettings
{
    public string Mode { get; set; } = "Off";          // Off | Source (replaces the video) | Overlay (transparent, on top of the playlist)
    public double RenderScale { get; set; } = 1.0;     // render at a fraction of the output size (ffmpeg scales up); 0.5..1
    public int MaxBalls { get; set; } = 200;           // hard cap 300; extra people wait for a free slot
    public double BallSize { get; set; } = 0.6;        // radius in room units (room is 16 × 10)
    public double Gravity { get; set; } = 14;          // units/s²
    public double Bounciness { get; set; } = 0.6;      // restitution 0..0.95
    public double Friction { get; set; } = 0.8;        // rolling friction per second
    public double CameraYaw { get; set; } = 0;         // degrees
    public double CameraPitch { get; set; } = 22;      // degrees
    public double CameraZoom { get; set; } = 1.0;
    public bool Orbit { get; set; } = true;
    public double OrbitSpeed { get; set; } = 4;        // degrees/s
    public string BackgroundColor { get; set; } = "#0F172A";
    public string BackgroundImage { get; set; } = "";
    public string FloorColor { get; set; } = "#1E293B";
    public string WallColor { get; set; } = "#26344A";
    public bool ShowRoom { get; set; } = true;
    public bool Labels { get; set; } = true;
    public bool ShowCount { get; set; } = true;
    public bool JoinEvents { get; set; } = true;
    public bool LeaveEvents { get; set; } = true;
    public bool ChatBubbles { get; set; } = true;
    public bool Commands { get; set; } = true;
    public bool ShakeHostOnly { get; set; }
    public int ShakeCooldownSeconds { get; set; } = 30;
    public bool IncludeViewers { get; set; } = true;
    public bool IncludeChatters { get; set; } = true;
    public int ChatterMinutes { get; set; } = 20;
    public int ScanSeconds { get; set; } = 60;         // full (scrolling) member-list scan interval
    public BallPitSettings Clone() => (BallPitSettings)MemberwiseClone();
    public bool Active => Mode is "Source" or "Overlay";
}

public sealed class Ball
{
    public string Name = "", Key = "";
    public Vector3 Pos, Vel;
    public Vector3 AX = Vector3.UnitX, AY = Vector3.UnitY, AZ = Vector3.UnitZ;   // orientation (object axes in world space)
    public float BaseRadius = 0.6f, SizeMul = 1f; public double BigUntil;
    public float Radius => BaseRadius * SizeMul;
    public uint[]? Texture;            // TexSize² BGRA (straight alpha)
    public bool HasAvatar; public string? AvatarUrl;
    public uint Tint;                  // 0 = none, else 0xRRGGBB (from !color)
    public uint LabelColor = 0xFFFFFF; public bool CustomLabelColor;
    public double JoinedAt, LeaveAt = -1;  // LeaveAt >= 0: popping out
    public string? Bubble; public double BubbleUntil;
    public string? PopText; public double PopUntil;
    public bool OnFloor; public int Sources;
    public bool Leaving => LeaveAt >= 0;
}

/// <summary>Physics + people. Thread-safe: every public member locks <see cref="Sync"/>.</summary>
public sealed class BallPitScene
{
    public const float RoomX = 8f, RoomZ = 5f, RoomH = 9f, Ceiling = 14f;
    public const int TexSize = 64, HardCap = 300;
    public readonly object Sync = new();
    public BallPitSettings Settings { get; set; }
    public readonly List<Ball> Balls = new();
    readonly List<(string Name, string? Avatar, string? Color)> _waiting = new();
    public readonly List<(Vector3 P, Vector3 V, uint C, double Until)> Particles = new();
    public double Time { get; private set; }
    public double ShakeUntil { get; private set; }
    double _lastShake = double.MinValue;
    readonly Random _rnd;
    public Func<string, uint[]?>? TextureFor;     // host: cached avatar texture by URL (null = not ready)

    public BallPitScene(BallPitSettings s, int seed = 0) { Settings = s; _rnd = seed == 0 ? new Random() : new Random(seed); }

    public int Count { get { lock (Sync) return Balls.Count(b => !b.Leaving); } }
    public int Waiting { get { lock (Sync) return _waiting.Count; } }
    public IReadOnlyList<string> Names() { lock (Sync) return Balls.Where(b => !b.Leaving).Select(b => b.Name).Concat(_waiting.Select(w => w.Name)).ToList(); }
    public bool Has(string name) { lock (Sync) return Find(name) != null || _waiting.Any(w => Key(w.Name) == Key(name)); }

    public static string Key(string name) => (name ?? "").Trim().ToLowerInvariant();
    Ball? Find(string name) { var k = Key(name); return Balls.FirstOrDefault(b => b.Key == k && !b.Leaving); }
    int Cap => Math.Clamp(Settings.MaxBalls, 1, HardCap);

    /// <summary>Radius after crowd scaling: many balls → smaller, so the floor isn't a solid wall.</summary>
    public float EffectiveRadius(int n) => (float)(Math.Clamp(Settings.BallSize, 0.15, 2.0) * Math.Clamp(Math.Sqrt(70.0 / Math.Max(1, n)), 0.5, 1.0));

    float Rand(float a, float b) => a + (float)_rnd.NextDouble() * (b - a);

    /// <summary>Adds a person (drops in from the top with a "joined" pop label). False if already there.</summary>
    public bool Join(string name, string? avatarUrl = null, string? color = null, bool announce = true)
    {
        name = (name ?? "").Trim(); if (name.Length == 0) return false;
        lock (Sync)
        {
            var b0 = Find(name);
            if (b0 != null) { Update(b0, avatarUrl, color); return false; }
            if (_waiting.Any(w => Key(w.Name) == Key(name))) return false;
            if (Balls.Count(b => !b.Leaving) >= Cap) { _waiting.Add((name, avatarUrl, color)); return true; }
            Spawn(name, avatarUrl, color, announce);
            return true;
        }
    }
    void Spawn(string name, string? avatarUrl, string? color, bool announce)
    {
        var r = EffectiveRadius(Balls.Count + 1);
        var b = new Ball
        {
            Name = name, Key = Key(name), BaseRadius = r, JoinedAt = Time,
            Pos = new(Rand(-RoomX + r, RoomX - r), RoomH + Rand(0, 2.5f), Rand(-RoomZ + r, RoomZ - r)),
            Vel = new(Rand(-1.5f, 1.5f), Rand(-2, 0), Rand(-1.5f, 1.5f)),
        };
        var ax = Vector3.Normalize(new Vector3(Rand(-1, 1), Rand(-1, 1), Rand(-1, 1)) + new Vector3(0.01f, 0, 0));
        Rotate(b, ax, Rand(0, 6.28f));
        Update(b, avatarUrl, color);
        if (b.Texture == null) b.Texture = BallPitTextures.Initials(name, b.Tint);
        if (announce && Settings.JoinEvents) { b.PopText = name + " joined"; b.PopUntil = Time + 2.8; }
        Balls.Add(b);
        foreach (var o in Balls) if (!o.Leaving) o.BaseRadius = r;   // keep everyone the same (crowd-scaled) size
    }
    void Update(Ball b, string? avatarUrl, string? color)
    {
        if (!string.IsNullOrEmpty(avatarUrl) && avatarUrl != b.AvatarUrl)
        {
            b.AvatarUrl = avatarUrl;
            if (TextureFor?.Invoke(avatarUrl) is { } t) { b.Texture = t; b.HasAvatar = true; }
        }
        if (ParseColor(color) is uint c && !b.CustomLabelColor) b.LabelColor = c;
    }

    /// <summary>Host: an avatar finished downloading.</summary>
    public void AvatarReady(string url, uint[] tex)
    {
        lock (Sync) foreach (var b in Balls) if (b.AvatarUrl == url) { b.Texture = tex; b.HasAvatar = true; }
    }

    /// <summary>Removes a person (pop + particles, or immediately when leave events are off).</summary>
    public bool Leave(string name)
    {
        lock (Sync)
        {
            int wi = _waiting.FindIndex(w => Key(w.Name) == Key(name));
            if (wi >= 0) { _waiting.RemoveAt(wi); return true; }
            var b = Find(name); if (b == null) return false;
            if (Settings.LeaveEvents)
            {
                b.LeaveAt = Time;
                for (int i = 0; i < 14; i++)
                {
                    var d = Vector3.Normalize(new Vector3(Rand(-1, 1), Rand(-0.2f, 1), Rand(-1, 1)) + new Vector3(0, 0.3f, 0));
                    Particles.Add((b.Pos, d * Rand(3, 6), b.Tint != 0 ? b.Tint : b.LabelColor, Time + Rand(0.5f, 0.9f)));
                }
            }
            else Balls.Remove(b);
            PromoteWaiting();
            return true;
        }
    }
    void PromoteWaiting()
    {
        while (_waiting.Count > 0 && Balls.Count(b => !b.Leaving) < Cap) { var w = _waiting[0]; _waiting.RemoveAt(0); Spawn(w.Name, w.Avatar, w.Color, true); }
    }

    public void Jump(Ball b, float strength = 1f)
    {
        b.Vel += new Vector3(Rand(-1.5f, 1.5f), (8.5f + Rand(0, 2.5f)) * strength, Rand(-1.5f, 1.5f));
    }

    /// <summary>A chat line: that person's ball jumps and shows a speech bubble (they're added first if chatters are included).</summary>
    public void Chat(string user, string text)
    {
        lock (Sync)
        {
            var b = Find(user);
            if (b == null && Settings.IncludeChatters) { Join(user); b = Find(user); }
            if (b == null) return;
            Jump(b, 0.8f);
            if (Settings.ChatBubbles && !string.IsNullOrWhiteSpace(text))
            {
                var t = Regex.Replace(text.Trim(), @"\s+", " ");
                b.Bubble = t.Length > 42 ? t[..40] + "…" : t; b.BubbleUntil = Time + 4.5;
            }
        }
    }

    public static readonly string[] CommandNames = { "!bounce", "!bigball", "!color", "!shake" };

    /// <summary>Ball Pit chat commands. Returns what happened (for the log) or null if not applicable/ignored.</summary>
    public string? Command(string user, string cmd, string arg, bool isHost)
    {
        if (!Settings.Commands) return null;
        lock (Sync)
        {
            var b = Find(user);
            if (b == null && cmd != "!shake" && Settings.IncludeChatters) { Join(user); b = Find(user); }
            switch (cmd)
            {
                case "!bounce": if (b == null) return null; Jump(b, 1.3f); return user + " bounced";
                case "!bigball": if (b == null) return null; b.SizeMul = 1.8f; b.BigUntil = Time + 60; Jump(b, 0.6f); return user + " is a big ball for 60 s";
                case "!color":
                    if (b == null || ParseColor(arg) is not uint c) return null;
                    b.Tint = c; b.LabelColor = c; b.CustomLabelColor = true;
                    if (!b.HasAvatar) b.Texture = BallPitTextures.Initials(b.Name, c);
                    return $"{user} → #{c:X6}";
                case "!shake":
                    if (Settings.ShakeHostOnly && !isHost) return null;
                    if (!isHost && Time - _lastShake < Math.Max(5, Settings.ShakeCooldownSeconds)) return null;
                    _lastShake = Time; ShakeUntil = Time + 0.8;
                    foreach (var o in Balls) if (!o.Leaving) Jump(o, Rand(0.8f, 1.4f));
                    return "shake!";
            }
            return null;
        }
    }

    public static uint? ParseColor(string? s)
    {
        s = (s ?? "").Trim();
        var m = Regex.Match(s, @"^#?([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$");
        if (m.Success)
        {
            var h = m.Groups[1].Value; if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
            return uint.Parse(h, NumberStyles.HexNumber);
        }
        m = Regex.Match(s, @"^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)");
        if (m.Success) return (uint)((Math.Min(255, int.Parse(m.Groups[1].Value)) << 16) | (Math.Min(255, int.Parse(m.Groups[2].Value)) << 8) | Math.Min(255, int.Parse(m.Groups[3].Value)));
        return null;
    }

    public void AddTestUsers(int n)
    {
        string[] a = { "Bouncy", "Sparkle", "Neon", "Pixel", "Turbo", "Cosmic", "Jelly", "Mango", "Disco", "Ziggy", "Fizz", "Comet" };
        string[] c = { "#F87171", "#FBBF24", "#34D399", "#60A5FA", "#A78BFA", "#F472B6", "" };
        for (int i = 0; i < n; i++) { var name = $"{a[_rnd.Next(a.Length)]}{_rnd.Next(10, 999)}"; Join(name, null, c[_rnd.Next(c.Length)]); }
    }
    public void Clear() { lock (Sync) { Balls.Clear(); _waiting.Clear(); Particles.Clear(); } }

    static void Rotate(Ball b, Vector3 axis, float ang)
    {
        var q = Quaternion.CreateFromAxisAngle(axis, ang);
        b.AX = Vector3.Transform(b.AX, q); b.AY = Vector3.Transform(b.AY, q); b.AZ = Vector3.Transform(b.AZ, q);
    }

    /// <summary>Advances the simulation by <paramref name="dt"/> seconds (fixed sub-steps ≤ 1/120 s).</summary>
    public void Step(double dt)
    {
        lock (Sync)
        {
            int n = Math.Max(1, (int)Math.Ceiling(dt * 120)); float h = (float)(dt / n);
            for (int i = 0; i < n; i++) SubStep(h);
            Time += dt;
            Balls.RemoveAll(b => b.Leaving && Time - b.LeaveAt > 0.45);
            Particles.RemoveAll(p => p.Until < Time);
            for (int i = 0; i < Particles.Count; i++) { var p = Particles[i]; p.V.Y -= (float)(Settings.Gravity * 0.6 * dt); p.P += p.V * (float)dt; Particles[i] = p; }
            foreach (var b in Balls) if (b.SizeMul != 1 && Time > b.BigUntil) b.SizeMul = Math.Max(1, b.SizeMul - (float)dt * 2);
            PromoteWaiting();
            if (_orthoCount++ % 60 == 0) foreach (var b in Balls) Orthonormalize(b);
        }
    }
    int _orthoCount;
    static void Orthonormalize(Ball b)
    {
        b.AX = Vector3.Normalize(b.AX); b.AY = Vector3.Normalize(b.AY - Vector3.Dot(b.AY, b.AX) * b.AX); b.AZ = Vector3.Cross(b.AX, b.AY);
    }

    void SubStep(float h)
    {
        float g = (float)Math.Clamp(Settings.Gravity, 0, 60), e = (float)Math.Clamp(Settings.Bounciness, 0, 0.95), fr = (float)Math.Clamp(Settings.Friction, 0, 10);
        var bs = Balls;
        foreach (var b in bs)
        {
            b.Vel.Y -= g * h;
            b.Vel *= 1 - 0.02f * h;   // air drag
            b.Pos += b.Vel * h;
            b.OnFloor = false;
        }
        for (int it = 0; it < 3; it++)
        {
            for (int i = 0; i < bs.Count; i++)
            {
                var a = bs[i]; if (a.Leaving) continue; float ra = a.Radius;
                for (int j = i + 1; j < bs.Count; j++)
                {
                    var b = bs[j]; if (b.Leaving) continue; float rb = b.Radius, rs = ra + rb;
                    var d = b.Pos - a.Pos;
                    if (Math.Abs(d.X) >= rs || Math.Abs(d.Y) >= rs || Math.Abs(d.Z) >= rs) continue;
                    float d2 = d.LengthSquared(); if (d2 >= rs * rs) continue;
                    float dist = MathF.Sqrt(d2); var nrm = dist > 1e-5f ? d / dist : new Vector3(0, 1, 0);
                    float wa = 1 / (ra * ra * ra), wb = 1 / (rb * rb * rb), ws = wa + wb;
                    float pen = rs - dist;
                    a.Pos -= nrm * (pen * wa / ws); b.Pos += nrm * (pen * wb / ws);
                    float vrel = Vector3.Dot(b.Vel - a.Vel, nrm);
                    if (vrel < 0)
                    {
                        float ee = vrel < -1.0f ? e : 0f;
                        float jimp = -(1 + ee) * vrel / ws;
                        a.Vel -= nrm * (jimp * wa); b.Vel += nrm * (jimp * wb);
                    }
                    if (nrm.Y < -0.5f) a.OnFloor = true; if (nrm.Y > 0.5f) b.OnFloor = true;   // resting on another ball
                }
            }
            foreach (var b in bs) Walls(b, e);
        }
        foreach (var b in bs)
        {
            if (b.OnFloor)
            {
                float k = Math.Max(0, 1 - fr * h);
                b.Vel.X *= k; b.Vel.Z *= k;
                var hv = new Vector3(b.Vel.X, 0, b.Vel.Z);
                var w = Vector3.Cross(Vector3.UnitY, hv) / b.Radius;   // rolling without slipping
                float wl = w.Length(); if (wl > 1e-4f) Rotate(b, w / wl, wl * h);
            }
        }
    }

    static void Walls(Ball b, float e)
    {
        float r = b.Radius;
        if (b.Pos.X < -RoomX + r) { b.Pos.X = -RoomX + r; if (b.Vel.X < 0) b.Vel.X = -b.Vel.X * e; }
        if (b.Pos.X > RoomX - r) { b.Pos.X = RoomX - r; if (b.Vel.X > 0) b.Vel.X = -b.Vel.X * e; }
        if (b.Pos.Z < -RoomZ + r) { b.Pos.Z = -RoomZ + r; if (b.Vel.Z < 0) b.Vel.Z = -b.Vel.Z * e; }
        if (b.Pos.Z > RoomZ - r) { b.Pos.Z = RoomZ - r; if (b.Vel.Z > 0) b.Vel.Z = -b.Vel.Z * e; }
        if (b.Pos.Y > Ceiling - r) { b.Pos.Y = Ceiling - r; if (b.Vel.Y > 0) b.Vel.Y = -b.Vel.Y * e; }
        if (b.Pos.Y < r)
        {
            b.Pos.Y = r; b.OnFloor = true;
            if (b.Vel.Y < 0) b.Vel.Y = b.Vel.Y < -1.2f ? -b.Vel.Y * e : 0;
        }
    }

    /// <summary>Total kinetic-ish energy (tests).</summary>
    public double Energy() { lock (Sync) return Balls.Sum(b => 0.5 * b.Vel.LengthSquared() + Settings.Gravity * b.Pos.Y); }
}

/// <summary>Merges the room member list, the LIVE viewers list and chat authors into Join/Leave calls (pure, unit-tested).</summary>
public sealed class BallPitRoster
{
    public record Member(string Name, string? Avatar = null, string? Color = null);
    readonly Dictionary<string, (Member M, bool Member, bool Viewer, DateTime LastChat, int Missing)> _p = new();
    public Action<Member> OnJoin = _ => { }; public Action<string> OnLeave = _ => { };
    public BallPitSettings Settings { get; set; }
    public int? ReportedTotal { get; private set; }
    public BallPitRoster(BallPitSettings s) => Settings = s;
    public int Count => _p.Count;
    public IEnumerable<Member> People => _p.Values.Select(v => v.M);

    void Upsert(Member m, bool member, bool viewer, DateTime? chat)
    {
        var k = BallPitScene.Key(m.Name); if (k.Length == 0) return;
        if (_p.TryGetValue(k, out var e))
        {
            var mm = new Member(e.M.Name, m.Avatar ?? e.M.Avatar, m.Color ?? e.M.Color);
            bool changed = mm != e.M;
            _p[k] = (mm, e.Member || member, e.Viewer || viewer, chat ?? e.LastChat, member ? 0 : e.Missing);
            if (changed) OnJoin(mm);   // scene.Join updates avatar/color of an existing ball
        }
        else { _p[k] = (m, member, viewer, chat ?? DateTime.MinValue, 0); OnJoin(m); }
    }

    /// <summary>A member-list read. complete = the whole (virtualized) list was scrolled through; only then can people leave.
    /// Someone missing from two complete scans in a row leaves (one scan can miss a row that re-rendered mid-scroll).</summary>
    public void Members(IEnumerable<Member> list, bool complete, DateTime now, int? total = null)
    {
        ReportedTotal = total;
        var seen = new HashSet<string>();
        foreach (var m in list) { var k = BallPitScene.Key(m.Name); if (k.Length == 0 || !seen.Add(k)) continue; Upsert(m, true, false, null); }
        if (!complete) return;
        foreach (var k in _p.Keys.ToList())
        {
            var e = _p[k]; if (!e.Member || seen.Contains(k)) continue;
            e.Missing++; _p[k] = e;
            if (e.Missing >= 2) { e.Member = false; e.Missing = 0; _p[k] = e; MaybeLeave(k, now); }
        }
    }
    /// <summary>The LIVE viewers list (a snapshot; not virtualized as far as we know).</summary>
    public void Viewers(IEnumerable<string> names, DateTime now)
    {
        if (!Settings.IncludeViewers) return;
        var seen = new HashSet<string>(names.Select(BallPitScene.Key).Where(k => k.Length > 0));
        foreach (var n in names) Upsert(new Member(n.Trim()), false, true, null);
        foreach (var k in _p.Keys.ToList()) { var e = _p[k]; if (e.Viewer && !seen.Contains(k)) { e.Viewer = false; _p[k] = e; MaybeLeave(k, now); } }
    }
    public void Chatter(string name, DateTime now)
    {
        if (!Settings.IncludeChatters || string.IsNullOrWhiteSpace(name)) return;
        Upsert(new Member(name.Trim()), false, false, now);
    }
    /// <summary>Chat-only people leave after ChatterMinutes of silence.</summary>
    public void Tick(DateTime now) { foreach (var k in _p.Keys.ToList()) MaybeLeave(k, now); }

    void MaybeLeave(string k, DateTime now)
    {
        var e = _p[k];
        if (e.Member || e.Viewer) return;
        if (Settings.IncludeChatters && now - e.LastChat < TimeSpan.FromMinutes(Math.Max(1, Settings.ChatterMinutes))) return;
        _p.Remove(k); OnLeave(e.M.Name);
    }
}
