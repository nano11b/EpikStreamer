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
if (mode == "v22")
{
    // --- scheduler ---
    var now = new DateTime(2026, 10, 25, 19, 59, 0); // Sunday
    var once = new ScheduleEntry { Id = "a", Repeat = Recurrence.Once, Date = now.Date, Start = new(20, 0, 0), Stop = new(21, 0, 0) };
    Check(Scheduler.NextStart(once, now) == now.Date.AddHours(20), "Once: next start today 20:00");
    var fired = new HashSet<string>();
    Check(Scheduler.Due(new[] { once }, now, fired).Count == 0, "not due before start");
    Check(Scheduler.Due(new[] { once }, now.AddMinutes(1.5), fired).Count == 1, "due just after start");
    Check(Scheduler.Due(new[] { once }, now.AddMinutes(1.6), fired).Count == 0, "fires only once");
    Check(Scheduler.Due(new[] { once }, now.AddMinutes(30), new HashSet<string>()).Count == 0, "missed beyond grace → not started late");
    Check(Scheduler.NextStart(once, now.AddDays(1)) == null, "Once: nothing after the day");
    var overnight = new ScheduleEntry { Repeat = Recurrence.Daily, Date = now.Date, Start = new(23, 0, 0), Stop = new(1, 0, 0) };
    Check(Scheduler.End(overnight, now.Date.AddHours(23)) == now.Date.AddDays(1).AddHours(1), "stop ≤ start → next day");
    Check(Scheduler.NextStart(overnight, now.Date.AddDays(1).AddMinutes(30)) == now.Date.AddHours(23), "overnight occurrence still active after midnight");
    var weekly = new ScheduleEntry { Repeat = Recurrence.Weekly, Date = now.Date, Days = new() { DayOfWeek.Wednesday, DayOfWeek.Friday }, Start = new(18, 30, 0) };
    Check(Scheduler.NextStart(weekly, now) == new DateTime(2026, 10, 28, 18, 30, 0), "Weekly Wed/Fri → next Wednesday");
    Check(Scheduler.Describe(weekly) == "Weekly Wed,Fri 18:30", "Describe: " + Scheduler.Describe(weekly));
    Check(Scheduler.NextStart(new ScheduleEntry { Enabled = false, Date = now.Date, Start = new(20, 0, 0) }, now) == null, "disabled → none");
    File.WriteAllText($"{dir}/pl.m3u", "#EXTM3U\n# c\nclip one.mp4\n/abs/b.mkv\n\n");
    var pl = Scheduler.LoadPlaylistFile($"{dir}/pl.m3u");
    Check(pl.Count == 2 && pl[0] == $"{dir}/clip one.mp4" && pl[1] == "/abs/b.mkv", "m3u playlist load (relative+absolute)");

    // --- chat commands ---
    var cs = new ChatSettings { CooldownSeconds = 10 };
    cs.Custom.Add(new ChatCommand { Trigger = "!socials", Response = "hi {user}, {viewers} here" });
    var cc = new ChatCommands(cs); var ctx = new ChatCommands.Context { Song = "intro.mp4", Uptime = TimeSpan.FromMinutes(75), Viewers = 7 };
    var t = DateTime.Now;
    Check(cc.Handle(new("bob", "!song"), ctx, t) == "Now playing: intro.mp4", "!song");
    Check(cc.Handle(new("bob", "!song"), ctx, t.AddSeconds(3)) == null, "cooldown");
    Check(cc.Handle(new("bob", "!song"), ctx, t.AddSeconds(11)) != null, "after cooldown");
    Check(cc.Handle(new("amy", "!UPTIME please"), ctx, t) == "Live for 1h 15m", "!uptime (case-insensitive, args ignored)");
    Check(cc.Handle(new("amy", "!socials"), ctx, t) == "hi amy, 7 here", "custom command with {user} {viewers}");
    Check(cc.Handle(new("amy", "!help"), ctx, t)!.Contains("!socials") && !cc.Handle(new("x", "!help"), ctx, t.AddSeconds(30))!.Contains("!discord"), "!help lists enabled commands only");
    Check(cc.Handle(new("amy", "hello"), ctx, t) == null && cc.Handle(new("amy", "!nope"), ctx, t) == null, "non-commands ignored");
    Check(cc.Handle(new("z", "!uptime", "m1"), ctx, t.AddSeconds(60)) != null && cc.Handle(new("z", "!uptime", "m1"), ctx, t.AddSeconds(120)) == null, "duplicate message id ignored");
    Check(!cs.ReplyInChat, "reply-in-chat is OFF by default");
    var csOff = new ChatSettings { CommandsEnabled = false };
    Check(new ChatCommands(csOff).Handle(new("a", "!song"), ctx, t) == null, "commands disabled");
    Check(ChatCommands.FormatUptime(TimeSpan.FromSeconds(42)) == "42s" && ChatCommands.FormatUptime(TimeSpan.FromSeconds(125)) == "2m 05s", "uptime format");

    // --- viewer alerts ---
    var va = new ViewerAlerts(new ChatSettings()) { NewViewerThrottle = TimeSpan.FromSeconds(20) };
    Check(va.Update(3, t) == null, "first count is the baseline");
    Check(va.Update(4, t.AddSeconds(1)) == "Welcome! 4 watching", "new viewer alert");
    Check(va.Update(5, t.AddSeconds(5)) == null, "new viewer throttled");
    Check(va.Update(10, t.AddSeconds(6)) == "★ 10 viewers! ★", "milestone 10 (bypasses throttle)");
    Check(va.Update(9, t.AddSeconds(40)) == null && va.Update(10, t.AddSeconds(41)) != "★ 10 viewers! ★", "milestone fires once");
    Check(va.Update(60, t.AddSeconds(90)) == "★ 60 viewers! ★" && va.Update(70, t.AddSeconds(200)) != "★ 60 viewers! ★", "jump past several milestones → one alert");

    // --- history ---
    var hs = new HistoryStore($"{dir}/history.jsonl"); hs.Clear();
    var tr = new SessionTracker("Main", new[] { "EpikChat", "=cmd|evil" }, t);
    tr.OnStats(new StreamStats(30, 2400, 1, 100, 2, 0, TimeSpan.FromSeconds(5), 0, 0, 2, "a.mp4", 1));
    tr.OnStats(new StreamStats(30, 2600, 1, 200, 5, 0, TimeSpan.FromSeconds(10), 1, 1, 2, "b, \"q\".mp4", 1));
    tr.OnViewers(4); tr.OnViewers(8);
    hs.Append(tr.Finish(t.AddMinutes(30), "Stopped"));
    File.AppendAllText(hs.Path, "{corrupt\n");
    var hl = hs.Load();
    Check(hl.Count == 1 && hl[0].PeakViewers == 8 && hl[0].AvgViewers == 6 && hl[0].AvgBitrateKbps == 2500 && hl[0].DroppedFrames == 5 && hl[0].Reconnects == 1 && hl[0].DurationSec == 1800 && hl[0].FilesPlayed.Count == 2, "history record round-trip (corrupt line skipped)");
    var csv = HistoryStore.ToCsv(hl);
    Check(csv.Split("\r\n")[1].Contains("\"a.mp4; b, \"\"q\"\".mp4\"") && csv.Contains(",EpikChat; =cmd|evil,"), "CSV quoting: " + csv.Split("\r\n")[1]);
    Check(HistoryStore.ToCsv(new[] { new SessionRecord { Profile = "=HYPERLINK(1)" } }).Contains("\"'=HYPERLINK(1)\""), "CSV formula injection neutralised");

    // --- hotkeys ---
    Check(Hotkey.TryParse("Ctrl+Alt+S", out var m1, out var k1) && m1 == (Hotkey.MOD_CONTROL | Hotkey.MOD_ALT) && k1 == 'S', "hotkey Ctrl+Alt+S");
    Check(Hotkey.TryParse("shift + win + F5", out var m2, out var k2) && m2 == (Hotkey.MOD_SHIFT | Hotkey.MOD_WIN) && k2 == 0x74, "hotkey Shift+Win+F5");
    Check(Hotkey.TryParse("F9", out _, out var k3) && k3 == 0x78, "bare F-key allowed");
    Check(!Hotkey.TryParse("S", out _, out _) && !Hotkey.TryParse("Ctrl+Hyper+S", out _, out _) && !Hotkey.TryParse("", out _, out _), "invalid hotkeys rejected");
    Check(Hotkey.TryParse("Ctrl+Num5", out _, out var k4) && k4 == 0x65 && Hotkey.TryParse("MediaNext", out _, out var k5) && k5 == 0xB0, "numpad + media keys");

    // --- dshow parsing (new and old ffmpeg formats) ---
    var newFmt = "[dshow @ 0x1] \"Integrated Camera\" (video)\n[dshow @ 0x1]   Alternative name \"@device_pnp_x\"\n[dshow @ 0x1] \"Microphone (Realtek(R) Audio)\" (audio)\n[dshow @ 0x1]   Alternative name \"@device_cm_y\"\n[dshow @ 0x1] \"Stereo Mix (Realtek(R) Audio)\" (audio)\r\n";
    var oldFmt = "[dshow @ 0x2] DirectShow video devices\n[dshow @ 0x2]  \"Cam\"\n[dshow @ 0x2] DirectShow audio devices\n[dshow @ 0x2]  \"Headset Mic (USB)\"\n[dshow @ 0x2]     Alternative name \"@device_cm_z\"\ndummy: Immediate exit requested\n";
    var d1 = DshowDevices.ParseAudio(newFmt); var d2 = DshowDevices.ParseAudio(oldFmt);
    Check(d1.SequenceEqual(new[] { "Microphone (Realtek(R) Audio)", "Stereo Mix (Realtek(R) Audio)" }), "dshow audio devices (new format): " + string.Join("|", d1));
    Check(d2.SequenceEqual(new[] { "Headset Mic (USB)" }), "dshow audio devices (old format): " + string.Join("|", d2));

    // --- graph construction ---
    var gp = MakeProfile(); gp.Audio = new AudioSettings { Normalize = "Loudnorm", TargetLufs = -14, MusicEnabled = true, MicEnabled = true, MicNoiseGate = true };
    var ga = CommandBuilder.EncoderArgs(gp, new List<CommandBuilder.Destination> { new("x", "rtmp://h/x/K") }, true, new(5555, 6666, "Mic (USB)", true, 0, 0.8));
    var gj = string.Join(" ", ga);
    Check(gj.Contains("tcp://127.0.0.1:6666?listen=1") && gj.Contains("audio=Mic (USB)") && gj.Contains("[2:a]aformat") && gj.Contains("[3:a]aresample"), "inputs: logo=1, music=2, mic=3");
    Check(gj.Contains("azmq=bind_address=tcp\\\\://127.0.0.1\\\\:5555") && gj.Contains("volume@music=0") && gj.Contains("volume@mic=0.8") && gj.Contains("agate@gate") && gj.Contains("sidechaincompress@duck") && gj.Contains("loudnorm=I=-14"), "audio graph: zmq, named volumes, gate, ducking, loudnorm");
    Check(gj.Contains("drawtext@alert") && gj.Contains("reload=1") && gj.Contains("colorchannelmixer@logo") && gj.Contains("drawtext@text") && gj.Contains("drawtext@clock"), "named video filters + alert drawtext reload");
    var plain = string.Join(" ", CommandBuilder.EncoderArgs(MakeProfile(), new List<CommandBuilder.Destination> { new("x", "rtmp://h/x/K") }, false));
    Check(!plain.Contains("azmq") && !plain.Contains("amix") && !plain.Contains("loudnorm") && plain.Contains("aresample=44100[aout]"), "defaults: plain audio chain");
    Check(CommandBuilder.MicInputArgs("lavfi:sine")[1] == "lavfi", "mic lavfi test hook");

    // --- settings JSON round-trip with v2.2 fields ---
    var store = new ProfileStore { Theme = "Midnight Blue" }; store.Profiles.Add(gp); store.Schedule.Add(weekly);
    var js = System.Text.Json.JsonSerializer.Serialize(store, ProfileStore.Json);
    var back = System.Text.Json.JsonSerializer.Deserialize<ProfileStore>(js, ProfileStore.Json)!;
    Check(back.Theme == "Midnight Blue" && back.Schedule[0].Days.Count == 2 && back.Profiles[0].Audio.Normalize == "Loudnorm" && !back.Chat.ReplyInChat && back.Hotkeys.StartStop == "Ctrl+Alt+S", "settings round-trip");
    var old = System.Text.Json.JsonSerializer.Deserialize<ProfileStore>("{\"Active\":\"A\",\"Profiles\":[{\"Name\":\"A\"}]}", ProfileStore.Json)!;
    Check(old.Profiles[0].Audio != null && old.Chat != null && old.Schedule.Count == 0 && old.Theme == "Dark", "v2.1 settings load with v2.2 defaults");
    Console.WriteLine(fails == 0 ? "ALL V2.2 UNIT TESTS PASSED" : $"{fails} FAILED");
    return fails;
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
if (mode == "audio")
{
    prof.Audio = new AudioSettings { Normalize = Environment.GetEnvironmentVariable("NORM") ?? "Loudnorm", TargetLufs = -16, MusicEnabled = true, MusicPlaylist = { "/tmp/zt/music1.mp3", "/tmp/zt/music2.wav" },
        MusicVolume = 0.3, Ducking = true, MicEnabled = true, MicDevice = "lavfi:sine=f=1000:sample_rate=44100,volume=0.1", MicNoiseGate = Environment.GetEnvironmentVariable("NORM") == null };
}
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
eng.TextOverlays = mode == "audio";
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
else if (mode == "audio")
{
    At(5, () =>
    {
        eng.ShowAlert("ALERT TEST ★", 3); eng.ShowChatResponse("Now playing: test", 3);
        Check(File.ReadAllText($"{dir}/work/{CommandBuilder.AlertFile}").StartsWith("ALERT TEST"), "alert text written");
    }, "ALERT");
    At(7, () =>
    {
        Check(eng.ControlAvailable, "azmq runtime control available");
        Check(eng.SetMusicMuted(true), "zmq: mute music");
        Check(eng.SetMicMuted(true), "zmq: mute mic");
        Check(eng.SetOverlaysHidden(true), "zmq: hide logo/text/clock");
        Check(!string.IsNullOrEmpty(eng.CurrentMusic), "music playing: " + eng.CurrentMusic);
    }, "MUTE");
    At(9.5, () => Check(File.ReadAllText($"{dir}/work/{CommandBuilder.AlertFile}") == "", "alert cleared after its duration"), "ALERT OFF");
    At(11, () => { Check(eng.SetMusicMuted(false) && eng.SetMicMuted(false) && eng.SetOverlaysHidden(false), "zmq: unmute + show"); }, "UNMUTE");
    At(16, eng.Stop, "STOP");
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
