namespace EpikStreamer.Core;

/// <summary>v2.2 per-profile audio processing.</summary>
public class AudioSettings
{
    public string Normalize { get; set; } = "Off";          // Off | Loudnorm | Dynaudnorm
    public double TargetLufs { get; set; } = -16;
    public double MainVolume { get; set; } = 1.0;            // stream audio
    // background music (mixed under the video's own audio)
    public bool MusicEnabled { get; set; }
    public List<string> MusicPlaylist { get; set; } = new();
    public bool MusicShuffle { get; set; } = true;
    public double MusicVolume { get; set; } = 0.25;
    public bool Ducking { get; set; } = true;               // duck music while the stream audio / mic is loud
    public double DuckThreshold { get; set; } = 0.05;       // linear 0..1 (sidechaincompress threshold)
    public double DuckRatio { get; set; } = 6;
    // microphone (Windows dshow device)
    public bool MicEnabled { get; set; }
    public string MicDevice { get; set; } = "";             // dshow friendly name ("lavfi:<graph>" is a test hook)
    public double MicVolume { get; set; } = 1.0;
    public bool MicNoiseGate { get; set; }
    public double GateThreshold { get; set; } = 0.02;
}

public enum Recurrence { Once, Daily, Weekly }

public class ScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "Scheduled stream";
    public Recurrence Repeat { get; set; } = Recurrence.Once;
    public DateTime Date { get; set; } = DateTime.Today;    // for Once (and the first day for Daily/Weekly)
    public List<DayOfWeek> Days { get; set; } = new();       // for Weekly
    public TimeSpan Start { get; set; } = new(20, 0, 0);
    public TimeSpan? Stop { get; set; }                     // null = run until the playlist ends; Stop <= Start => next day
    public string Profile { get; set; } = "";               // blank = active profile
    public string PlaylistFile { get; set; } = "";          // optional .m3u/.txt; blank = profile's playlist
}

public class HotkeySettings
{
    public bool Enabled { get; set; } = true;
    public string StartStop { get; set; } = "Ctrl+Alt+S";
    public string Next { get; set; } = "Ctrl+Alt+N";
    public string MuteMusic { get; set; } = "Ctrl+Alt+M";
    public string MuteMic { get; set; } = "Ctrl+Alt+K";
    public string ToggleOverlays { get; set; } = "Ctrl+Alt+O";
}

public class ChatCommand
{
    public string Trigger { get; set; } = "!cmd";
    public string Response { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public class ChatSettings
{
    public bool CommandsEnabled { get; set; } = true;
    /// <summary>Opt-in: type responses into EpikChat as you. OFF by default; responses otherwise only show on-stream.</summary>
    public bool ReplyInChat { get; set; }
    public int CooldownSeconds { get; set; } = 10;
    public int OverlaySeconds { get; set; } = 8;
    public bool ShowOnStream { get; set; } = true;
    public string SongTemplate { get; set; } = "Now playing: {song}";
    public string UptimeTemplate { get; set; } = "Live for {uptime}";
    public List<ChatCommand> Custom { get; set; } = new() { new() { Trigger = "!discord", Response = "Edit custom commands in Settings → Chat", Enabled = false } };
    // alerts
    public bool AlertsEnabled { get; set; } = true;
    public bool NewViewerAlert { get; set; } = true;
    public string NewViewerText { get; set; } = "Welcome! {viewers} watching";
    public List<int> Milestones { get; set; } = new() { 10, 25, 50, 100, 250, 500 };
    public string MilestoneText { get; set; } = "★ {viewers} viewers! ★";
    public int AlertSeconds { get; set; } = 6;
    public string AlertSound { get; set; } = "";             // .wav, played locally
}
