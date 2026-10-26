using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace EpikStreamer.Core;

/// <summary>One scanned media file (v2.3). Metadata is cached and only re-probed when size or mtime change.</summary>
public class LibraryItem
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";        // metadata title, else the cleaned-up file name
    public string Artist { get; set; } = "";
    public double Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string VideoCodec { get; set; } = "";
    public string AudioCodec { get; set; } = "";
    public long Size { get; set; }
    public long MtimeTicks { get; set; }
    public string Thumb { get; set; } = "";        // cached JPEG path ("" = none yet)
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public int Plays { get; set; }
    public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : Artist + " - " + Title;
    public string Resolution => Height > 0 ? Height + "p" : "";
    public string DurationText => FormatTime(Duration);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public static string FormatTime(double s)
    {
        if (s <= 0 || double.IsNaN(s)) return "--:--";
        var t = TimeSpan.FromSeconds(Math.Round(s));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
    public static string TitleFromFile(string path)
    {
        var n = System.IO.Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('.', ' ');
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s*[\[\(](official|lyrics?|hd|4k|1080p|720p|audio|video|music video)[^\]\)]*[\]\)]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(n, @"\s{2,}", " ").Trim();
    }
}

public class NamedPlaylist
{
    public string Name { get; set; } = "New playlist";
    public List<string> Items { get; set; } = new();
    public override string ToString() => $"{Name} ({Items.Count})";
}

public class LibrarySettings
{
    public List<string> Folders { get; set; } = new();
    public bool Subfolders { get; set; } = true;
    public string Extensions { get; set; } = ".mp4 .mkv .mov .avi .webm .flv .m4v .ts .wmv .mpg";
    public bool Thumbnails { get; set; } = true;
    public HashSet<string> ExtSet() => Extensions.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant()).ToHashSet();
}

/// <summary>library.json: settings, cached items and saved playlists.</summary>
public class LibraryStore
{
    public LibrarySettings Settings { get; set; } = new();
    public List<LibraryItem> Items { get; set; } = new();
    public List<NamedPlaylist> Playlists { get; set; } = new();
    readonly object _lk = new();

    public static string DefaultDir => System.IO.Path.Combine(ProfileStore.Dir, "library");
    [System.Text.Json.Serialization.JsonIgnore] public string Dir { get; set; } = DefaultDir;
    [System.Text.Json.Serialization.JsonIgnore] public string FilePath => System.IO.Path.Combine(Dir, "library.json");
    [System.Text.Json.Serialization.JsonIgnore] public string ThumbDir => System.IO.Path.Combine(Dir, "thumbs");

    public static LibraryStore Load(string? dir = null)
    {
        dir ??= DefaultDir; LibraryStore? s = null;
        try { var f = System.IO.Path.Combine(dir, "library.json"); if (File.Exists(f)) s = JsonSerializer.Deserialize<LibraryStore>(File.ReadAllText(f), ProfileStore.Json); } catch { }
        s ??= new LibraryStore(); s.Dir = dir; return s;
    }
    public void Save()
    {
        lock (_lk)
        {
            Directory.CreateDirectory(Dir); var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, ProfileStore.Json)); File.Move(tmp, FilePath, true);
        }
    }
    public List<LibraryItem> Snapshot() { lock (_lk) return Items.ToList(); }
    public LibraryItem? Find(string path) { lock (_lk) return Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)); }
    public void Replace(List<LibraryItem> items) { lock (_lk) Items = items; }

    // ---- search / sort / filter ----
    public static IEnumerable<LibraryItem> Query(IEnumerable<LibraryItem> items, string search, string sort, string filter)
    {
        var q = items;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var words = Fuzzy.Norm(search).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            q = q.Where(i => { var h = Fuzzy.Norm(i.Display + " " + System.IO.Path.GetFileName(i.Path)); return words.All(h.Contains); });
        }
        q = filter switch
        {
            "HD (720p+)" => q.Where(i => i.Height >= 720),
            "SD (<720p)" => q.Where(i => i.Height > 0 && i.Height < 720),
            "Short (<5 min)" => q.Where(i => i.Duration > 0 && i.Duration < 300),
            "Long (≥5 min)" => q.Where(i => i.Duration >= 300),
            "Never played" => q.Where(i => i.Plays == 0),
            "Missing metadata" => q.Where(i => i.Duration <= 0),
            _ => q,
        };
        return sort switch
        {
            "Duration" => q.OrderBy(i => i.Duration),
            "Recently added" => q.OrderByDescending(i => i.AddedUtc),
            "Most played" => q.OrderByDescending(i => i.Plays).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
            "Folder" => q.OrderBy(i => i.Folder, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase),
            "Resolution" => q.OrderByDescending(i => i.Height),
            _ => q.OrderBy(i => i.Display, StringComparer.OrdinalIgnoreCase),
        };
    }
    public static readonly string[] Sorts = { "Title", "Duration", "Recently added", "Most played", "Folder", "Resolution" };
    public static readonly string[] Filters = { "All", "HD (720p+)", "SD (<720p)", "Short (<5 min)", "Long (≥5 min)", "Never played", "Missing metadata" };

    public void MarkPlayed(string path) { lock (_lk) { var i = Items.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)); if (i != null) i.Plays++; } }
}

