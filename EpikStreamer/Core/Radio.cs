using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace EpikStreamer.Core;

/// <summary>v2.6 "Radio": an internet radio stream (Icecast/Shoutcast MP3/AAC or HLS, e.g. an AzuraCast station) as background audio.
/// Stored per profile (Profile.Radio). The optional AzuraCast API key is kept encrypted (DPAPI / libsecret), never in plain text.</summary>
public class RadioSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";                 // .pls / .m3u / .m3u8 playlist URL or a direct stream URL
    public double Volume { get; set; } = 0.35;
    public bool Duck { get; set; } = true;                // duck under the stream audio / mic (uses Audio → ducking threshold/ratio)
    public bool Normalize { get; set; }                   // loudnorm on the radio branch (the global Audio normalization still applies to the mix)
    public bool PreferHls { get; set; }                   // default: prefer the MP3/AAC entry, fall back to HLS
    public bool NowPlaying { get; set; } = true;          // poll AzuraCast now-playing (auto-detected from the URL)
    public bool FeedCard { get; set; } = true;            // radio track → the on-stream now-playing card (Overlays tab card must be on)
    public bool ShowListeners { get; set; }
    public bool PitCard { get; set; } = true;             // radio card inside the Ball Pit scene
    public int PollSeconds { get; set; } = 10;
    public string ApiBase { get; set; } = "";             // blank = auto-detect from the URL
    public string Shortcode { get; set; } = "";           // blank = auto-detect from the URL
    public string? ProtectedApiKey { get; set; }          // optional; sent as X-API-Key only when set
    public bool Requests { get; set; }                    // !request → AzuraCast song requests while the radio is on (default off)
    public bool BeatPulse { get; set; }                   // Ball Pit balls bounce/pulse with the radio loudness
    public double BeatStrength { get; set; } = 0.5;
    public string SongTemplate { get; set; } = "Now playing on {station}: {radio}";
    public string LastTemplate { get; set; } = "Last song on {station}: {last}";
    public bool Active => Enabled && Url.Trim().Length > 0;
}

public record RadioEntry(string Url, string Title)
{
    public bool IsHls => RadioPlaylist.IsHlsUrl(Url);
}

/// <summary>.pls / .m3u parsing and stream choice (pure; unit-tested).</summary>
public static class RadioPlaylist
{
    public static bool IsHlsUrl(string u) { var p = PathOf(u); return p.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase); }
    public static bool IsPlaylistUrl(string u) { var p = PathOf(u).ToLowerInvariant(); return p.EndsWith(".pls") || p.EndsWith(".m3u") || p.EndsWith(".m3u8") || p.EndsWith(".xspf"); }
    static string PathOf(string u) => Uri.TryCreate(u.Trim(), UriKind.Absolute, out var x) ? x.AbsolutePath : u.Split('?', '#')[0];
    public static bool IsHlsText(string t) => t.Contains("#EXT-X-TARGETDURATION") || t.Contains("#EXT-X-STREAM-INF") || t.Contains("#EXT-X-MEDIA-SEQUENCE");
    static bool IsHttp(string u) => Uri.TryCreate(u, UriKind.Absolute, out var x) && (x.Scheme == "http" || x.Scheme == "https");

    public static List<RadioEntry> ParsePls(string text)
    {
        var files = new SortedDictionary<int, string>(); var titles = new Dictionary<int, string>();
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim(); var m = Regex.Match(l, @"^(File|Title)(\d+)\s*=\s*(.*)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); var v = m.Groups[3].Value.Trim();
            if (m.Groups[1].Value.Equals("File", StringComparison.OrdinalIgnoreCase)) files[n] = v; else titles[n] = v;
        }
        return files.Where(f => IsHttp(f.Value)).Select(f => new RadioEntry(f.Value, titles.GetValueOrDefault(f.Key, ""))).ToList();
    }

    public static List<RadioEntry> ParseM3u(string text, string baseUrl)
    {
        var r = new List<RadioEntry>(); string title = "";
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim(); if (l.Length == 0) continue;
            if (l.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) { var c = l.IndexOf(','); title = c >= 0 ? l[(c + 1)..].Trim() : ""; continue; }
            if (l.StartsWith('#')) continue;
            var u = Uri.TryCreate(new Uri(baseUrl), l, out var abs) ? abs.ToString() : l;
            if (IsHttp(u)) r.Add(new RadioEntry(u, title)); title = "";
        }
        return r;
    }

    /// <summary>Parses a downloaded playlist. An HLS media/master playlist is itself the stream.</summary>
    public static List<RadioEntry> Parse(string text, string url)
    {
        if (IsHlsText(text)) return new() { new RadioEntry(url, "HLS") };
        if (Regex.IsMatch(text, @"^\s*\[playlist\]", RegexOptions.IgnoreCase | RegexOptions.Multiline) || Regex.IsMatch(text, @"^File1\s*=", RegexOptions.IgnoreCase | RegexOptions.Multiline)) return ParsePls(text);
        return ParseM3u(text, url);
    }

    /// <summary>Order of streams to try: MP3 first (then other non-HLS, e.g. AAC), HLS last, unless HLS is preferred.</summary>
    public static List<RadioEntry> Order(IEnumerable<RadioEntry> entries, bool preferHls)
    {
        int Rank(RadioEntry e)
        {
            if (e.IsHls) return preferHls ? 0 : 3;
            var s = (e.Title + " " + e.Url).ToLowerInvariant();
            return (preferHls ? 1 : 0) + (s.Contains("mp3") ? 0 : 1);
        }
        return entries.Select((e, i) => (e, i)).OrderBy(x => Rank(x.e)).ThenBy(x => x.i).Select(x => x.e).ToList();
    }

    /// <summary>Downloads a playlist URL (never a stream body) and returns the streams in preference order.</summary>
    public static async Task<List<RadioEntry>> ResolveAsync(HttpClient http, string url, bool preferHls, CancellationToken ct)
    {
        url = url.Trim();
        if (!IsHttp(url)) throw new ArgumentException("Enter an http(s) URL of a .pls/.m3u/.m3u8 playlist or a stream.");
        bool byExt = IsPlaylistUrl(url);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var ctype = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        bool isList = byExt || ctype.Contains("scpls") || ctype.Contains("mpegurl") || ctype.Contains("x-mpegurl") || ctype.Contains("text/");
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode} for the playlist");
        if (!isList) return new() { new RadioEntry(url, ctype) };     // a direct stream (audio/mpeg, audio/aac …): don't read the body
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[65536]; int n = 0, k;
        while (n < buf.Length && (k = await s.ReadAsync(buf.AsMemory(n), ct)) > 0) n += k;
        var text = Encoding.UTF8.GetString(buf, 0, n);
        var e = Parse(text, url);
        if (e.Count == 0) throw new InvalidDataException("No stream URLs in the playlist.");
        return Order(e, preferHls);
    }
}

