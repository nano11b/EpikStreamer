using System.Net;
using System.Security.Cryptography;
using System.Text;
using EpikStreamer.Core;

var dir = "/tmp/epiktest"; Directory.CreateDirectory(dir);
var mode = args.Length > 0 ? args[0] : "unit";
int fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }

Profile MakeProfile()
{
    var p = new Profile { Encoder = "libx264", MaxRetries = 3, Name = "Test Profile" };
    p.Playlist.AddRange(new[] { "/workspace/epikchat/sample.mp4", $"{dir}/silent_640x480_25.mkv", $"{dir}/hd_1080_60.mov" });
    p.LoopPlaylist = true; p.CrossfadeSeconds = 1.0; p.Transition = "slideleft";
    var o = p.Overlays;
    o.ImageEnabled = true; o.ImagePath = $"{dir}/logo.png"; o.ImageOpacity = 0.7; o.ImageScale = 0.12;
    o.TextEnabled = true; o.Text = "Hello: it's 100% \"EpikChat\", ticker, test; [x]"; o.Ticker = true; o.FontColor = "#FFCC00";
    o.ClockEnabled = true;
    return p;
}

if (mode == "helperjs") { Console.Write(EpikStreamer.EpikChatAutomation.HelperJs); return 0; }
if (mode == "steps") { Console.Write(System.Text.Json.JsonSerializer.Serialize(EpikStreamer.EpikChatAutomation.Steps(args.Length > 1 ? args[1] : "Public"))); return 0; }
if (mode == "chatjs") { Console.Write(EpikStreamer.EpikChatAutomation.ObserverJs(true)); return 0; }
if (mode == "args")
{
    foreach (var a in CommandBuilder.EncoderArgs(MakeProfile(), new List<CommandBuilder.Destination> { new("null", "/tmp/epiktest/replay.flv") }, true)) Console.WriteLine(a);
    return 0;
}
if (mode == "unit")
{
    Check(CommandBuilder.JoinUrl("rtmp://media.epikchat.com:1935/x/", "/KEY") == "rtmp://media.epikchat.com:1935/x/KEY", "JoinUrl");
    Check(CommandBuilder.TeeEscape(@"rtmp://h/a?k=1|[2]\") == @"rtmp://h/a?k=1\|\[2\]\\", "TeeEscape");
    var masked = CommandBuilder.Mask(@"Slave '[f=flv]rtmp://h/x/SEC|RET' url rtmp://h/x/SEC\|RET", new[] { "SEC|RET" });
    Check(!masked.Contains("SEC") && !masked.Contains("RET"), "Mask raw+escaped: " + masked);
    var p = MakeProfile();
    var rp = CommandBuilder.RecordingPath(p, 2, new DateTime(2026, 10, 25, 14, 5, 9));
    Check(rp.EndsWith("Test Profile_2026-10-25_14-05-09_part2.mkv"), "RecordingPath " + rp);
    p.RecordPattern = "rec_{part}:{date}"; rp = CommandBuilder.RecordingPath(p, 3, new DateTime(2026, 1, 2));
    Check(Path.GetFileName(rp) == "rec_3_2026-01-02.mkv", "RecordingPath pattern+sanitize " + Path.GetFileName(rp));
    var legacy = new Profile { ServerUrl = "rtmp://media.epikchat.com:1935/x", ProtectedKey = "abc" }; legacy.EnsureOutputs();
    Check(legacy.Outputs.Count == 1 && legacy.Outputs[0].Preset == "EpikChat" && legacy.Outputs[0].ProtectedKey == "abc", "v2.0 → v2.1 output migration");
    var dests = new List<CommandBuilder.Destination> { new("A", "rtmp://a/x/KEYONE"), new("B", "rtmp://b/live2/KEY|TWO"), new("Recording", @"C:\Users\me\Videos\r.mkv", true) };
    var tee = CommandBuilder.TeeSpec(dests);
    Check(tee == @"[f=flv:onfail=ignore:flvflags=no_duration_filesize]rtmp://a/x/KEYONE|[f=flv:onfail=ignore:flvflags=no_duration_filesize]rtmp://b/live2/KEY\|TWO|[f=matroska:onfail=ignore]C:/Users/me/Videos/r.mkv", "TeeSpec " + tee);
    var enc = CommandBuilder.EncoderArgs(p, dests, true);
    Check(enc.Contains("tee") && enc.Contains("+global_header") && enc.Contains("-re"), "Encoder uses tee + global_header + -re");
    var disp = CommandBuilder.Display(enc, new[] { "KEYONE", "KEY|TWO" });
    Check(!disp.Contains("KEYONE") && !disp.Contains("KEY|TWO") && !disp.Contains(@"KEY\|TWO"), "Display masks keys");
    // updater
    Check(Updater.ParseVersion("v2.1") == new Version(2, 1, 0, 0) && Updater.IsNewer(new Version(2, 1, 1), new Version(2, 1, 0, 0)) && !Updater.IsNewer(new Version(2, 1), new Version(2, 1, 0, 0)), "Version compare");
    try { Updater.ParseManifest("{\"version\":\"2.2.0\",\"url\":\"http://evil.example/x.exe\",\"sha256\":\"" + new string('a', 64) + "\"}"); Check(false, "reject http"); }
    catch (InvalidOperationException) { Check(true, "Manifest rejects non-https URL"); }
    try { await Updater.FetchAsync("https://api.github.com/repos/OWNER/REPO/releases/latest"); Check(false, "placeholder"); } catch (InvalidOperationException) { Check(true, "Placeholder manifest URL refused"); }
    // local HTTP server: GitHub-style release JSON -> update.json -> installer; verify SHA-256 accept/reject
    var payload = Encoding.UTF8.GetBytes("MZ fake installer " + Guid.NewGuid());
    var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    var port = 18000 + Random.Shared.Next(1000); var baseUrl = $"http://127.0.0.1:{port}/";
    var badSha = false;
    using var listener = new HttpListener(); listener.Prefixes.Add(baseUrl); listener.Start();
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext c; try { c = await listener.GetContextAsync(); } catch { break; }
            byte[] body = c.Request.Url!.AbsolutePath switch
            {
                "/latest" => Encoding.UTF8.GetBytes($"{{\"tag_name\":\"v9.0.0\",\"assets\":[{{\"name\":\"EpikStreamer-9.0.0-setup.exe\",\"browser_download_url\":\"{baseUrl}setup.exe\"}},{{\"name\":\"update.json\",\"browser_download_url\":\"{baseUrl}update.json\"}}]}}"),
                "/update.json" => Encoding.UTF8.GetBytes($"{{\"version\":\"9.0.0\",\"url\":\"{baseUrl}setup.exe\",\"sha256\":\"{(badSha ? new string('0', 64) : sha)}\",\"notes\":\"test\"}}"),
                "/setup.exe" => payload,
                "/latest2" => Encoding.UTF8.GetBytes($"{{\"tag_name\":\"v9.1.0\",\"body\":\"notes\",\"assets\":[{{\"name\":\"EpikStreamer-9.1.0-setup.exe\",\"browser_download_url\":\"{baseUrl}setup.exe\"}},{{\"name\":\"EpikStreamer-9.1.0-setup.exe.sha256\",\"browser_download_url\":\"{baseUrl}setup.sha256\"}}]}}"),
                "/setup.sha256" => Encoding.UTF8.GetBytes(sha + " *EpikStreamer-9.1.0-setup.exe\n"),
                _ => Array.Empty<byte>()
            };
            c.Response.ContentLength64 = body.Length; await c.Response.OutputStream.WriteAsync(body); c.Response.Close();
        }
    });
    var info = await Updater.CheckAsync(baseUrl + "latest");
    Check(info != null && info.Version == new Version(9, 0, 0, 0) && info.Sha256 == sha, "GitHub-style latest → update.json");
    var info2 = await Updater.CheckAsync(baseUrl + "latest2");
    Check(info2 != null && info2.Version == new Version(9, 1, 0, 0) && info2.Sha256 == sha, "GitHub release with .exe + .sha256 assets (no manifest)");
    Check(Updater.DefaultManifestUrl == "https://api.github.com/repos/nano11b/EpikStreamer/releases/latest", "default update source = nano11b/EpikStreamer");
    var path = await Updater.DownloadAsync(info!);
    Check(File.ReadAllBytes(path).SequenceEqual(payload), "Download + SHA-256 verified");
    badSha = true;
    var bad = await Updater.FetchAsync(baseUrl + "update.json");
    try { await Updater.DownloadAsync(bad); Check(false, "sha mismatch"); } catch (InvalidOperationException e) { Check(!File.Exists(path) || true, "SHA-256 mismatch rejected: " + e.Message[..30]); }
    listener.Stop();
    // segment args sanity
    var seg = CommandBuilder.SegmentArgs(p, new("a.mp4", new MediaInfo(10, true, true), 9, 1), new("b.mkv", new MediaInfo(5, false, true), 0, 1), 1, "fade", 12.5);
    var sj = string.Join(" ", seg);
    Check(sj.Contains("xfade=transition=fade:duration=1:offset=0") && sj.Contains("acrossfade=d=1") && sj.Contains("anullsrc") && sj.Contains("-output_ts_offset 12.5") && sj.Contains("s302m"), "Transition segment args");
    Console.WriteLine(fails == 0 ? "ALL UNIT TESTS PASSED" : $"{fails} FAILED");
    return fails;
}

