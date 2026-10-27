using System.IO;
namespace EpikStreamer.Core;
/// <summary>2.8.2: what the stream's picture comes from — the playlist (video files) or the Ball Pit scene. This is the same
/// setting as the Ball Pit tab's mode ("Source" = Ball Pit scene); the two controls are kept in sync through it.</summary>
public static class SourceMode
{
    public const string Playlist = "Playlist", BallPit = "BallPit";
    public static string Of(BallPitSettings b) => b.Mode == "Source" ? BallPit : Playlist;
    /// <summary>Sets the Ball Pit mode for a source choice. Playlist turns a Source-mode pit Off but keeps Overlay.</summary>
    public static void Set(BallPitSettings b, string source)
    {
        if (source == BallPit) b.Mode = "Source";
        else if (b.Mode == "Source") b.Mode = "Off";
    }
    public static bool PitOnly(Profile p) => p.BallPit.Mode == "Source" && !p.Playlist.Any(File.Exists);
    /// <summary>The hint shown under the source selector.</summary>
    public static string Note(Profile p)
    {
        if (Of(p.BallPit) == Playlist) return "Files play back to back, gaplessly, over one connection. Each is scaled/padded to the output size and frame rate. Drag-and-drop files here too.";
        var audio = p.Radio.Active ? "Audio comes from the Radio tab (" + p.Radio.Url.Trim() + ")." : "Audio: the Radio tab is off, so the stream carries silence (turn the radio on, or add music on the Audio tab).";
        return "The Ball Pit scene is the picture (configure it on the Ball Pit tab). No video files are needed. " + audio
            + (p.Playlist.Count > 0 ? " Playlist files are only used for their audio." : "");
    }
    /// <summary>Start-time validation of the source. Null = OK; otherwise the warning to show.</summary>
    public static string? StartProblem(Profile p)
    {
        if (p.BallPit.Mode == "Source") return null;   // the pit is the picture; an empty playlist gets a silent filler
        if (p.Playlist.Count == 0) return "Add at least one video to the playlist, or choose \"Ball Pit scene\" as the source.";
        if (!p.Playlist.Any(File.Exists)) return "None of the playlist files exist.";
        return null;
    }
    /// <summary>Reply for !request when there's no playlist and no radio request handling.</summary>
    public const string NoPlaylistReply = "Song requests need a playlist — this stream is the Ball Pit scene only (no video files).";
}