public record AzuraTarget(string Base, string Shortcode);
public record AzuraSong(string Id, string Text, string Artist, string Title, string Album, string Art);
public record AzuraRequestable(string RequestId, string RequestUrl, AzuraSong Song);

/// <summary>Parsed AzuraCast /api/nowplaying/{station} (only what we use).</summary>
public sealed class AzuraNowPlaying
{
    public string Station = "", Shortcode = "", StationId = ""; public bool Online, RequestsEnabled, IsLive; public string Streamer = "";
    public AzuraSong Song = new("", "", "", "", "", ""); public double Elapsed, Duration; public DateTime FetchedUtc;
    public string Next = ""; public int? Listeners; public List<AzuraSong> History = new();
    public string Display => Song.Artist.Length > 0 && Song.Title.Length > 0 ? $"{Song.Artist} – {Song.Title}" : Song.Text;
    public double PositionAt(DateTime utc) => Duration > 0 ? Math.Clamp(Elapsed + (utc - FetchedUtc).TotalSeconds, 0, Duration) : 0;
    public string Key => Song.Id + "|" + Song.Text;

    static string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), _ => "" } : "";
    static double N(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    static bool B(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static JsonElement O(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v : default;
    public static AzuraSong Song_(JsonElement s) => new(S(s, "id"), S(s, "text"), S(s, "artist"), S(s, "title"), S(s, "album"), S(s, "art"));

    public static AzuraNowPlaying Parse(string json, DateTime nowUtc)
    {
        using var d = JsonDocument.Parse(json); var r = d.RootElement;
        if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0) r = r[0];
        var st = O(r, "station"); var np = O(r, "now_playing");
        var x = new AzuraNowPlaying
        {
            Station = S(st, "name"), Shortcode = S(st, "shortcode"), StationId = S(st, "id"), RequestsEnabled = B(st, "requests_enabled"),
            Online = !(r.TryGetProperty("is_online", out var on) && on.ValueKind == JsonValueKind.False),
            Song = Song_(O(np, "song")), Elapsed = N(np, "elapsed"), Duration = N(np, "duration"), FetchedUtc = nowUtc,
            Next = Song_(O(O(r, "playing_next"), "song")) is var nx && nx.Artist.Length > 0 && nx.Title.Length > 0 ? $"{nx.Artist} – {nx.Title}" : Song_(O(O(r, "playing_next"), "song")).Text,
            IsLive = B(O(r, "live"), "is_live"), Streamer = S(O(r, "live"), "streamer_name"),
        };
        var li = O(r, "listeners"); if (li.ValueKind == JsonValueKind.Object) x.Listeners = (int)N(li, "current");
        // AzuraCast reports elapsed = "now" with duration 0 when nothing is scheduled (e.g. offline): treat as unknown
        if (x.Duration <= 0 || x.Elapsed < 0 || x.Elapsed > x.Duration + 30) { x.Elapsed = 0; x.Duration = 0; }
        if (r.TryGetProperty("song_history", out var h) && h.ValueKind == JsonValueKind.Array)
            foreach (var e in h.EnumerateArray().Take(10)) x.History.Add(Song_(O(e, "song")));
        return x;
    }
}

/// <summary>Documented public AzuraCast API: now playing, requestable songs, submit a request. Optional X-API-Key.</summary>
public sealed class AzuraClient
{
    public static readonly string UserAgent = "EpikStreamer/" + (typeof(AzuraClient).Assembly.GetName().Version?.ToString(3) ?? "2");
    readonly HttpClient _http; public AzuraTarget Target { get; } readonly string? _key;
    public AzuraClient(HttpClient http, AzuraTarget t, string? apiKey = null) { _http = http; Target = t; _key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(); }

    /// <summary>https://host[/prefix]/public|listen|hls|api/nowplaying|api/station/{shortcode}… → base + shortcode.</summary>
    public static AzuraTarget? Detect(string url)
    {
        var m = Regex.Match(url.Trim(), @"^(https?://[^/?#]+(?:/[^?#]*?)?)/(?:public|listen|hls|api/nowplaying|api/station|api/live/nowplaying/[a-z]+)/([A-Za-z0-9_\-]+)(?:[/?#]|$)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var sc = m.Groups[2].Value; if (sc.Equals("sse", StringComparison.OrdinalIgnoreCase)) return null;
        return new AzuraTarget(m.Groups[1].Value.TrimEnd('/'), sc);
    }

    HttpRequestMessage Req(HttpMethod m, string path)
    {
        var r = new HttpRequestMessage(m, Target.Base + path);
        r.Headers.TryAddWithoutValidation("User-Agent", UserAgent); r.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (_key != null) r.Headers.TryAddWithoutValidation("X-API-Key", _key);
        return r;
    }
    async Task<string> Get(string path, CancellationToken ct)
    {
        using var r = Req(HttpMethod.Get, path); using var resp = await _http.SendAsync(r, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Msg(body)}");
        return body;
    }
    static string Msg(string body)
    {
        try { using var d = JsonDocument.Parse(body); var r = d.RootElement; foreach (var k in new[] { "formatted_message", "message" }) if (r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s) return s; }
        catch { }
        return body.Length > 160 ? body[..160] : body;
    }

    public async Task<AzuraNowPlaying> NowPlaying(CancellationToken ct) => AzuraNowPlaying.Parse(await Get($"/api/nowplaying/{Uri.EscapeDataString(Target.Shortcode)}", ct), DateTime.UtcNow);

    public async Task<List<AzuraRequestable>> Search(string phrase, int rows, CancellationToken ct)
    {
        var body = await Get($"/api/station/{Uri.EscapeDataString(Target.Shortcode)}/requests?searchPhrase={Uri.EscapeDataString(phrase)}&rowCount={rows}&page=1", ct);
        using var d = JsonDocument.Parse(body); var r = d.RootElement;
        var arr = r.ValueKind == JsonValueKind.Array ? r : r.TryGetProperty("rows", out var rr) ? rr : default;
        var list = new List<AzuraRequestable>();
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                var id = e.TryGetProperty("request_id", out var v) ? v.GetString() ?? "" : ""; if (id.Length == 0) continue;
                list.Add(new(id, e.TryGetProperty("request_url", out var u) ? u.GetString() ?? "" : "", AzuraNowPlaying.Song_(e.TryGetProperty("song", out var s) ? s : default)));
            }
        return list;
    }

    /// <summary>Submits a request. Returns (ok, AzuraCast's message). AzuraCast rate-limits requests per client IP.</summary>
    public async Task<(bool ok, string message)> Submit(AzuraRequestable item, string stationId, CancellationToken ct)
    {
        var path = item.RequestUrl.StartsWith("/api/", StringComparison.Ordinal) ? item.RequestUrl
            : $"/api/station/{Uri.EscapeDataString(stationId.Length > 0 ? stationId : Target.Shortcode)}/request/{Uri.EscapeDataString(item.RequestId)}";
        foreach (var m in new[] { HttpMethod.Post, HttpMethod.Get })   // POST per the API docs; older versions accept GET
        {
            using var r = Req(m, path); using var resp = await _http.SendAsync(r, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed) continue;
            bool ok = resp.IsSuccessStatusCode;
            try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("success", out var sv)) ok = ok && sv.ValueKind != JsonValueKind.False; } catch { }
            return (ok, Msg(body));
        }
        return (false, "request method not allowed");
    }
}

