using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace EpikStreamer.Core;

public enum Corner { TopLeft, TopCenter, TopRight, Center, BottomLeft, BottomCenter, BottomRight }

public class OverlaySettings
{
    public bool ImageEnabled { get; set; }
    public string ImagePath { get; set; } = "";
    public Corner ImageCorner { get; set; } = Corner.TopRight;
    public double ImageScale { get; set; } = 0.15;   // logo width as fraction of output width
    public double ImageOpacity { get; set; } = 1.0;  // 0..1

    public bool TextEnabled { get; set; }
    public string Text { get; set; } = "Live on EpikChat";
    public int FontSize { get; set; } = 36;
    public string FontColor { get; set; } = "#FFFFFF";
    public Corner TextCorner { get; set; } = Corner.BottomLeft;
    public bool TextBackground { get; set; } = true;
    public bool Ticker { get; set; }
    public int TickerSpeed { get; set; } = 120;       // pixels per second
    public string FontFile { get; set; } = "";        // empty = Arial (Windows) / DejaVu (Linux)

    public bool ClockEnabled { get; set; }
    public string ClockFormat { get; set; } = "%Y-%m-%d %X";
    public int ClockFontSize { get; set; } = 28;
    public Corner ClockCorner { get; set; } = Corner.TopLeft;

    public OverlaySettings Clone() => (OverlaySettings)MemberwiseClone();
}

public class OutputTarget
{
    public string Name { get; set; } = "EpikChat";
    public string Preset { get; set; } = "EpikChat";
    public string ServerUrl { get; set; } = "rtmp://media.epikchat.com:1935/x";
    public string? ProtectedKey { get; set; }   // DPAPI, only when the profile's RememberKey is on
    public bool Enabled { get; set; } = true;
    public override string ToString() => (Enabled ? "● " : "○ ") + Name;

    public static readonly (string Name, string Url, string Hint)[] Presets =
    {
        ("EpikChat", "rtmp://media.epikchat.com:1935/x", "Go live → RTMP encoder"),
        ("Twitch", "rtmp://live.twitch.tv/app", "Creator Dashboard → Settings → Stream"),
        ("YouTube", "rtmp://a.rtmp.youtube.com/live2", "YouTube Studio → Go live → Stream"),
        ("Kick", "rtmps://fa723fc1b171.global-contribute.live-video.net/app", "Kick dashboard → Settings → Stream URL & Key (use the URL shown there)"),
        ("Custom", "", "Any RTMP/RTMPS ingest"),
    };
}

public class Profile
{
    public string Name { get; set; } = "Default";
    public string ServerUrl { get; set; } = "rtmp://media.epikchat.com:1935/x";
    public bool RememberKey { get; set; }
    public string? ProtectedKey { get; set; }
    public string Encoder { get; set; } = "libx264";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public int VideoKbps { get; set; } = 2500;
    public int AudioKbps { get; set; } = 128;
    public int KeyframeSeconds { get; set; } = 2;
    public int MaxRetries { get; set; } = 10;
    public string Audience { get; set; } = "Public"; // used by "Sign in to EpikChat & get stream key"
    public bool LoopPlaylist { get; set; }
    public bool Shuffle { get; set; }
    public string FfmpegPath { get; set; } = "";
    public List<string> Playlist { get; set; } = new();
    public List<OutputTarget> Outputs { get; set; } = new();

    // v2.1: transitions
    public double CrossfadeSeconds { get; set; } = 1.0;   // 0 = hard cut (still gapless)
    public string Transition { get; set; } = "fade";       // ffmpeg xfade transition name

    // v2.1: local recording (tee output)
    public bool RecordEnabled { get; set; }
    public string RecordFolder { get; set; } = "";         // blank = My Videos\EpikStreamer
    public string RecordPattern { get; set; } = "{profile}_{date}_{time}";
    public bool RemuxToMp4 { get; set; } = true;

    /// <summary>Migrates the v2.0 single ServerUrl/key into Outputs[0].</summary>
    public void EnsureOutputs()
    {
        if (Outputs.Count > 0) return;
        var preset = ServerUrl.Contains("epikchat", StringComparison.OrdinalIgnoreCase) ? "EpikChat" : "Custom";
        Outputs.Add(new OutputTarget { Name = preset, Preset = preset, ServerUrl = ServerUrl, ProtectedKey = ProtectedKey, Enabled = true });
    }
    public OverlaySettings Overlays { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();   // v2.2
    public NowPlayingSettings NowPlaying { get; set; } = new();   // v2.3
    public BallPitSettings BallPit { get; set; } = new();         // v2.5

    public Profile Clone(string newName)
    {
        var json = JsonSerializer.Serialize(this, ProfileStore.Json);
        var p = JsonSerializer.Deserialize<Profile>(json, ProfileStore.Json)!;
        p.Name = newName;
        return p;
    }
}

public static class Secret
{
    /// <summary>Non-Windows secret store (Linux: libsecret / AES file). Windows always uses DPAPI and ignores these.</summary>
    public static Func<string, string?>? PlatformProtect { get; set; }
    public static Func<string, string>? PlatformUnprotect { get; set; }

