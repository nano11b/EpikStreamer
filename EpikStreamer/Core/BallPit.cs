using System.IO;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
namespace EpikStreamer.Core;

/// <summary>v2.5 "Ball Pit": every room member becomes a bouncy, avatar-textured ball in a CPU-rendered 3D room.</summary>
public class BallPitSettings
{
    public string Mode { get; set; } = "Off";          // Off | Source (replaces the video) | Overlay (transparent, on top of the playlist)
    public double RenderScale { get; set; } = 0.75;    // render at a fraction of the output size (ffmpeg scales up); 0.5..1 (2.8.3: default 0.75 = Medium)
    /// <summary>v2.6.1: lower the Ball Pit load automatically when the encoder falls behind real time.</summary>
    public bool AutoQuality { get; set; } = true;
    /// <summary>2.8.3: default 60 (was 200), max <see cref="BallPitScene.HardCap"/>. Beyond it the most recent chatters, then viewers,
    /// get a ball; everyone else is counted in "+N more".</summary>
    public int MaxBalls { get; set; } = BallPitScene.DefaultMax;
    /// <summary>2.8.3: new balls per second (joins are queued; a burst of 50 trickles in instead of landing in one frame).</summary>
    public double SpawnPerSecond { get; set; } = 4;
    /// <summary>2.8.3 performance preset: Low | Medium | High (see <see cref="BallPitQuality"/>).</summary>
    public string Quality { get; set; } = "Medium";
    /// <summary>2.8.3 one-time migration marker (MaxBalls 200 → 60 for settings saved with the old default).</summary>
    public int DefaultsVersion { get; set; }
    public bool Migrate()
    {
        if (DefaultsVersion >= 284) return false;
        if (DefaultsVersion < 283)
        {
            if (MaxBalls == 200 || MaxBalls > BallPitScene.HardCap) MaxBalls = BallPitScene.DefaultMax;
            if (string.IsNullOrEmpty(Quality)) Quality = "Medium";
        }
        // 2.8.4: Space is the new default look. Profiles that customised the classic background (colour, image, floor or
        // wall colour) keep Classic; untouched defaults get Space.
        if (Background is not ("Classic" or "Space")) Background = "Space";
        if (!string.IsNullOrWhiteSpace(BackgroundImage) || !Same(BackgroundColor, "#0F172A") || !Same(FloorColor, "#1E293B") || !Same(WallColor, "#26344A"))
            Background = "Classic";
        DefaultsVersion = 284; return true;
    }
    static bool Same(string? a, string b) => string.Equals((a ?? "").Trim(), b, StringComparison.OrdinalIgnoreCase);
    /// <summary>2.8.4: Classic (flat colour / image + room) | Space (pre-rendered starfield, galaxies, nebulae). Source mode only:
    /// Overlay mode stays transparent.</summary>
    public string Background { get; set; } = "Space";
    /// <summary>2.8.4: sky layout seed (Randomize picks a new one).</summary>
    public int SkySeed { get; set; } = 2804;
    public bool IsSpace => Background == "Space";
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

/// <summary>v2.6 radio card drawn inside the Ball Pit (station, song, art, progress, up next, listeners).</summary>
public sealed record RadioCardInfo(string Station, string Title, string Artist, string Next, int? Listeners,
    double Elapsed, double Duration, DateTime AtUtc, byte[]? Art, string ArtKey, bool Online = true)
{
    public double PositionAt(DateTime utc) => Duration > 0 ? Math.Clamp(Elapsed + (utc - AtUtc).TotalSeconds, 0, Duration) : 0;
}

/// <summary>2.8.3 performance preset + the live degrade level the feed applies on top of it.</summary>
public sealed class BallPitQuality
{
    public string Name = "Medium"; public double Scale = 0.75; public bool HalfRate, Shadows = true, Specular = true, Avatars = true;
    public int LabelLimit = 40;        // labels only while at most this many balls are on screen (0 = never)
    public int MaxParticles = 300; public double CapScale = 1; public int Level;
    /// <summary>2.8.4 Space background animation: 0 = static, 1 = slow drift, 2 = drift + twinkle.</summary>
    public int SkyAnim = 1;
    public static BallPitQuality Preset(string? name) => (name ?? "").ToLowerInvariant() switch
    {
        "low" => new() { Name = "Low", SkyAnim = 0, Scale = 0.5, HalfRate = true, Shadows = false, Specular = false, Avatars = false, LabelLimit = 0, MaxParticles = 60 },
        "high" => new() { Name = "High", SkyAnim = 2, Scale = 1.0, LabelLimit = 80, MaxParticles = 400 },
        _ => new() { Name = "Medium" },
    };
    public const int MaxLevel = 4;
    public static readonly string[] LevelNames = { "full", "no shadows/highlights/sky animation", "+ no labels, fewer particles", "+ every 2nd frame drawn", "+ ball count halved" };
    /// <summary>The preset degraded <paramref name="level"/> steps (0 = as set).</summary>
    public static BallPitQuality For(string? preset, int level)
    {
        var q = Preset(preset); q.Level = Math.Clamp(level, 0, MaxLevel);
        if (q.Level >= 1) { q.Shadows = false; q.Specular = false; q.SkyAnim = 0; }
        if (q.Level >= 2) { q.LabelLimit = 0; q.MaxParticles = Math.Min(q.MaxParticles, 60); }
        if (q.Level >= 3) q.HalfRate = true;
        if (q.Level >= 4) q.CapScale = 0.5;
        return q;
    }
}

public sealed class Ball
{
    public string Name = "", Key = "";
    public Vector3 Pos, Vel;
    public Vector3 AX = Vector3.UnitX, AY = Vector3.UnitY, AZ = Vector3.UnitZ;   // orientation (object axes in world space)
    public float BaseRadius = 0.6f, SizeMul = 1f; public double BigUntil;
    public float Radius => BaseRadius * SizeMul;
    public uint[]? Texture;            // TexSize² BGRA (straight alpha): the avatar once loaded, else the initials disc
    public uint[]? InitialsTex;        // always the initials disc (used when avatars are off)
    public bool HasAvatar; public string? AvatarUrl;
    public uint Tint;                  // 0 = none, else 0xRRGGBB (from !color)
    public uint LabelColor = 0xFFFFFF; public bool CustomLabelColor;
    public double JoinedAt, LeaveAt = -1;  // LeaveAt >= 0: popping out
    public string? Bubble; public double BubbleUntil;
    public string? PopText; public double PopUntil;
    public bool OnFloor; public int Sources;
    public bool Viewer; public double LastChat = double.NegativeInfinity;
    public bool Leaving => LeaveAt >= 0;
}

/// <summary>Physics + people. Thread-safe: every public member locks <see cref="Sync"/>.
/// <para>2.8.3: joins are queued and spawned at <see cref="BallPitSettings.SpawnPerSecond"/>; at most <see cref="Cap"/> balls (default 60,
/// max <see cref="HardCap"/>), priority recent chatters → viewers → members, the rest is <see cref="Overflow"/> ("+N more").
/// Avatars that finish loading are handed over lock-free (<see cref="AvatarReady"/> never waits for the lock: in 2.8.2 ~30
/// thread-pool threads parked there while the renderer held the lock and waited for the pool → 13–21 s frame stalls).
/// Collisions use a uniform grid (no per-frame allocations).</para></summary>
public sealed class BallPitScene
{
    public const float RoomX = 8f, RoomZ = 5f, RoomH = 9f, Ceiling = 14f;
    public const int TexSize = 64, HardCap = 200, DefaultMax = 60;
    public readonly object Sync = new();
    public BallPitSettings Settings { get; set; }
    public readonly List<Ball> Balls = new();
    sealed class Pending { public string Name = "", Key = ""; public string? Avatar, Color; public bool Viewer; public double LastChat = double.NegativeInfinity; public long Order; public bool Announce; }
    readonly List<Pending> _pending = new(); long _order;
    readonly System.Collections.Concurrent.ConcurrentQueue<(string Url, uint[] Tex)> _texReady = new();
    public readonly List<(Vector3 P, Vector3 V, uint C, double Until)> Particles = new();
    public double Time { get; private set; }
    public double ShakeUntil { get; private set; }
    double _lastShake = double.MinValue, _tokens = 2;
    readonly Random _rnd;
    public Func<string, uint[]?>? TextureFor;     // host: cached avatar texture by URL (null = not ready; the host loads it and calls AvatarReady)
    /// <summary>Set by the feed (preset + degrade level). Read by the renderer and Step.</summary>
    public BallPitQuality Quality { get; set; } = BallPitQuality.Preset("Medium");
    public long Spawned, Evicted, Coalesced;