/// <summary>Polls AzuraCast now-playing (default every 10 s) and downloads album art to a cache folder.</summary>
public sealed class AzuraPoller : IDisposable
{
    readonly AzuraClient _c; readonly string _artDir; readonly HttpClient _http; readonly System.Threading.Timer _t; int _busy; bool _disposed;
    public AzuraNowPlaying? Current { get; private set; }
    public string ArtFile { get; private set; } = "";
    public byte[]? ArtBytes { get; private set; }
    public string LastError { get; private set; } = "";
    public AzuraClient Client => _c;
    public event Action<AzuraNowPlaying>? Updated, SongChanged;
    public AzuraPoller(AzuraClient c, HttpClient http, string artDir, int seconds)
    {
        _c = c; _http = http; _artDir = artDir; Directory.CreateDirectory(artDir);
        var p = TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 300));
        _t = new System.Threading.Timer(_ => _ = PollAsync(), null, TimeSpan.Zero, p);
    }
    public async Task PollAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var np = await _c.NowPlaying(cts.Token);
            bool changed = Current == null || Current.Key != np.Key;
            if (changed) await FetchArt(np, cts.Token);
            Current = np; LastError = "";
            Updated?.Invoke(np); if (changed) SongChanged?.Invoke(np);
        }
        catch (Exception ex) { LastError = ex.Message; }
        finally { Volatile.Write(ref _busy, 0); }
    }
    async Task FetchArt(AzuraNowPlaying np, CancellationToken ct)
    {
        var url = np.Song.Art; if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { ArtFile = ""; ArtBytes = null; return; }
        try
        {
            var f = Path.Combine(_artDir, "radio-art-" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16] + ".img");
            if (!File.Exists(f))
            {
                using var r = new HttpRequestMessage(HttpMethod.Get, url); r.Headers.TryAddWithoutValidation("User-Agent", AzuraClient.UserAgent);
                using var resp = await _http.SendAsync(r, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentLength > 5_000_000) { ArtFile = ""; ArtBytes = null; return; }
                var data = await resp.Content.ReadAsByteArrayAsync(ct); if (data.Length > 5_000_000) return;
                File.WriteAllBytes(f + ".tmp", data); File.Move(f + ".tmp", f, true);
                foreach (var old in Directory.GetFiles(_artDir, "radio-art-*.img").Select(p => new FileInfo(p)).OrderByDescending(i => i.LastWriteTimeUtc).Skip(40)) try { old.Delete(); } catch { }
            }
            ArtBytes = File.ReadAllBytes(f); ArtFile = f;
        }
        catch { ArtFile = ""; ArtBytes = null; }
    }
    public void Dispose() { _disposed = true; _t.Dispose(); }
}