/// <summary>Scans the watched folders with ffprobe and caches metadata + thumbnails.</summary>
public static class LibraryScanner
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IEnumerable<string> Enumerate(LibrarySettings s)
    {
        var ext = s.ExtSet();
        foreach (var folder in s.Folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = s.Subfolders, IgnoreInaccessible = true }); }
            catch { continue; }
            foreach (var f in files) if (ext.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())) yield return f;
        }
    }

    /// <summary>Returns the new item list (unchanged files reuse the cache; files that vanished are dropped).</summary>
    public static async Task<(int total, int probed, int failed)> ScanAsync(LibraryStore store, string ffprobe, string? ffmpeg, IProgress<string>? progress, CancellationToken ct)
    {
        var old = store.Snapshot().ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        var files = Enumerate(store.Settings).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<LibraryItem>(); int probed = 0, failed = 0, n = 0;
        Directory.CreateDirectory(store.ThumbDir);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested(); n++;
            FileInfo fi; try { fi = new FileInfo(f); } catch { continue; }
            if (old.TryGetValue(f, out var c) && c.Size == fi.Length && c.MtimeTicks == fi.LastWriteTimeUtc.Ticks && c.Duration > 0)
            {
                if (store.Settings.Thumbnails && ffmpeg != null && (c.Thumb == "" || !File.Exists(c.Thumb))) c.Thumb = await ThumbAsync(ffmpeg, c, store.ThumbDir, ct) ?? "";
                result.Add(c); continue;
            }
            progress?.Report($"Scanning {n}/{files.Count}: {System.IO.Path.GetFileName(f)}");
            var item = await ProbeAsync(ffprobe, f, ct) ?? new LibraryItem { Path = f, Title = LibraryItem.TitleFromFile(f) };
            if (item.Duration <= 0) failed++; else probed++;
            item.Size = fi.Length; item.MtimeTicks = fi.LastWriteTimeUtc.Ticks;
            if (c != null) { item.AddedUtc = c.AddedUtc; item.Plays = c.Plays; }
            if (store.Settings.Thumbnails && ffmpeg != null && item.Duration > 0) item.Thumb = await ThumbAsync(ffmpeg, item, store.ThumbDir, ct) ?? "";
            result.Add(item);
        }
        store.Replace(result); store.Save();
        progress?.Report($"Library: {result.Count} files ({probed} new/changed, {failed} unreadable)");
        return (result.Count, probed, failed);
    }

    public static List<string> ProbeArgs(string file) => new() { "-v", "error", "-show_entries",
        "format=duration:format_tags=title,artist:stream=codec_type,codec_name,width,height", "-of", "json", file };

    public static LibraryItem? ParseProbe(string file, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json); var r = doc.RootElement;
            var it = new LibraryItem { Path = file, Title = LibraryItem.TitleFromFile(file) };
            if (r.TryGetProperty("format", out var f))
            {
                if (f.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, Inv, out var dur)) it.Duration = dur;
                if (f.TryGetProperty("tags", out var tags))
                    foreach (var t in tags.EnumerateObject())
                    {
                        var v = t.Value.GetString()?.Trim() ?? "";
                        if (v.Length == 0) continue;
                        if (t.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) it.Title = v;
                        else if (t.Name.Equals("artist", StringComparison.OrdinalIgnoreCase)) it.Artist = v;
                    }
            }
            if (r.TryGetProperty("streams", out var ss))
                foreach (var s in ss.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var ty) ? ty.GetString() : "";
                    var name = s.TryGetProperty("codec_name", out var cn) ? cn.GetString() ?? "" : "";
                    if (type == "video" && it.VideoCodec == "" && name is not ("mjpeg" or "png"))   // skip cover art
                    { it.VideoCodec = name; it.Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0; it.Height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0; }
                    else if (type == "audio" && it.AudioCodec == "") it.AudioCodec = name;
                }
            return it;
        }
        catch { return null; }
    }

    static async Task<LibraryItem?> ProbeAsync(string ffprobe, string file, CancellationToken ct)
    {
        try
        {
            var (code, outp) = await RunAsync(ffprobe, ProbeArgs(file), 20000, ct);
            return code == 0 ? ParseProbe(file, outp) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static string ThumbName(LibraryItem it) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(it.Path.ToLowerInvariant() + "|" + it.Size + "|" + it.MtimeTicks)))[..16].ToLowerInvariant() + ".jpg";

    public static List<string> ThumbArgs(LibraryItem it, string outFile)
    {
        double at = it.Duration > 0 ? Math.Min(30, it.Duration * 0.1) : 0;
        return new() { "-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-ss", at.ToString("0.###", Inv), "-i", it.Path,
            "-frames:v", "1", "-vf", "scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2", "-q:v", "4", "-update", "1", outFile };
    }

    static async Task<string?> ThumbAsync(string ffmpeg, LibraryItem it, string dir, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(dir, ThumbName(it));
        if (File.Exists(path)) return path;
        try { var (code, _) = await RunAsync(ffmpeg, ThumbArgs(it, path), 20000, ct); return code == 0 && File.Exists(path) ? path : null; }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static async Task<(int code, string stdout)> RunAsync(string exe, List<string> args, int timeoutMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(ct); var e = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch { try { p.Kill(true); } catch { } throw; }
        return (p.ExitCode, await o);
    }
}

