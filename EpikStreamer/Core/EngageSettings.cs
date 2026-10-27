namespace EpikStreamer.Core;

/// <summary>v2.8 Engage (viewer points, rewards, mini-games, polls/predictions). Per profile. Points have no real value.</summary>
public class EngageSettings
{
    public bool Enabled { get; set; } = true;
    public string CurrencyName { get; set; } = "Epik Points";
    // ---- earning ----
    public int PointsPerMinute { get; set; } = 10;          // per minute watching while live (from the viewers list)
    public int ChatBonus { get; set; } = 5;                 // per chat message…
    public int ChatCooldownSeconds { get; set; } = 30;      // …at most one bonus per user per this many seconds
    public int ChatMaxPerMinute { get; set; } = 10;         // …and at most this many bonus points per user per minute
    public int FirstChatterBonus { get; set; } = 100;
    public bool StreakEnabled { get; set; } = true;
    public int StreakBonus { get; set; } = 25;              // × consecutive stream days (from day 2), once per stream
    public int StreakMaxDays { get; set; } = 7;
    // ---- commands ----
    public bool GiveEnabled { get; set; } = true;
    public int TopCount { get; set; } = 5;
    /// <summary>Names or EpikChat userIds (comma/space separated) treated like the host for host commands.</summary>
    public string Moderators { get; set; } = "";
    /// <summary>Never earn or play (bots, alt accounts). Names or userIds.</summary>
    public string Excluded { get; set; } = "";
    public int UserCooldownSeconds { get; set; } = 3;       // anti-abuse: per user, between Engage commands
    public int GlobalCommandsPerMinute { get; set; } = 40;  // anti-abuse: all users together
    // ---- rewards ----
    public bool RequireApproval { get; set; }               // every redemption waits for the host (per-reward flag too)
    public List<Reward> Rewards { get; set; } = Reward.Defaults();
    public GameSettings Games { get; set; } = new();
    public PollSettings Polls { get; set; } = new();
    public EngageOverlaySettings Overlay { get; set; } = new();
    public static HashSet<string> List(string s) => s.Split(new[] { ',', ' ', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim().TrimStart('@').ToLowerInvariant()).Where(x => x.Length > 0).ToHashSet();
}

public class Reward
{
    public string Name { get; set; } = "";                  // !redeem <name>
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "Alert";             // Song | BallPit | Alert | Sound
    public string Arg { get; set; } = "";                   // BallPit: !bigball/!shake/!bounce; Alert: text ({user} {arg}); Sound: .wav path
    public int Cost { get; set; } = 100;
    public int CooldownSeconds { get; set; } = 30;          // per reward, all users
    public bool RequireApproval { get; set; }
    public bool Enabled { get; set; } = true;
    public static List<Reward> Defaults() => new()
    {
        new() { Name = "song", Title = "Song request", Kind = "Song", Cost = 500, CooldownSeconds = 60 },
        new() { Name = "bigball", Title = "Big ball (Ball Pit)", Kind = "BallPit", Arg = "!bigball", Cost = 100, CooldownSeconds = 20 },
        new() { Name = "shake", Title = "Shake the pit", Kind = "BallPit", Arg = "!shake", Cost = 250, CooldownSeconds = 60 },
        new() { Name = "hype", Title = "Hype alert", Kind = "Alert", Arg = "🔥 {user} says: {arg}", Cost = 300, CooldownSeconds = 60 },
        new() { Name = "airhorn", Title = "Airhorn", Kind = "Sound", Arg = "", Cost = 200, CooldownSeconds = 90, Enabled = false },
    };
}

public class GameSettings
{
    public int GameCooldownSeconds { get; set; } = 20;      // per user per game
    public int MinBet { get; set; } = 10;
    public int MaxBet { get; set; } = 1000;
    public bool GambleEnabled { get; set; } = true;
    public double GambleWinChance { get; set; } = 0.45;
    public double GambleMultiplier { get; set; } = 2.0;     // total returned on a win (stake × this)
    public bool SlotsEnabled { get; set; } = true;
    public int SlotsCost { get; set; } = 20;                // default bet for !slots
    public double SlotsPair { get; set; } = 1.5;
    public double SlotsTriple { get; set; } = 8;
    public double SlotsJackpot { get; set; } = 25;
    public bool DuelEnabled { get; set; } = true;
    public int DuelAcceptSeconds { get; set; } = 60;
    public bool TriviaEnabled { get; set; } = true;
    public int TriviaEveryMinutes { get; set; } = 0;        // 0 = host-triggered only (!trivia)
    public int TriviaSeconds { get; set; } = 30;
    public int TriviaReward { get; set; } = 100;
    public List<string> TriviaPacks { get; set; } = new();  // extra JSON packs; the built-in pack is always available
    public bool TriviaBuiltIn { get; set; } = true;
    public bool HeistEnabled { get; set; } = true;
    public int HeistJoinSeconds { get; set; } = 60;
    public double HeistBaseChance { get; set; } = 0.35;
    public double HeistPerMember { get; set; } = 0.06;
    public double HeistMaxChance { get; set; } = 0.8;
    public double HeistMultiplier { get; set; } = 1.8;
    public bool RaceEnabled { get; set; } = true;
    public int RaceBetSeconds { get; set; } = 45;
    public int RaceMaxRacers { get; set; } = 8;
    public int RaceWinnerBonus { get; set; } = 100;
    public double RaceSeconds { get; set; } = 10;
}

public class PollSettings
{
    public bool Enabled { get; set; } = true;
    public bool ModsCanCreate { get; set; } = true;
    public bool BareVotes { get; set; } = true;             // typing "2" or the option text alone counts as a vote
    public int DefaultSeconds { get; set; } = 90;
    public bool PredictionsEnabled { get; set; } = true;
    public int MaxStake { get; set; } = 5000;
}

public class EngageOverlaySettings
{
    public bool Enabled { get; set; } = true;
    public Corner Corner { get; set; } = Corner.TopRight;
    public double WidthFraction { get; set; } = 0.32;
    public string Background { get; set; } = "#101018";
    public double Opacity { get; set; } = 0.82;
    public string TextColor { get; set; } = "#FFFFFF";
    public string AccentColor { get; set; } = "#A855F7";
    public int LeaderboardSeconds { get; set; } = 12;
    public int ToastSeconds { get; set; } = 7;
    public int ResultSeconds { get; set; } = 12;
}