    public BallPitScene(BallPitSettings s, int seed = 0) { Settings = s; _rnd = seed == 0 ? new Random() : new Random(seed); }

    int Alive() { int n = 0; foreach (var b in Balls) if (!b.Leaving) n++; return n; }
    public int Count { get { lock (Sync) return Alive(); } }
    /// <summary>People known but without a ball (queued to spawn, or over the cap).</summary>
    public int Waiting { get { lock (Sync) return _pending.Count; } }
    public int Overflow => Waiting;
    public IReadOnlyList<string> Names() { lock (Sync) return Balls.Where(b => !b.Leaving).Select(b => b.Name).Concat(_pending.Select(w => w.Name)).ToList(); }
    public bool Has(string name) { lock (Sync) return Find(name) != null || PendingIndex(Key(name)) >= 0; }

    public static string Key(string name) => (name ?? "").Trim().ToLowerInvariant();
    Ball? Find(string name) { var k = Key(name); foreach (var b in Balls) if (b.Key == k && !b.Leaving) return b; return null; }
    int PendingIndex(string k) { for (int i = 0; i < _pending.Count; i++) if (_pending[i].Key == k) return i; return -1; }
    public int Cap => Math.Max(1, (int)(Math.Clamp(Settings.MaxBalls, 1, HardCap) * Math.Clamp(Quality.CapScale, 0.1, 1)));