    public static string? Protect(string s)
    {
        if (!string.IsNullOrEmpty(s) && !OperatingSystem.IsWindows() && PlatformProtect != null) return PlatformProtect(s);
        if (string.IsNullOrEmpty(s) || !OperatingSystem.IsWindows()) return null;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser));
    }
    public static string Unprotect(string? b64)
    {
        if (!string.IsNullOrEmpty(b64) && !OperatingSystem.IsWindows() && PlatformUnprotect != null) return PlatformUnprotect(b64);
        if (string.IsNullOrEmpty(b64) || !OperatingSystem.IsWindows()) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }
}

/// <summary>v1 settings.json shape, used only for migration.</summary>
public class LegacySettings
{
    public string FilePath { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public int VideoKbps { get; set; } = 2500;
    public int AudioKbps { get; set; } = 128;
    public int KeyframeSeconds { get; set; } = 2;
    public bool Loop { get; set; }
    public bool RememberKey { get; set; }
    public string? ProtectedKey { get; set; }
}

public class ProfileStore
{
    public string Active { get; set; } = "Default";
    // app-wide settings (v2.1)
    public string UpdateManifestUrl { get; set; } = Updater.DefaultManifestUrl;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public string? ProtectedGitHubToken { get; set; }   // optional, DPAPI; only for GitHub API rate limits
    public bool ChatPanelVisible { get; set; }
    public bool ChatCompact { get; set; } = true;
    // v2.2
    public string Theme { get; set; } = "Dark";          // Dark | Light | Midnight Blue | Epik Purple
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool Notifications { get; set; } = true;
    public bool LaunchAtStartup { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool SchedulerEnabled { get; set; } = true;
    public List<ScheduleEntry> Schedule { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public ChatSettings Chat { get; set; } = new();
    // v2.3
    public RequestSettings Requests { get; set; } = new();
    public ViewerSettings Viewers { get; set; } = new();
    public int ControlPort { get; set; } = 0;          // 0 = pick a free port per session
    public List<Profile> Profiles { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EpikStreamer");
    public static string FilePath => Path.Combine(Dir, "profiles.json");
    public static string LegacyPath => Path.Combine(Dir, "settings.json");

    public Profile Current => Profiles.FirstOrDefault(p => p.Name == Active) ?? Profiles[0];

    public static ProfileStore Load()
    {
        ProfileStore? s = null;
        try { if (File.Exists(FilePath)) s = JsonSerializer.Deserialize<ProfileStore>(File.ReadAllText(FilePath), Json); } catch { }
        if (s == null || s.Profiles.Count == 0)
        {
            s = new ProfileStore();
            var p = new Profile { Name = "Default" };
            try
            {
                if (File.Exists(LegacyPath))
                {
                    var l = JsonSerializer.Deserialize<LegacySettings>(File.ReadAllText(LegacyPath));
                    if (l != null)
                    {
                        if (!string.IsNullOrWhiteSpace(l.ServerUrl)) p.ServerUrl = l.ServerUrl;
                        p.FfmpegPath = l.FfmpegPath; p.Width = l.Width; p.Height = l.Height; p.Fps = l.Fps;
                        p.VideoKbps = l.VideoKbps; p.AudioKbps = l.AudioKbps; p.KeyframeSeconds = l.KeyframeSeconds;
                        p.LoopPlaylist = l.Loop; p.RememberKey = l.RememberKey; p.ProtectedKey = l.ProtectedKey;
                        if (!string.IsNullOrWhiteSpace(l.FilePath)) p.Playlist.Add(l.FilePath);
                    }
                }
            }
            catch { }
            s.Profiles.Add(p);
            s.Active = p.Name;
        }
        foreach (var p in s.Profiles) p.EnsureOutputs();
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, true);
    }
}

/// <summary>Which platform's ffmpeg devices / encoders / update packages to target. Defaults to the running OS; tests can flip it.</summary>
public static class Os
{
    public static bool Windows { get; set; } = OperatingSystem.IsWindows();
}