/// <summary>Fuzzy title matching for !request (normalized token overlap + prefix + edit distance).</summary>
public static class Fuzzy
{
    public static string Norm(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD); var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public static int Lev(string a, string b)
    {
        if (a.Length == 0) return b.Length; if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1]; var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++) cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>0..1 similarity of a query to a candidate title.</summary>
    public static double Score(string query, string candidate)
    {
        var q = Norm(query); var c = Norm(candidate);
        if (q.Length == 0 || c.Length == 0) return 0;
        if (q == c) return 1;
        if (c.Contains(q)) return 0.9 + 0.09 * q.Length / c.Length;
        var qt = q.Split(' '); var ct = c.Split(' ');
        double hit = 0;
        foreach (var w in qt)
        {
            double best = 0;
            foreach (var x in ct)
            {
                if (x == w) { best = 1; break; }
                if (w.Length >= 3 && x.StartsWith(w)) best = Math.Max(best, 0.85);
                else if (w.Length >= 4 && x.Length >= 4) { var d = Lev(w, x); best = Math.Max(best, d <= (w.Length >= 7 ? 2 : 1) ? 0.75 : 0); }
            }
            hit += best;
        }
        var tok = hit / qt.Length;                                   // coverage of the query
        var whole = 1.0 - (double)Lev(q, c) / Math.Max(q.Length, c.Length);
        return Math.Max(tok * 0.85, whole * 0.8);
    }

    public record Match(LibraryItem Item, double Score);
    public static List<Match> Rank(string query, IEnumerable<LibraryItem> items, int take = 5) =>
        items.Select(i => new Match(i, Math.Max(Score(query, i.Display), Math.Max(Score(query, i.Title), Score(query, System.IO.Path.GetFileNameWithoutExtension(i.Path))))))
             .Where(m => m.Score > 0.3).OrderByDescending(m => m.Score).ThenBy(m => m.Item.Title.Length).Take(take).ToList();
}
