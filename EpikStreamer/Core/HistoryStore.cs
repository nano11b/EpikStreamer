using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
namespace EpikStreamer.Core;

public class SessionRecord
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public double DurationSec { get; set; }
    public string Profile { get; set; } = "";
    public List<string> Destinations { get; set; } = new();
    public int PeakViewers { get; set; }
    public double AvgViewers { get; set; }
    public double AvgBitrateKbps { get; set; }
    public long DroppedFrames { get; set; }
    public int Reconnects { get; set; }
    public List<string> FilesPlayed { get; set; } = new();
    public string EndReason { get; set; } = "";
    public int UniqueViewers { get; set; }                       // v2.3 (named viewers seen in the chat lists)
    public List<string> TopViewers { get; set; } = new();        // v2.3 "name (minutes)", longest first, max 20
}

/// <summary>Accumulates one session's stats (thread-safe).</summary>
public sealed class SessionTracker
{
    readonly object _lk = new(); readonly SessionRecord _r;
    double _brSum; long _brN; double _vSum; long _vN;
    public SessionTracker(string profile, IEnumerable<string> destinations, DateTime start)
    { _r = new SessionRecord { Start = start, Profile = profile, Destinations = destinations.ToList() }; }
    public void OnStats(StreamStats s)
    {
        lock (_lk)
        {
            if (s.BitrateKbps > 0) { _brSum += s.BitrateKbps; _brN++; }
            _r.DroppedFrames = Math.Max(_r.DroppedFrames, s.Dropped); _r.Reconnects = Math.Max(_r.Reconnects, s.Reconnects);
            if (!string.IsNullOrEmpty(s.ItemName) && (_r.FilesPlayed.Count == 0 || _r.FilesPlayed[^1] != s.ItemName)) _r.FilesPlayed.Add(s.ItemName);
        }
    }
    public void OnViewers(int v) { lock (_lk) { _vSum += v; _vN++; _r.PeakViewers = Math.Max(_r.PeakViewers, v); } }
    public void SetViewers(IEnumerable<ViewerSession> v, DateTime now)
    {
        var l = v.ToList();
        lock (_lk) { _r.UniqueViewers = l.Count; _r.TopViewers = l.OrderByDescending(x => x.TotalNow(now)).Take(20).Select(x => $"{x.Name} ({x.TotalNow(now).TotalMinutes:0} min)").ToList(); }
    }
    public SessionRecord Finish(DateTime end, string reason)
    {
        lock (_lk)
        {
            _r.End = end; _r.DurationSec = Math.Round((end - _r.Start).TotalSeconds, 1); _r.EndReason = reason;
            _r.AvgBitrateKbps = _brN == 0 ? 0 : Math.Round(_brSum / _brN, 1); _r.AvgViewers = _vN == 0 ? 0 : Math.Round(_vSum / _vN, 2);
            return _r;
        }
    }
}

/// <summary>Append-only JSON-lines store (%APPDATA%\EpikStreamer\history.jsonl) + CSV export.</summary>
public sealed class HistoryStore
{
    public string Path { get; }
    public HistoryStore(string? path = null) => Path = path ?? System.IO.Path.Combine(ProfileStore.Dir, "history.jsonl");
    static readonly JsonSerializerOptions J = new() { WriteIndented = false };

    public void Append(SessionRecord r)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.AppendAllText(Path, JsonSerializer.Serialize(r, J) + "\n", new UTF8Encoding(false));
    }
    public List<SessionRecord> Load()
    {
        var res = new List<SessionRecord>();
        if (!File.Exists(Path)) return res;
        foreach (var l in File.ReadLines(Path))
        { if (string.IsNullOrWhiteSpace(l)) continue; try { if (JsonSerializer.Deserialize<SessionRecord>(l, J) is { } r) res.Add(r); } catch { /* skip a corrupt line */ } }
        return res;
    }
    public void Clear() { try { File.Delete(Path); } catch { } }

    public static string ToCsv(IEnumerable<SessionRecord> rs)
    {
        var sb = new StringBuilder("start,end,duration_s,profile,destinations,peak_viewers,avg_viewers,avg_bitrate_kbps,dropped_frames,reconnects,files_played,end_reason,unique_viewers\r\n");
        var ci = CultureInfo.InvariantCulture;
        foreach (var r in rs)
            sb.Append(string.Join(",", new[]
            {
                r.Start.ToString("yyyy-MM-dd HH:mm:ss", ci), r.End.ToString("yyyy-MM-dd HH:mm:ss", ci), r.DurationSec.ToString(ci), Q(r.Profile),
                Q(string.Join("; ", r.Destinations)), r.PeakViewers.ToString(ci), r.AvgViewers.ToString(ci), r.AvgBitrateKbps.ToString(ci),
                r.DroppedFrames.ToString(ci), r.Reconnects.ToString(ci), Q(string.Join("; ", r.FilesPlayed)), Q(r.EndReason), r.UniqueViewers.ToString(ci),
            })).Append("\r\n");
        return sb.ToString();
    }
    static string Q(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || s.StartsWith('=') || s.StartsWith('+') || s.StartsWith('-') || s.StartsWith('@')
        ? "\"" + (s.Length > 0 && "=+-@".Contains(s[0]) ? "'" : "") + s.Replace("\"", "\"\"") + "\"" : s;
}