/// <summary>Chat !request → AzuraCast song requests (search the requestable list, submit the best match), with per-user cooldowns.</summary>
public sealed class AzuraRequests
{
    readonly Func<AzuraClient?> _client; readonly Func<AzuraNowPlaying?> _np;
    readonly Dictionary<string, DateTime> _last = new(StringComparer.OrdinalIgnoreCase); DateTime _lastAny = DateTime.MinValue;
    public AzuraRequests(Func<AzuraClient?> client, Func<AzuraNowPlaying?> np) { _client = client; _np = np; }
    public static bool IsRequest(string text) => text.Trim().Split(' ', 2)[0].ToLowerInvariant() is "!request" or "!sr";

    public async Task<string?> Handle(string user, string text, int userCooldownSeconds, int globalCooldownSeconds, DateTime now, CancellationToken ct)
    {
        var parts = text.Trim().Split(' ', 2); var q = parts.Length > 1 ? parts[1].Trim() : "";
        var c = _client(); var np = _np();
        if (c == null) return null;
        if (np != null && !np.RequestsEnabled) return $"{(np.Station.Length > 0 ? np.Station : "The radio")} isn't taking song requests right now.";
        if (q.Length == 0) return "Usage: !request <artist or song> (requests go to the radio station)";
        if (q.Length > 100) q = q[..100];
        lock (_last)
        {
            if (_last.TryGetValue(user, out var t) && (now - t).TotalSeconds < userCooldownSeconds) return $"@{user} please wait {Math.Ceiling(userCooldownSeconds - (now - t).TotalSeconds)} s before requesting again.";
            if ((now - _lastAny).TotalSeconds < globalCooldownSeconds) return null;
            _last[user] = now; _lastAny = now;
        }
        try
        {
            var rows = await c.Search(q, 10, ct);
            if (rows.Count == 0) return $"@{user} no requestable song matches \"{q}\".";
            var best = rows.OrderByDescending(r => Score(q, r.Song)).First();
            var (ok, msg) = await c.Submit(best, np?.StationId ?? "", ct);
            var name = best.Song.Artist.Length > 0 ? $"{best.Song.Artist} – {best.Song.Title}" : best.Song.Text;
            if (!ok) lock (_last) _last.Remove(user);   // a refused request doesn't use up the user's cooldown
            return ok ? $"🎵 Requested on the radio for {user}: {name}" : $"@{user} the station refused the request: {msg}";
        }
        catch (Exception ex) { lock (_last) _last.Remove(user); return $"@{user} couldn't reach the radio for requests ({ex.Message})."; }
    }
    static double Score(string q, AzuraSong s)
    {
        var words = Regex.Split(q.ToLowerInvariant(), @"\W+").Where(w => w.Length > 0).ToArray(); if (words.Length == 0) return 0;
        var hay = (s.Artist + " " + s.Title + " " + s.Text).ToLowerInvariant();
        return words.Count(w => hay.Contains(w)) / (double)words.Length + (hay.Contains(q.ToLowerInvariant()) ? 0.5 : 0);
    }
}

