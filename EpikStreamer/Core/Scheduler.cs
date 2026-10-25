using System.IO;
namespace EpikStreamer.Core;

/// <summary>Pure schedule math (unit-tested). The UI polls <see cref="Due"/> every few seconds.</summary>
public static class Scheduler
{
    /// <summary>Start time of the occurrence that contains or follows <paramref name="now"/>, or null if none.</summary>
    public static DateTime? NextStart(ScheduleEntry e, DateTime now)
    {
        if (!e.Enabled) return null;
        // look back one day so an occurrence that's currently running is returned
        for (int d = -1; d <= 8; d++)
        {
            var day = now.Date.AddDays(d);
            if (!Occurs(e, day)) continue;
            var start = day + e.Start;
            if (start >= now || (e.Stop != null && now < End(e, start))) return start;
        }
        return null;
    }

    public static bool Occurs(ScheduleEntry e, DateTime day) => e.Repeat switch
    {
        Recurrence.Once => day.Date == e.Date.Date,
        Recurrence.Daily => day.Date >= e.Date.Date,
        Recurrence.Weekly => day.Date >= e.Date.Date && (e.Days.Count == 0 ? day.DayOfWeek == e.Date.DayOfWeek : e.Days.Contains(day.DayOfWeek)),
        _ => false,
    };

    /// <summary>End of an occurrence; a stop time at or before the start means the next day.</summary>
    public static DateTime End(ScheduleEntry e, DateTime start)
    {
        if (e.Stop is not TimeSpan stop) return DateTime.MaxValue;
        var end = start.Date + stop;
        return end <= start ? end.AddDays(1) : end;
    }

    /// <summary>Occurrence that should be running at <paramref name="now"/> (start ≤ now &lt; end, and started within the grace window).</summary>
    public static DateTime? ActiveOccurrence(ScheduleEntry e, DateTime now, TimeSpan grace)
    {
        if (!e.Enabled) return null;
        for (int d = -1; d <= 0; d++)
        {
            var day = now.Date.AddDays(d);
            if (!Occurs(e, day)) continue;
            var start = day + e.Start;
            if (start <= now && now < End(e, start) && now - start <= grace) return start;
        }
        return null;
    }

    /// <summary>Entries that should start now and haven't fired yet; records them in <paramref name="fired"/> (key id@start).</summary>
    public static List<(ScheduleEntry Entry, DateTime Start)> Due(IEnumerable<ScheduleEntry> entries, DateTime now, ISet<string> fired, TimeSpan? grace = null)
    {
        var res = new List<(ScheduleEntry, DateTime)>();
        foreach (var e in entries)
            if (ActiveOccurrence(e, now, grace ?? TimeSpan.FromMinutes(2)) is DateTime s && fired.Add($"{e.Id}@{s:O}")) res.Add((e, s));
        return res;
    }

    /// <summary>Loads an .m3u/.m3u8/.txt playlist (one path per line, # comments, relative to the file).</summary>
    public static List<string> LoadPlaylistFile(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => Path.IsPathRooted(l) || l.Contains("://") ? l : Path.GetFullPath(Path.Combine(dir, l))).ToList();
    }

    public static string Describe(ScheduleEntry e)
    {
        var when = e.Repeat switch
        {
            Recurrence.Once => e.Date.ToString("ddd yyyy-MM-dd"),
            Recurrence.Daily => "Daily",
            _ => "Weekly " + (e.Days.Count == 0 ? e.Date.DayOfWeek.ToString()[..3] : string.Join(",", e.Days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3]))),
        };
        return $"{when} {e.Start:hh\\:mm}" + (e.Stop is TimeSpan s ? $"–{s:hh\\:mm}" : "");
    }
}