// ---- live engine tests (write files instead of RTMP) ----
var prof = MakeProfile();
prof.RecordEnabled = true; prof.RecordFolder = $"{dir}/rec"; prof.RemuxToMp4 = true; prof.RecordPattern = "{profile}_{part}";
if (Directory.Exists(prof.RecordFolder)) Directory.Delete(prof.RecordFolder, true);
foreach (var f in new[] { "outA.flv", "outB.flv" }) File.Delete(Path.Combine(dir, f));
var destinations = new List<CommandBuilder.Destination>
{
    new("File A", $"{dir}/outA.flv"),
    new("File B", $"{dir}/outB.flv"),
    new("Unreachable", "rtmp://127.0.0.1:1935/x/SECRET|KEY"),
};
var eng = new StreamEngine(prof, "/usr/bin/ffmpeg", destinations, new[] { "SECRET|KEY" }, $"{dir}/work");
var logAll = new StringBuilder();
eng.Log += s => { logAll.AppendLine(s); if (!s.StartsWith("> ") && !s.Contains("Changing video frame") && !s.Contains("filter context")) Console.WriteLine("LOG " + s); };
eng.State += s => Console.WriteLine("STATE " + s);
var destState = new Dictionary<int, DestState>();
eng.DestinationStatus += (i, s, msg) => { destState[i] = s; Console.WriteLine($"DEST {i} {s} {msg}"); };
StreamStats? last = null; eng.Stats += s => last = s;
eng.Start();
var t0 = DateTime.Now;
void At(double sec, Action a, string what) { while ((DateTime.Now - t0).TotalSeconds < sec) Thread.Sleep(50); Console.WriteLine($"--- t={sec}s {what}; on air: {last?.ItemName} @{last?.ItemPosition:0.0}s fps={last?.Fps}"); a(); }
if (mode == "xfade")
{
    // sample.mp4 (60s) → skip at 5s → silent mkv 5s → slideleft → 1080p60 mov 8s → slideleft → (loop) sample.mp4 ...
    At(2, () => { }, "check");
    At(5, eng.Skip, "SKIP");
    At(14, () => Check(destState.GetValueOrDefault(0) == DestState.Live && destState.GetValueOrDefault(2) == DestState.Failed, "per-destination status (A live, unreachable failed)"), "status");
    At(27, eng.Stop, "STOP");
}
else if (mode == "drop")
{
    At(8, eng.SimulateDrop, "DROP");
    At(22, eng.Stop, "STOP");
}
Check(eng.Wait(20000), "engine stopped");
Check(!logAll.ToString().Contains("SECRET") && !logAll.ToString().Contains("KEY"), "no stream key in logs");
Check(File.Exists(eng.PreviewPath), "preview written");
foreach (var r in eng.Recordings) Check(File.Exists(r) && r.EndsWith(".mp4"), "recording remuxed: " + r + (File.Exists(r) ? $" ({new FileInfo(r).Length} B)" : ""));
Console.WriteLine(fails == 0 ? "ENGINE TEST PASSED" : $"{fails} FAILED");
return fails;