    /// <summary>Radius after crowd scaling: many balls → smaller, so the floor isn't a solid wall.</summary>
    public float EffectiveRadius(int n) => (float)(Math.Clamp(Settings.BallSize, 0.15, 2.0) * Math.Clamp(Math.Sqrt(70.0 / Math.Max(1, n)), 0.5, 1.0));

    float Rand(float a, float b) => a + (float)_rnd.NextDouble() * (b - a);

    /// <summary>Adds a person. 2.8.3: queued; <see cref="Step"/> spawns them at the spawn rate while there is room. False if already known
    /// (then the avatar/colour/viewer flag is updated).</summary>
    public bool Join(string name, string? avatarUrl = null, string? color = null, bool announce = true, bool viewer = false)
    {
        name = (name ?? "").Trim(); if (name.Length == 0) return false;
        var k = Key(name);
        lock (Sync)
        {
            var b0 = Find(name);
            if (b0 != null) { Update(b0, avatarUrl, color); b0.Viewer |= viewer; return false; }
            foreach (var lb in Balls) if (lb.Key == k && lb.Leaving) { lb.LeaveAt = -1; Update(lb, avatarUrl, color); lb.Viewer |= viewer; Coalesced++; return true; }   // left and came back: keep the ball
            int pi = PendingIndex(k);
            if (pi >= 0) { var p = _pending[pi]; if (!string.IsNullOrEmpty(avatarUrl)) p.Avatar = avatarUrl; if (color != null) p.Color = color; p.Viewer |= viewer; return false; }
            _pending.Add(new Pending { Name = name, Key = k, Avatar = avatarUrl, Color = color, Viewer = viewer, Order = _order++, Announce = announce });
            return true;
        }
    }
    /// <summary>Spawns everyone queued up to the cap at once (tests, offline preview tools).</summary>
    public void SpawnAllNow() { lock (Sync) while (_pending.Count > 0 && Alive() < Cap) { int i = Best(); var p = _pending[i]; _pending.RemoveAt(i); Spawn(p, false); } }

    /// <summary>3 = chatted within ChatterMinutes, 2 = LIVE viewer, 1 = room member.</summary>
    int Class(bool viewer, double lastChat) => lastChat > Time - Math.Max(1, Settings.ChatterMinutes) * 60 ? 3 : viewer ? 2 : 1;
    double Priority(bool viewer, double lastChat, long order)
    {
        int c = Class(viewer, lastChat);
        return c * 1e6 + (c == 3 ? Math.Min(lastChat, 9e5) : 0) - order * 1e-9;
    }
    int Best() { int bi = -1; double bp = double.MinValue; for (int i = 0; i < _pending.Count; i++) { var w = _pending[i]; var pr = Priority(w.Viewer, w.LastChat, w.Order); if (pr > bp) { bp = pr; bi = i; } } return bi; }
    int Lowest(out double lp)
    {
        int li = -1; lp = double.MaxValue;
        for (int i = 0; i < Balls.Count; i++) { var b = Balls[i]; if (b.Leaving || (Race?.Racers.Contains(b.Name) ?? false)) continue; var pr = Priority(b.Viewer, b.LastChat, (long)(b.JoinedAt * 1000)); if (pr < lp) { lp = pr; li = i; } }
        return li;
    }