/// <summary>Keeps one radio connection alive for a whole stream: a small ffmpeg "relay" decodes the station to raw PCM
/// (s16le, 48 kHz stereo) into a jitter buffer. If the station hiccups, the relay restarts in the background with backoff
/// (and rotates to the next stream, e.g. HLS); the encoder never sees it — <see cref="RadioFeed"/> fills gaps with silence.</summary>
public sealed class RadioRelay : IDisposable
{
    public const int Rate = 48000, Bps = Rate * 4;   // bytes per second
    readonly string _ffmpeg; readonly List<string> _urls; readonly FfmpegCaps _caps; readonly double _maxBuf;
    readonly object _lk = new(); readonly byte[] _ring; int _head, _count; bool _primed; volatile bool _disposed;
    Process? _proc; Thread? _thread; DateTime _lastData = DateTime.MinValue;
    public int Reconnects { get; private set; }
    public long BytesIn { get; private set; }
    public int UrlIndex { get; private set; }
    public string CurrentUrl => _urls[Math.Min(UrlIndex, _urls.Count - 1)];
    public string State { get; private set; } = "starting";
    public event Action<string>? Log;
    public bool Receiving => (DateTime.UtcNow - _lastData).TotalSeconds < 3;
    public double Buffered { get { lock (_lk) return _count / (double)Bps; } }
    public double PrerollSeconds { get; set; } = 1.0;
    public double PrerollHls { get; set; } = 6.0;
    /// <summary>Test hook: wraps the input URL (e.g. to inject failures).</summary>
    public Func<string, string>? UrlFilter { get; set; }

    public RadioRelay(string ffmpeg, IEnumerable<string> urls, FfmpegCaps? caps = null, double maxBufferSeconds = 20)
    {
        _ffmpeg = ffmpeg; _urls = urls.Where(u => u.Length > 0).ToList(); if (_urls.Count == 0) throw new ArgumentException("no radio URL");
        _caps = caps ?? FfmpegCaps.All; _maxBuf = maxBufferSeconds; _ring = new byte[(int)(maxBufferSeconds * Bps) & ~3];
    }

