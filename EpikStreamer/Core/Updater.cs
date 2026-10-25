using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
namespace EpikStreamer.Core;

/// <summary>Manifest describing a release: { "version", "url", "sha256", "notes" }.</summary>
public record UpdateInfo(Version Version, string Url, string Sha256, string Notes);

/// <summary>
/// Auto-update. The manifest URL may point at either
///  • a GitHub "latest release" API endpoint (https://api.github.com/repos/OWNER/REPO/releases/latest); the release must
///    carry an "update.json" asset (made by release.ps1), or
///  • an update.json file hosted anywhere (https).
/// Nothing is installed without the user confirming, and the installer is only run if its SHA-256 matches the manifest.
/// </summary>
public static class Updater
{
    public const string DefaultManifestUrl = "https://api.github.com/repos/nano11b/EpikStreamer/releases/latest";
    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
    public static bool IsPlaceholder(string url) => url.Contains("/OWNER/REPO/", StringComparison.Ordinal);

    /// <summary>Optional GitHub token, only to raise API rate limits (public repo needs none). Sent to api.github.com only.</summary>
    public static string? GitHubToken { get; set; }

    static HttpClient NewClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("EpikStreamer-Updater/" + Current);
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return h;
    }

    static void RequireSafe(string url)
    {
        var u = new Uri(url);
        if (u.Scheme == "https") return;
        if (u.Scheme == "http" && u.IsLoopback) return;   // local testing only
        throw new InvalidOperationException("Update URLs must use https: " + url);
    }

    public static Version ParseVersion(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' }); if (cut > 0) s = s[..cut];
        var v = Version.Parse(s.Contains('.') ? s : s + ".0");
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    public static bool IsNewer(Version remote, Version current) =>
        ParseVersion(remote.ToString()) > ParseVersion(current.ToString());

    /// <summary>Returns the manifest (whether or not it's newer); throws on network/format errors.</summary>
    public static async Task<UpdateInfo> FetchAsync(string manifestUrl, CancellationToken ct = default)
    {
        if (IsPlaceholder(manifestUrl)) throw new InvalidOperationException("No update source configured (the manifest URL still has the OWNER/REPO placeholder).");
        RequireSafe(manifestUrl);
        using var http = NewClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        if (!string.IsNullOrWhiteSpace(GitHubToken) && new Uri(manifestUrl).Host == "api.github.com")
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", GitHubToken.Trim());
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("assets", out var assets))   // GitHub release JSON
        {
            foreach (var a in assets.EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), "update.json", StringComparison.OrdinalIgnoreCase))
                {
                    var u = a.GetProperty("browser_download_url").GetString()!;
                    RequireSafe(u);
                    return ParseManifest(await http.GetStringAsync(u, ct));
                }
            // fallback: installer .exe + "<installer>.sha256" asset, version from the tag
            string? exe = null, shaUrl = null; string exeName = "";
            foreach (var a in assets.EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? ""; var u = a.GetProperty("browser_download_url").GetString() ?? "";
                if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && n.Contains("setup", StringComparison.OrdinalIgnoreCase)) { exe = u; exeName = n; }
            }
            foreach (var a in assets.EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (exe != null && (n.Equals(exeName + ".sha256", StringComparison.OrdinalIgnoreCase) || n.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)))
                    shaUrl = a.GetProperty("browser_download_url").GetString();
            }
            if (exe == null || shaUrl == null) throw new InvalidOperationException("The latest release has neither update.json nor a *-setup.exe with a .sha256 asset.");
            RequireSafe(exe); RequireSafe(shaUrl);
            var sums = await http.GetStringAsync(shaUrl, ct);
            string? hash = null;
            foreach (var line in sums.Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '*', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 1 && parts[0].Length == 64 && (parts.Length == 1 || parts[^1].Equals(exeName, StringComparison.OrdinalIgnoreCase))) { hash = parts[0].ToLowerInvariant(); break; }
            }
            if (hash == null || !hash.All(Uri.IsHexDigit)) throw new InvalidOperationException("Could not read the installer's SHA-256 from " + shaUrl);
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "0" : "0";
            var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new UpdateInfo(ParseVersion(tag), exe, hash, notes);
        }
        return ParseManifest(json);
    }

    public static UpdateInfo ParseManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string Get(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
        var sha = Get("sha256").Trim().ToLowerInvariant();
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit)) throw new InvalidOperationException("Manifest has no valid sha256.");
        var url = Get("url"); RequireSafe(url);
        return new UpdateInfo(ParseVersion(Get("version")), url, sha, Get("notes"));
    }

    public static async Task<UpdateInfo?> CheckAsync(string manifestUrl, CancellationToken ct = default)
    {
        var m = await FetchAsync(manifestUrl, ct);
        return IsNewer(m.Version, Current) ? m : null;
    }

    /// <summary>Downloads the installer to %TEMP% and verifies its SHA-256; deletes it and throws on mismatch.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        RequireSafe(info.Url);
        var dir = Path.Combine(Path.GetTempPath(), "EpikStreamer-update"); Directory.CreateDirectory(dir);
        var name = Path.GetFileName(new Uri(info.Url).LocalPath);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = $"EpikStreamer-{info.Version}-setup.exe";
        var path = Path.Combine(dir, name);
        using (var http = NewClient())
        using (var resp = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            var buf = new byte[81920]; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n; if (total > 0) progress?.Report((double)done / total); }
        }
        var actual = Sha256Of(path);
        if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException($"SHA-256 mismatch: expected {info.Sha256}, got {actual}. The download was deleted.");
        }
        return path;
    }

    public static string Sha256Of(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>Runs the (verified) Inno Setup installer; the caller should then exit the app.</summary>
    public static void RunInstaller(string path)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        foreach (var a in new[] { "/SILENT", "/CLOSEAPPLICATIONS", "/RESTARTAPPLICATIONS", "/NORESTART" }) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}