    void Spawn(Pending w, bool announce)
    {
        var r = EffectiveRadius(Alive() + 1);
        var b = new Ball
        {
            Name = w.Name, Key = w.Key, BaseRadius = r, JoinedAt = Time, Viewer = w.Viewer, LastChat = w.LastChat,
            Pos = new(Rand(-RoomX + r, RoomX - r), RoomH + Rand(0, 2.5f), Rand(-RoomZ + r, RoomZ - r)),
            Vel = new(Rand(-1.5f, 1.5f), Rand(-2, 0), Rand(-1.5f, 1.5f)),
        };
        var ax = Vector3.Normalize(new Vector3(Rand(-1, 1), Rand(-1, 1), Rand(-1, 1)) + new Vector3(0.01f, 0, 0));
        Rotate(b, ax, Rand(0, 6.28f));
        Update(b, w.Avatar, w.Color);
        b.InitialsTex = BallPitTextures.Initials(b.Name, b.Tint);
        if (b.Texture == null) b.Texture = b.InitialsTex;
        if (announce && Settings.JoinEvents) { b.PopText = w.Name + " joined"; b.PopUntil = Time + 2.8; }
        Balls.Add(b); Spawned++;
        foreach (var o in Balls) if (!o.Leaving) o.BaseRadius = r;   // keep everyone the same (crowd-scaled) size
    }
    void Update(Ball b, string? avatarUrl, string? color)
    {
        if (!string.IsNullOrEmpty(avatarUrl) && avatarUrl != b.AvatarUrl)
        {
            b.AvatarUrl = avatarUrl;
            if (TextureFor?.Invoke(avatarUrl) is { } t) { b.Texture = t; b.HasAvatar = true; }   // cache hit; a miss starts a background load
        }
        if (ParseColor(color) is uint c && !b.CustomLabelColor) b.LabelColor = c;
    }
    /// <summary>Back to the queue (over the cap / lower priority): no pop, no particles; the person keeps their place in "+N more".</summary>
    void Evict(int i)
    {
        var b = Balls[i]; Balls.RemoveAt(i); Evicted++;
        _pending.Add(new Pending { Name = b.Name, Key = b.Key, Avatar = b.AvatarUrl, Color = b.CustomLabelColor ? null : $"#{b.LabelColor:X6}", Viewer = b.Viewer, LastChat = b.LastChat, Order = _order++ });
    }

    /// <summary>Host: an avatar finished loading. Never blocks (called on the loader's thread): applied by the next <see cref="Step"/>.</summary>
    public void AvatarReady(string url, uint[] tex) => _texReady.Enqueue((url, tex));

    /// <summary>Removes a person (pop + particles, or immediately when leave events are off). Queued people just leave the queue.</summary>
    public bool Leave(string name)
    {
        lock (Sync)
        {
            int wi = PendingIndex(Key(name));
            if (wi >= 0) { _pending.RemoveAt(wi); Coalesced++; return true; }
            var b = Find(name); if (b == null) return false;
            if (Settings.LeaveEvents)
            {
                b.LeaveAt = Time;
                int room = Math.Max(0, Quality.MaxParticles - Particles.Count), np = Math.Min(14, room);
                for (int i = 0; i < np; i++)
                {
                    var d = Vector3.Normalize(new Vector3(Rand(-1, 1), Rand(-0.2f, 1), Rand(-1, 1)) + new Vector3(0, 0.3f, 0));
                    Particles.Add((b.Pos, d * Rand(3, 6), b.Tint != 0 ? b.Tint : b.LabelColor, Time + Rand(0.5f, 0.9f)));
                }
            }
            else Balls.Remove(b);
            return true;
        }
    }