    public static List<string> RelayArgs(string url, FfmpegCaps caps, bool hls)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "warning" };
        bool http = url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        if (http)
        {   // protocol options are per input and go immediately before its -i
            if (caps.HttpReconnect) a.AddRange(new[] { "-reconnect", "1", "-reconnect_streamed", "1" });
            if (caps.HttpReconnectDelayMax) a.AddRange(new[] { "-reconnect_delay_max", "5" });
            if (caps.RwTimeout) a.AddRange(new[] { "-rw_timeout", "15000000" });
            a.AddRange(new[] { "-user_agent", AzuraClient.UserAgent });
        }
        if (hls) a.AddRange(new[] { "-live_start_index", "-3" });
        a.AddRange(new[] { "-i", url, "-vn", "-sn", "-dn", "-af", "aresample=48000", "-f", "s16le", "-ar", "48000", "-ac", "2", "pipe:1" });
        return a;
    }

    public void Start() { if (_thread != null) return; _thread = new Thread(Loop) { IsBackground = true, Name = "radio relay" }; _thread.Start(); }

    void Loop()
    {
        int fails = 0, noData = 0;
        while (!_disposed)
        {
            var url = CurrentUrl; bool hls = RadioPlaylist.IsHlsUrl(url); long got = 0;
            State = Reconnects == 0 && fails == 0 ? "connecting" : "reconnecting";
            try
            {
                var psi = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
                foreach (var x in RelayArgs(UrlFilter?.Invoke(url) ?? url, _caps, hls)) psi.ArgumentList.Add(x);
                var p = Process.Start(psi)!; _proc = p; p.StandardInput.Close();
                p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) Log?.Invoke("[radio] " + d); }; p.BeginErrorReadLine();
                var so = p.StandardOutput.BaseStream; var buf = new byte[16384]; int n;
                var watchdog = new System.Threading.Timer(_ => { if (got > 0 && (DateTime.UtcNow - _lastData).TotalSeconds > 12 || got == 0 && (DateTime.UtcNow - _started).TotalSeconds > 25) try { p.Kill(true); } catch { } }, null, 2000, 2000);
                _started = DateTime.UtcNow;
                try
                {
                    while (!_disposed && (n = so.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (got == 0) { State = "playing"; if (Reconnects > 0 || fails > 0) Log?.Invoke($"📻 Radio reconnected ({Host(url)})."); }
                        got += n; BytesIn += n; _lastData = DateTime.UtcNow; Push(buf, n, hls);
                    }
                }
                finally { watchdog.Dispose(); try { if (!p.HasExited) p.Kill(true); } catch { } p.WaitForExit(2000); p.Dispose(); _proc = null; }
            }
            catch (Exception ex) { Log?.Invoke("📻 Radio relay error: " + ex.Message); }
            if (_disposed) break;
            Reconnects++;
            if (got > Bps * 5) { fails = 0; noData = 0; } else { fails++; noData++; }
            var wait = TimeSpan.FromSeconds(Math.Min(5, fails == 0 ? 1 : Math.Pow(2, Math.Min(fails, 4)) / 2));   // short: listeners notice
            State = $"lost the station, retrying in {wait.TotalSeconds:0} s";
            Log?.Invoke($"📻 Radio stream dropped ({Host(url)}); the stream continues with silence, retrying in {wait.TotalSeconds:0} s.");
            if (noData >= 2 && _urls.Count > 1) { UrlIndex = (UrlIndex + 1) % _urls.Count; noData = 0; Log?.Invoke($"📻 Switching radio stream to {Host(CurrentUrl)}{(RadioPlaylist.IsHlsUrl(CurrentUrl) ? " (HLS)" : "")}."); }
            for (int i = 0; i < wait.TotalMilliseconds / 100 && !_disposed; i++) Thread.Sleep(100);
        }
        State = "stopped";
    }
    DateTime _started;
    static string Host(string u) => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host + x.AbsolutePath : u;

    void Push(byte[] b, int n, bool hls)
    {
        lock (_lk)
        {
            int cap = _ring.Length;
            for (int off = 0; off < n;)
            {
                int tail = (_head + _count) % cap, room = Math.Min(n - off, cap - tail);
                Buffer.BlockCopy(b, off, _ring, tail, room); off += room; _count += room;
                if (_count > cap) { int over = _count - cap; _head = (_head + over) % cap; _count = cap; }
            }
            // keep latency bounded: an Icecast burst-on-connect or a batch of HLS segments must not pile up
            double pre = hls ? PrerollHls : PrerollSeconds, max = Math.Min(_maxBuf - 1, pre + (hls ? 10 : 4));
            if (_count > max * Bps) { int drop = (_count - (int)(pre * Bps)) & ~3; _head = (_head + drop) % cap; _count -= drop; }
            if (!_primed && _count >= pre * Bps) _primed = true;
        }
    }

    /// <summary>Copies up to dst.Length bytes of buffered PCM (0 while buffering after a drop).</summary>
    public int Take(Span<byte> dst)
    {
        lock (_lk)
        {
            if (!_primed) return 0;
            int n = Math.Min(dst.Length, _count) & ~3, cap = _ring.Length;
            int a = Math.Min(n, cap - _head); _ring.AsSpan(_head, a).CopyTo(dst); if (n > a) _ring.AsSpan(0, n - a).CopyTo(dst[a..]);
            _head = (_head + n) % cap; _count -= n;
            if (_count == 0 && n < dst.Length) _primed = false;   // underrun: buffer up again before resuming
            return n;
        }
    }
    /// <summary>Test hook: kills the current relay process (simulates the station dropping).</summary>
    public void KillCurrent() { try { _proc?.Kill(true); } catch { } }
    public void Dispose() { _disposed = true; KillCurrent(); }
}

/// <summary>Pushes the relay's audio into the encoder's radio input in real time (20 ms chunks), padding with silence when
/// the station is away, and measures the loudness (RMS) for the Ball Pit beat effect.</summary>
public sealed class RadioFeed : IDisposable
{
    readonly RadioRelay _relay; readonly int _port; volatile bool _stop; Thread? _t;
    public long Bytes, SilentBytes; public bool Connected;
    public float Level;          // smoothed RMS 0..1 of what was sent
    public event Action<string>? Log;
    public Action<float>? OnLevel;
    public RadioFeed(RadioRelay relay, int port) { _relay = relay; _port = port; }
    public void Start() { _t = new Thread(Run) { IsBackground = true, Name = "radio feed" }; _t.Start(); }
    void Run()
    {
        TcpClient? tc = null;
        for (int i = 0; i < 200 && !_stop; i++) { try { tc = new TcpClient(); tc.NoDelay = true; tc.Connect("127.0.0.1", _port); break; } catch { tc?.Dispose(); tc = null; Thread.Sleep(100); } }
        if (tc == null) { if (!_stop) Log?.Invoke("📻 Could not connect the radio feed to the encoder."); return; }
        Connected = true;
        const int Chunk = RadioRelay.Bps / 50;   // 20 ms
        var buf = new byte[Chunk]; var sw = Stopwatch.StartNew(); long sent = 0; float lev = 0;
        try
        {
            using var net = tc.GetStream();
            while (!_stop)
            {
                long due = (long)(sw.Elapsed.TotalSeconds * RadioRelay.Bps) + RadioRelay.Bps / 5;   // stay ~200 ms ahead; TCP backpressure paces us
                if (sent >= due) { Thread.Sleep(5); continue; }
                int n = _relay.Take(buf);
                if (n < Chunk) { Array.Clear(buf, n, Chunk - n); SilentBytes += Chunk - n; }
                double sum = 0; for (int i = 0; i + 1 < Chunk; i += 8) { short v = (short)(buf[i] | buf[i + 1] << 8); sum += (double)v * v; }
                float rms = (float)Math.Min(1, Math.Sqrt(sum / (Chunk / 8)) / 32768 * 2.5);
                lev = rms > lev ? lev + (rms - lev) * 0.5f : lev + (rms - lev) * 0.1f; Level = lev; OnLevel?.Invoke(rms);
                net.Write(buf, 0, Chunk); sent += Chunk; Bytes = sent;
                if (sent == Chunk) sw.Restart();   // the encoder starts reading once all its inputs are open
            }
        }
        catch (Exception) { }
        finally { Connected = false; tc.Dispose(); }
    }
    public void Dispose() { _stop = true; }
}