    public void Jump(Ball b, float strength = 1f)
    {
        b.Vel += new Vector3(Rand(-1.5f, 1.5f), (8.5f + Rand(0, 2.5f)) * strength, Rand(-1.5f, 1.5f));
    }

    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    /// <summary>A chat line: that person's ball jumps and shows a speech bubble (they're added first if chatters are included).
    /// 2.8.3: a chatter without a ball moves to the front of the queue (and may replace the least active ball when the pit is full).</summary>
    public void Chat(string user, string text)
    {
        lock (Sync)
        {
            var b = Find(user);
            if (b == null)
            {
                if (Settings.IncludeChatters) Join(user);
                int pi = PendingIndex(Key(user)); if (pi >= 0) _pending[pi].LastChat = Time;
                return;
            }
            b.LastChat = Time;
            Jump(b, 0.8f);
            if (Settings.ChatBubbles && !string.IsNullOrWhiteSpace(text))
            {
                var t = Spaces.Replace(text.Trim(), " ");
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
            if (b == null && cmd != "!shake" && Settings.IncludeChatters) { Chat(user, ""); return null; }   // queued with chatter priority; not in the pit yet
            switch (cmd)
            {
                case "!bounce": if (b == null) return null; b.LastChat = Time; Jump(b, 1.3f); return user + " bounced";
                case "!bigball": if (b == null) return null; b.LastChat = Time; b.SizeMul = 1.8f; b.BigUntil = Time + 60; Jump(b, 0.6f); return user + " is a big ball for 60 s";
                case "!color":
                    if (b == null || ParseColor(arg) is not uint c) return null;
                    b.Tint = c; b.LabelColor = c; b.CustomLabelColor = true;
                    b.InitialsTex = BallPitTextures.Initials(b.Name, c);
                    if (!b.HasAvatar) b.Texture = b.InitialsTex;
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
    public void Clear() { lock (Sync) { Balls.Clear(); _pending.Clear(); Particles.Clear(); } }

    /// <summary>2.8.3: after a render error: everyone goes back to the queue (they drop in again at the spawn rate), effects are cleared.</summary>
    public void ResetAfterError()
    {
        lock (Sync)
        {
            for (int i = Balls.Count - 1; i >= 0; i--) { if (Balls[i].Leaving) Balls.RemoveAt(i); else Evict(i); }
            Particles.Clear(); Pulse = 0; _beatPending = 0; Race = null;
        }
    }

    static void Rotate(Ball b, Vector3 axis, float ang)
    {
        var q = Quaternion.CreateFromAxisAngle(axis, ang);
        b.AX = Vector3.Transform(b.AX, q); b.AY = Vector3.Transform(b.AY, q); b.AZ = Vector3.Transform(b.AZ, q);
    }

    // ---- v2.6 radio: beat pulse + in-scene radio card ----
    /// <summary>0 = off; 0..1 = how hard the balls bounce/pulse on beats of the radio.</summary>
    public float BeatStrength { get; set; }
    /// <summary>1 right after a detected beat, decays to 0 (renderer scales the balls slightly).</summary>
    public float Pulse { get; private set; }
    public int Beats { get; private set; }
    public RadioCardInfo? RadioCard { get; set; }
    /// <summary>v2.8: extra on-stream cards drawn on top of each frame (Engage overlay). Called by the renderer WITHOUT Sync held.</summary>
    public Action<SkiaSharp.SKCanvas, int, int>? Overlay { get; set; }
    float _fast, _slow, _beatPending; double _lastBeat = -10;
    /// <summary>Feed the radio's loudness (one RMS value per ~20 ms chunk, 0..1). Cheap; called from the radio feed thread.</summary>
    public void AudioLevel(float rms)
    {
        lock (Sync)
        {
            _fast += (rms - _fast) * 0.35f; _slow += (rms - _slow) * 0.02f;
            if (BeatStrength > 0 && _fast > _slow * 1.3f + 0.015f && Time - _lastBeat > 0.28) { _lastBeat = Time; _beatPending = Math.Clamp((_fast - _slow) * 4, 0.35f, 1f); }
        }
    }

    /// <summary>Advances the simulation by <paramref name="dt"/> seconds (fixed sub-steps ≤ 1/120 s), applies loaded avatars and
    /// spawns / swaps queued people at the spawn rate.</summary>
    public void Step(double dt)
    {
        dt = Math.Clamp(dt, 0, 0.25);
        lock (Sync)
        {
            while (_texReady.TryDequeue(out var tr)) foreach (var b in Balls) if (b.AvatarUrl == tr.Url) { b.Texture = tr.Tex; b.HasAvatar = true; }
            if (_beatPending > 0 && BeatStrength > 0)
            {
                float k = _beatPending * Math.Clamp(BeatStrength, 0, 1); Pulse = Math.Max(Pulse, k); Beats++;
                foreach (var b in Balls) if (!b.Leaving && b.Pos.Y - b.Radius < 0.35f) b.Vel += new Vector3(Rand(-0.4f, 0.4f), (2.2f + Rand(0, 1.8f)) * k * 1.6f, Rand(-0.4f, 0.4f));
            }
            _beatPending = 0; Pulse = Math.Max(0, Pulse - (float)dt * 4);
            int n = Math.Max(1, (int)Math.Ceiling(dt * 120)); float h = (float)(dt / n);
            for (int i = 0; i < n; i++) SubStep(h);
            Time += dt;
            if (Race != null) PlaceRacers(dt);
            Balls.RemoveAll(b => b.Leaving && Time - b.LeaveAt > 0.45);
            Particles.RemoveAll(p => p.Until < Time);
            for (int i = 0; i < Particles.Count; i++) { var p = Particles[i]; p.V.Y -= (float)(Settings.Gravity * 0.6 * dt); p.P += p.V * (float)dt; Particles[i] = p; }
            foreach (var b in Balls) if (b.SizeMul != 1 && Time > b.BigUntil) b.SizeMul = Math.Max(1, b.SizeMul - (float)dt * 2);
            Admit(dt);
            if (_orthoCount++ % 60 == 0) foreach (var b in Balls) Orthonormalize(b);
        }
    }
    int _orthoCount;
    /// <summary>Spawn queue: a token bucket (SpawnPerSecond, burst 2); over the cap a queued person with a higher priority (recent
    /// chatter, viewer) swaps with the least active ball; when the cap shrinks (degrade) extra balls go back to the queue.</summary>
    void Admit(double dt)
    {
        double rate = Math.Clamp(Settings.SpawnPerSecond, 0.5, 30);
        _tokens = Math.Min(2, _tokens + dt * rate);
        int alive = Alive(), cap = Cap;
        for (int k = 0; alive > cap && k < 4; k++) { int li = Lowest(out _); if (li < 0) break; Evict(li); alive--; }
        while (_tokens >= 1 && _pending.Count > 0)
        {
            int bi = Best(); var w = _pending[bi];
            if (alive >= cap)
            {
                int li = Lowest(out _);
                if (li < 0 || Class(w.Viewer, w.LastChat) <= Class(Balls[li].Viewer, Balls[li].LastChat)) break;   // only a higher class (chatter > viewer > member) swaps in
                Evict(li); alive--;
                bi = _pending.IndexOf(w);
            }
            _pending.RemoveAt(bi); Spawn(w, w.Announce && _pending.Count <= 3); alive++; _tokens -= 1;
        }
    }

    // ---- v2.8 Ball Pit race: racers roll along lanes from the left wall to the finish line (positions from the planned race) ----
    public RaceGame? Race { get; set; }
    public Func<DateTime> RaceClock { get; set; } = () => DateTime.UtcNow;
    public const float RaceStartX = -RoomX + 0.9f, RaceFinishX = RoomX - 1.3f;
    public static float LaneZ(int lane, int lanes) => lanes <= 1 ? 0 : -RoomZ + 0.9f + lane * (2 * RoomZ - 1.8f) / (lanes - 1);
    /// <summary>Called with Sync held, after physics: racers are placed on their lane, rolling; everyone else just gets nudged by them.</summary>
    void PlaceRacers(double dt)
    {
        var race = Race!; var t = race.Elapsed(RaceClock());
        for (int i = 0; i < race.Racers.Count; i++)
        {
            var b = Find(race.Racers[i]); if (b == null) continue;
            var p = (float)race.Progress(i, t);
            var x = RaceStartX + p * (RaceFinishX - RaceStartX);
            var dx = x - b.Pos.X;
            var target = new Vector3(x, b.Radius, LaneZ(i, race.Racers.Count));
            b.Pos = Vector3.Lerp(b.Pos, target, Math.Clamp((float)dt * 12f, 0.15f, 1f)); b.Vel = Vector3.Zero; b.OnFloor = true;
            if (Math.Abs(dx) > 1e-4f) Rotate(b, -Vector3.UnitZ, dx / Math.Max(0.1f, b.Radius));
        }
    }
    /// <summary>Puts the racers in the pit (they join if needed, ahead of the queue and the cap) and lines them up.</summary>
    public void StartRace(RaceGame? race)
    {
        lock (Sync)
        {
            Race = race; if (race == null) return;
            foreach (var n in race.Racers)
                if (Find(n) == null)
                {
                    int pi = PendingIndex(Key(n)); var w = pi >= 0 ? _pending[pi] : new Pending { Name = n.Trim(), Key = Key(n) };
                    if (pi >= 0) _pending.RemoveAt(pi);
                    Spawn(w, false);
                }
            for (int i = 0; i < race.Racers.Count; i++) if (Find(race.Racers[i]) is { } b) { b.Pos = new Vector3(RaceStartX, b.Radius + 2, LaneZ(i, race.Racers.Count)); b.PopText = $"#{i + 1}"; b.PopUntil = Time + 3; }
        }
    }
    static void Orthonormalize(Ball b)
    {
        b.AX = Vector3.Normalize(b.AX); b.AY = Vector3.Normalize(b.AY - Vector3.Dot(b.AY, b.AX) * b.AX); b.AZ = Vector3.Cross(b.AX, b.AY);
    }

    // uniform-grid broadphase (2.8.3; was all pairs). Reused arrays: no allocations per step once sized.
    int[] _cellHead = Array.Empty<int>(), _next = Array.Empty<int>(), _cellOf = Array.Empty<int>();
    int _gx, _gy, _gz; float _cell;
    void BuildGrid(List<Ball> bs)
    {
        float maxR = 0.1f; foreach (var b in bs) if (b.Radius > maxR) maxR = b.Radius;
        _cell = 2 * maxR;
        _gx = Math.Max(1, (int)MathF.Ceiling(2 * RoomX / _cell)); _gz = Math.Max(1, (int)MathF.Ceiling(2 * RoomZ / _cell)); _gy = Math.Max(1, (int)MathF.Ceiling((Ceiling + 4) / _cell));
        int cells = _gx * _gy * _gz;
        if (_cellHead.Length < cells) _cellHead = new int[cells];
        if (_next.Length < bs.Count) { _next = new int[bs.Count * 2]; _cellOf = new int[bs.Count * 2]; }
        Array.Fill(_cellHead, -1, 0, cells);
        for (int i = 0; i < bs.Count; i++)
        {
            var p = bs[i].Pos;
            int cx = Math.Clamp((int)((p.X + RoomX) / _cell), 0, _gx - 1), cy = Math.Clamp((int)((p.Y + 2) / _cell), 0, _gy - 1), cz = Math.Clamp((int)((p.Z + RoomZ) / _cell), 0, _gz - 1);
            int c = (cy * _gz + cz) * _gx + cx; _cellOf[i] = c; _next[i] = _cellHead[c]; _cellHead[c] = i;
        }
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
            BuildGrid(bs);
            for (int i = 0; i < bs.Count; i++)
            {
                var a = bs[i]; if (a.Leaving) continue; float ra = a.Radius;
                int c = _cellOf[i], cx = c % _gx, cz = c / _gx % _gz, cy = c / (_gx * _gz);
                for (int dy = -1; dy <= 1; dy++)
                {
                    int y = cy + dy; if (y < 0 || y >= _gy) continue;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int z = cz + dz; if (z < 0 || z >= _gz) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = cx + dx; if (x < 0 || x >= _gx) continue;
                            for (int j = _cellHead[(y * _gz + z) * _gx + x]; j >= 0; j = _next[j])
                            {
                                if (j <= i) continue;
                                var b = bs[j]; if (b.Leaving) continue; Collide(a, b, ra, e);
                            }
                        }
                    }
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
    static void Collide(Ball a, Ball b, float ra, float e)
    {
        float rb = b.Radius, rs = ra + rb;
        var d = b.Pos - a.Pos;
        if (Math.Abs(d.X) >= rs || Math.Abs(d.Y) >= rs || Math.Abs(d.Z) >= rs) return;
        float d2 = d.LengthSquared(); if (d2 >= rs * rs) return;
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
    public bool IsViewer(string name) => _p.TryGetValue(BallPitScene.Key(name), out var e) && e.Viewer;
    public IEnumerable<Member> People => _p.Values.Select(v => v.M);

    void Upsert(Member m, bool member, bool viewer, DateTime? chat)
    {
        var k = BallPitScene.Key(m.Name); if (k.Length == 0) return;
        if (_p.TryGetValue(k, out var e))
        {
            var mm = new Member(e.M.Name, m.Avatar ?? e.M.Avatar, m.Color ?? e.M.Color);
            bool changed = mm != e.M || (viewer && !e.Viewer);
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