/// <summary>Host-side radio glue shared by the Windows and Linux apps: resolves the playlist, runs the AzuraCast poller,
/// and turns now-playing into the stream card, the Ball Pit card and chat-command values.</summary>
public sealed class RadioHost : IDisposable
{
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    RadioSettings _s; readonly string _artDir;
    string _resolvedKey = ""; List<RadioEntry> _streams = new();
    string _pollKey = ""; AzuraPoller? _poller;
    public AzuraTarget? Target { get; private set; }
    public AzuraPoller? Poller => _poller;
    public AzuraNowPlaying? Current => _poller?.Current;
    public IReadOnlyList<RadioEntry> Streams => _streams;
    public AzuraRequests Requests { get; }
    public event Action<string>? Log;
    public event Action<AzuraNowPlaying>? SongChanged, Updated;

    public RadioHost(RadioSettings s, string artDir)
    {
        _s = s; _artDir = artDir;
        Requests = new AzuraRequests(() => _s.Active && _s.Requests && Target != null ? new AzuraClient(Http, Target, ApiKey) : null, () => Current);
    }
    public RadioSettings Settings => _s;
    string? ApiKey { get { var k = Secret.Unprotect(_s.ProtectedApiKey); return k.Length > 0 ? k : null; } }

    public static AzuraTarget? TargetFor(RadioSettings s)
    {
        var d = AzuraClient.Detect(s.Url);
        var b = s.ApiBase.Trim().TrimEnd('/'); var sc = s.Shortcode.Trim();
        if (b.Length > 0 && !b.StartsWith("http", StringComparison.OrdinalIgnoreCase)) b = "https://" + b;
        if (b.Length == 0) b = d?.Base ?? ""; if (sc.Length == 0) sc = d?.Shortcode ?? "";
        return b.Length > 0 && sc.Length > 0 ? new AzuraTarget(b, sc) : null;
    }

    /// <summary>Apply (possibly changed) settings: starts/stops/restarts the now-playing poller.</summary>
    public void Apply(RadioSettings s)
    {
        _s = s; Target = TargetFor(s);
        var key = s.Active && s.NowPlaying && Target != null ? $"{Target.Base}|{Target.Shortcode}|{s.PollSeconds}|{s.ProtectedApiKey}" : "";
        if (key == _pollKey) return;
        _pollKey = key; _poller?.Dispose(); _poller = null;
        if (key.Length == 0) return;
        _poller = new AzuraPoller(new AzuraClient(Http, Target!, ApiKey), Http, _artDir, s.PollSeconds);
        _poller.Updated += np => Updated?.Invoke(np);
        _poller.SongChanged += np => { Log?.Invoke($"📻 Now playing on {np.Station}: {np.Display}"); SongChanged?.Invoke(np); };
    }

    /// <summary>Stream URLs in preference order (cached per URL); empty if it can't be resolved.</summary>
    public async Task<List<string>> ResolveAsync(CancellationToken ct)
    {
        var key = _s.Url.Trim() + "|" + _s.PreferHls;
        if (key != _resolvedKey || _streams.Count == 0)
        {
            _streams = await RadioPlaylist.ResolveAsync(Http, _s.Url, _s.PreferHls, ct);
            _resolvedKey = key;
        }
        return _streams.Select(e => e.Url).ToList();
    }

    /// <summary>The "Test" button: resolve, decode a few seconds of each stream with ffmpeg, fetch now-playing.</summary>
    public async Task<string> TestAsync(string ffmpeg, CancellationToken ct)
    {
        var sb = new StringBuilder();
        _resolvedKey = "";
        var urls = await ResolveAsync(ct);
        sb.AppendLine($"Playlist OK: {urls.Count} stream(s).");
        var caps = FfmpegCaps.Probe(ffmpeg);
        foreach (var (u, i) in urls.Select((u, i) => (u, i)).Take(2))
        {
            var r = await Task.Run(() => DecodeCheck(ffmpeg, u, caps, 4), ct);
            sb.AppendLine($"{(i == 0 ? "▶" : "  ")} {u}{(RadioPlaylist.IsHlsUrl(u) ? " (HLS)" : "")}: {r}");
        }
        var t = Target ?? TargetFor(_s);
        if (t == null) sb.AppendLine("Not an AzuraCast URL (no now-playing / requests). Set API base + shortcode to override.");
        else
            try
            {
                var np = await new AzuraClient(Http, t, ApiKey).NowPlaying(ct);
                sb.AppendLine($"AzuraCast {t.Base} · {t.Shortcode}: “{np.Station}” {(np.Online ? "online" : "OFFLINE")}, now: {np.Display}" + (np.Next.Length > 0 ? $", next: {np.Next}" : "") + (np.Listeners is int n ? $", {n} listening" : "") + (np.RequestsEnabled ? ", requests enabled" : ", requests disabled"));
            }
            catch (Exception ex) { sb.AppendLine($"AzuraCast now-playing failed ({t.Base}, {t.Shortcode}): {ex.Message}"); }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Decodes <paramref name="seconds"/> s of a stream; returns "ok, 44100 Hz, mean -27.4 dB" or the error.</summary>
    public static string DecodeCheck(string ffmpeg, string url, FfmpegCaps caps, int seconds)
    {
        var a = RadioRelay.RelayArgs(url, caps, RadioPlaylist.IsHlsUrl(url));
        int o = a.IndexOf("-vn"); a.RemoveRange(o, a.Count - o);
        a[a.IndexOf("warning")] = "info";
        a.AddRange(new[] { "-t", seconds.ToString(CultureInfo.InvariantCulture), "-vn", "-af", "volumedetect", "-f", "null", "-" });
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var x in a) psi.ArgumentList.Add(x);
        try
        {
            using var p = Process.Start(psi)!; p.StandardInput.Close(); var err = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((seconds + 20) * 1000)) { try { p.Kill(true); } catch { } return "timed out"; }
            var e = err.Result;
            var mean = Regex.Match(e, @"mean_volume:\s*(-?[\d.]+|-inf) dB"); var hz = Regex.Match(e, @"Audio: (\w+).*?(\d+) Hz");
            if (p.ExitCode != 0 || !mean.Success) return "FAILED: " + (e.Trim().Split('\n').LastOrDefault() ?? "no audio");
            return $"ok, {(hz.Success ? $"{hz.Groups[1].Value} {hz.Groups[2].Value} Hz, " : "")}mean {mean.Groups[1].Value} dB";
        }
        catch (Exception ex) { return "FAILED: " + ex.Message; }
    }

    static string Title(AzuraNowPlaying np) => np.Song.Title.Length > 0 ? np.Song.Title : np.Song.Text;
    string Listeners(AzuraNowPlaying np) => _s.ShowListeners && np.Listeners is int n ? $"{n} listening" : "";

    /// <summary>Now-playing card info (null = radio has no now-playing data → playlist track is shown).</summary>
    public NowPlayingInfo? CardInfo()
    {
        if (!_s.Active || !_s.FeedCard || Current is not { } np) return null;
        var sub = string.Join(" · ", new[] { np.Song.Artist, Listeners(np) }.Where(x => x.Length > 0));
        var label = (np.Online ? "ON AIR" : "OFF AIR") + (np.Station.Length > 0 ? " · " + np.Station : "") + (np.IsLive && np.Streamer.Length > 0 ? " · LIVE: " + np.Streamer : "");
        return new NowPlayingInfo(Title(np), "", np.Next, np.PositionAt(DateTime.UtcNow), np.Duration, _poller?.ArtFile ?? "", label, sub.Length > 0 ? sub : " ", true);
    }
    public RadioCardInfo? PitCard()
    {
        if (!_s.Active || !_s.PitCard || Current is not { } np) return null;
        return new RadioCardInfo(np.Station, Title(np), np.Song.Artist, np.Next, _s.ShowListeners ? np.Listeners : null, np.Elapsed, np.Duration, np.FetchedUtc,
            _poller?.ArtBytes, _poller?.ArtFile ?? "", np.Online);
    }
    public void FillContext(ChatCommands.Context c)
    {
        if (!_s.Active || Current is not { } np) return;
        c.Radio = np.Display; c.RadioLast = np.History.FirstOrDefault() is { } h ? (h.Artist.Length > 0 && h.Title.Length > 0 ? $"{h.Artist} – {h.Title}" : h.Text) : "";
        c.Station = np.Station; c.RadioSongTemplate = _s.SongTemplate; c.RadioLastTemplate = _s.LastTemplate;
    }
    public void Dispose() { _poller?.Dispose(); _poller = null; _pollKey = ""; }
}
