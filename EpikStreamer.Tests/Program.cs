using System.Net;
using System.Security.Cryptography;
using System.Text;
using EpikStreamer.Core;

var dir = "/tmp/epiktest"; Directory.CreateDirectory(dir);
var mode = args.Length > 0 ? args[0] : "unit";
int fails = 0;
if (mode is "unit" or "v22") Os.Windows = true;   // these modes assert the Windows (dshow / setup.exe) code paths; Linux paths are checked separately below
var FF = Environment.GetEnvironmentVariable("FFMPEG") ?? "/usr/bin/ffmpeg";
if (mode == "ballpit") return BallPitTests.Unit();
if (mode == "radio") return RadioTests.Unit();
if (mode == "feed") return FeedTests.Run();
if (mode == "pitonly") return PitOnlyTests.Unit();
if (mode == "sky") return SkyTests.Run(args.Length > 1 ? args[1] : "");
if (mode == "pitlimits") return PitLimitTests.Run();
if (mode == "pitbench") return PitPerfTests.Bench(args.Length > 1 ? args[1] : "");
if (mode == "pitburst") return PitBurstTests.Run(FF, dir, Environment.GetEnvironmentVariable("OUT") ?? $"{dir}/pit-burst.mp4");
if (mode == "layout") return ChatLayoutTests.Run();
if (mode == "pitonly-encode") return PitOnlyTests.Encode(FF, dir, Environment.GetEnvironmentVariable("OUT") ?? $"{dir}/pit-radio-only.mp4", Environment.GetEnvironmentVariable("RADIO") != "0", double.Parse(Environment.GetEnvironmentVariable("SECS") ?? "30", System.Globalization.CultureInfo.InvariantCulture));
if (mode == "engage") return EngageTests.Run();
if (mode == "engagereplay") return EngageReplay.Run(args[1], args[2]);
if (mode == "feedreplay") return FeedReplay.Run(args[1], args[2], args.Length > 3 ? args[3] : "");
if (mode == "radio-live") return RadioTests.Live(FF, dir, Environment.GetEnvironmentVariable("RADIO_URL") ?? "https://radio.seven-labs.com/public/labsfm/playlist.pls", Environment.GetEnvironmentVariable("OUT") ?? dir);
if (mode == "pace") return PaceTests.Run(FF, dir, Environment.GetEnvironmentVariable("PITMODE") ?? "Source", Environment.GetEnvironmentVariable("CARD") == "1", double.Parse(Environment.GetEnvironmentVariable("SECS") ?? "90", System.Globalization.CultureInfo.InvariantCulture), Environment.GetEnvironmentVariable("ENCODER") ?? "libx264", Environment.GetEnvironmentVariable("OUT") ?? $"{dir}/pace.mp4");
if (mode == "radio-encode") return RadioTests.Encode(FF, dir, Environment.GetEnvironmentVariable("OUT") ?? $"{dir}/radio.mp4", Environment.GetEnvironmentVariable("RADIO_URL") ?? "mock",
    double.Parse(Environment.GetEnvironmentVariable("SECS") ?? "15", System.Globalization.CultureInfo.InvariantCulture), Environment.GetEnvironmentVariable("DROP") ?? "",
    double.Parse(Environment.GetEnvironmentVariable("DROP_AT") ?? "8", System.Globalization.CultureInfo.InvariantCulture), double.Parse(Environment.GetEnvironmentVariable("DROP_FOR") ?? "8", System.Globalization.CultureInfo.InvariantCulture));
if (mode == "ballpit-bench") return BallPitTests.Bench(Environment.GetEnvironmentVariable("OUT") ?? "/workspace/rel/v2.5.0");
if (mode == "engage-encode") return EngageEncode.Run(FF, dir, Environment.GetEnvironmentVariable("OUT") ?? $"{dir}/engage.mp4", Environment.GetEnvironmentVariable("PITMODE") ?? "Source", double.Parse(Environment.GetEnvironmentVariable("SECS") ?? "60", System.Globalization.CultureInfo.InvariantCulture));
if (mode == "ballpit-encode") return BallPitTests.Encode(FF, dir, Environment.GetEnvironmentVariable("OUT") ?? $"{dir}/ballpit.mp4", Environment.GetEnvironmentVariable("PITMODE") ?? "Source");
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

if (mode == "caps")
{
    var c = FfmpegCaps.Probe(FF);
    Console.WriteLine("caps: " + c.Describe());
    Check(c.Version.StartsWith("ffmpeg version"), "probe reads version");
    Check(ReferenceEquals(c, FfmpegCaps.Probe(FF)), "probe is cached per binary");
    Check(c.HasFilter("drawtext") && c.HasFilter("xfade") && c.HasEncoder("libx264"), "filters/encoders parsed");
    if (Environment.GetEnvironmentVariable("EXPECT_NO_TQ") == "1") Check(!c.InputThreadQueue, "master: input -thread_queue_size detected as unsupported");
    var pr = MakeProfile();
    var args0 = CommandBuilder.EncoderArgs(pr, new List<CommandBuilder.Destination> { new("x", "rtmp://h/x/K") }, true, new(5555, 6666, "lavfi:sine", true), c);
    Check(c.InputThreadQueue == args0.Contains("-thread_queue_size"), "-thread_queue_size emitted only when accepted");
    int iPipe = args0.IndexOf("pipe:0");
    Check(args0.IndexOf("-progress") < iPipe && args0[iPipe - 1] == "-i" && args0[iPipe - 2] == "mpegts" && args0[iPipe - 4] == "-re", "pipe input options directly precede -i pipe:0");
    var amf = new Profile { Encoder = "h264_amf" };
    var fakeAmf = new FfmpegCaps { EncoderOptions = new() { ["h264_amf"] = new() { "usage", "rc" } } };
    var va = string.Join(" ", CommandBuilder.VideoEncoderArgs(amf, fakeAmf));
    Check(va.Contains("-usage lowlatency") && va.Contains("-rc cbr") && !va.Contains("-quality"), "unknown encoder-private option dropped: " + va);
    var noFlv = new FfmpegCaps { FlvFlags = false, AtomicWriting = false, StatsPeriod = false, ReinitFilter = false, InputThreadQueue = false, Filters = new() { "drawtext" } };
    var na = string.Join(" ", CommandBuilder.EncoderArgs(pr, new List<CommandBuilder.Destination> { new("x", "rtmp://h/x/K"), new("r", "/tmp/r.mkv", true) }, true, new(5555, 6666, null, true), noFlv));
    Check(!na.Contains("flvflags") && !na.Contains("atomic_writing") && !na.Contains("stats_period") && !na.Contains("reinit_filter") && !na.Contains("azmq") && !na.Contains("thread_queue"), "gated options/filters omitted");
    // the user's exact lines
    var userErr = "Option thread_queue_size (set the maximum number of queued packets per stream on the muxer) cannot be applied to input url pipe:0 -- you are trying to apply an input option to an output file or vice versa. Move this option before the file it belongs to.";
    Check(StreamEngine.OptionError.IsMatch(userErr) && StreamEngine.OptionError.IsMatch("Error parsing options for input file pipe:0.") && StreamEngine.OptionError.IsMatch("Error opening input files: Invalid argument"), "option errors recognised");
    Check(!StreamEngine.OptionError.IsMatch("[in#0 @ 0000011a9e30f440] UDTA parsing failed retrying raw") && !StreamEngine.OptionError.IsMatch("stream 0, timescale not set")
          && !StreamEngine.OptionError.IsMatch("[tcp @ 0x1] Connection to tcp://h:1935 failed: Connection refused") && !StreamEngine.OptionError.IsMatch("Error opening output rtmp://h/x/K: Input/output error"), "harmless/network lines not fatal");
    Check(StreamEngine.IsFatalOptionError(-22, false, userErr) && !StreamEngine.IsFatalOptionError(-22, true, userErr) && !StreamEngine.IsFatalOptionError(1, false, null), "fatal only before progress");
    // auto-detect test encode (FfmpegLocator) works with this build
    Check((await FfmpegLocator.DetectEncodersAsync(FF)).Contains("libx264"), "encoder auto-detect test-encode runs");
    Console.WriteLine(fails == 0 ? "CAPS TESTS PASSED" : $"{fails} FAILED");
    return fails;
}
if (mode == "helperjs") { Console.Write(EpikStreamer.EpikChatAutomation.HelperJs); return 0; }
if (mode == "steps") { Console.Write(System.Text.Json.JsonSerializer.Serialize(EpikStreamer.EpikChatAutomation.Steps(args.Length > 1 ? args[1] : "Public"))); return 0; }
if (mode == "chatjs") { Console.Write(EpikStreamer.EpikChatAutomation.ChatObserverJs(true, "")); return 0; }
if (mode == "chatfeed")
{   // feeds the messages the JS observer posted in the headless mock (run-chat-mock.sh) through the real command handler
    var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args.Length > 1 ? args[1] : "/tmp/chat-posted.json"));
    var h = new ChatCommands(new ChatSettings { CooldownSeconds = 0 }); var cx = new ChatCommands.Context { Song = "Test Song", Uptime = TimeSpan.FromMinutes(5), Viewers = 3 };
    int n = 0; var now = DateTime.Now;
    foreach (var e in doc.RootElement.EnumerateArray())
    {
        var msg = new ChatMessage(e.GetProperty("user").GetString()!, e.GetProperty("text").GetString()!, e.GetProperty("id").GetString(), e.GetProperty("isBot").GetBoolean(), e.GetProperty("self").GetBoolean());
        for (int rep = 0; rep < 2; rep++) { var r = h.Handle(msg, cx, now = now.AddSeconds(1)); if (r != null) { n++; Console.WriteLine($"{msg.User}: {msg.Text} -> {r}"); } }
    }
    Console.WriteLine(n == 4 ? "CHATFEED PASSED (4 replies, each once)" : $"CHATFEED FAILED ({n} replies)"); return n == 4 ? 0 : 1;
}
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
    Check(cc.Handle(new("EpikBot", "!uptime", "b1", IsBot: true), ctx, t.AddSeconds(500)) == null, "bot messages ignored");
    Check(cc.Handle(new("me", "!uptime", "s1", Self: true), ctx, t.AddSeconds(520)) == null, "own (self-flagged) messages ignored");
    var csMe = new ChatSettings { MyUsername = "MeStreamer" };
    Check(new ChatCommands(csMe).Handle(new("mestreamer ", "!song"), ctx, t) == null && new ChatCommands(csMe).Handle(new("alice", "!song"), ctx, t) != null, "configured username ignored (case-insensitive)");
    Check(new ChatSettings().ReplyMinIntervalSeconds >= 1 && !new ChatSettings().DebugLog, "reply rate-limit default, debug off by default");
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
if (mode == "cardargs") { var cp0 = MakeProfile(); cp0.NowPlaying.Enabled = true; Console.WriteLine(string.Join("\n", NowPlayingCard.RenderArgs(cp0, new("T", "amy", "N", 30, 120, "/tmp/x.jpg"), "o.png", true))); return 0; }
if (mode == "v23")
{
    // --- fuzzy matching ---
    var lib = new List<LibraryItem>
    {
        new() { Path = "/m/Daft Punk - Harder Better Faster Stronger.mp4", Title = "Harder Better Faster Stronger", Artist = "Daft Punk", Duration = 224 },
        new() { Path = "/m/rick_astley_never_gonna_give_you_up (Official Video).mp4", Title = LibraryItem.TitleFromFile("/m/rick_astley_never_gonna_give_you_up (Official Video).mp4"), Duration = 213 },
        new() { Path = "/m/Beyoncé - Halo.mkv", Title = "Halo", Artist = "Beyoncé", Duration = 261 },
        new() { Path = "/m/long_mix.mkv", Title = "Two Hour Mix", Duration = 7200 },
        new() { Path = "/m/intro.mp4", Title = "intro", Duration = 30 },
    };
    Check(lib[1].Title == "rick astley never gonna give you up", "title from file name (tags like (Official Video) stripped): " + lib[1].Title);
    Check(Fuzzy.Rank("never gonna give", lib)[0].Item == lib[1], "fuzzy: substring");
    Check(Fuzzy.Rank("harder beter faster", lib)[0].Item == lib[0], "fuzzy: typo");
    Check(Fuzzy.Rank("beyonce halo", lib)[0].Item == lib[2], "fuzzy: accents + artist");
    Check(Fuzzy.Rank("daft", lib)[0].Item == lib[0], "fuzzy: prefix/artist");
    Check(Fuzzy.Rank("zzzz qqqq", lib).Count == 0 || Fuzzy.Rank("zzzz qqqq", lib)[0].Score < 0.55, "fuzzy: nonsense doesn't match");
    // --- requests ---
    var rs = new RequestSettings { PerUserLimit = 2, UserCooldownSeconds = 30, MaxQueue = 3, BlockedUsers = "troll, @spam", SkipVotes = 2 };
    var srq = new SongRequestQueue(rs); var t = new DateTime(2026, 10, 25, 20, 0, 0);
    string R(string u, string text, double sec = 0, string cur = "/m/intro.mp4", int viewers = 10) => srq.Handle(new(u, text), lib, cur, "track1", viewers, t.AddSeconds(sec))?.Reply ?? "(null)";
    Check(R("amy", "!sr never gonna").Contains("added") && srq.Count == 1, "!sr adds: " + R("x", "!queue"));
    Check(R("amy", "!request halo", 5).Contains("wait"), "per-user cooldown");
    Check(R("amy", "!request halo", 31).Contains("added") && R("amy", "!request daft", 62).Contains("already have 2"), "per-user limit");
    Check(R("bob", "!request never gonna give you up").Contains("already in the queue"), "duplicate rejected");
    Check(R("bob", "!request intro").Contains("playing right now"), "currently playing rejected");
    Check(R("troll", "!request halo").Contains("aren't available") && R("Spam", "!sr halo").Contains("aren't available"), "blocked users");
    Check(R("bob", "!sr xqzv blorp").Contains("no match"), "no match message");
    Check(R("bob", "!sr daft punk").Contains("added") && R("cat", "!sr two hour").Contains("queue is full"), "max queue");
    Check(R("x", "!queue").StartsWith("Queue: 1. rick astley"), "!queue lists");
    rs.AllowedUsers = "vip1"; Check(R("bob2", "!sr halo", 100).Contains("limited"), "allow-list"); rs.AllowedUsers = "";
    rs.MaxDurationMinutes = 60; srq.Clear(); Check(R("dan", "!sr two hour mix", 200).Contains("longer than"), "max duration"); rs.MaxDurationMinutes = 0;
    // engine side: placement + approval
    srq.Clear(); R("eve", "!sr halo", 300);
    Check(srq.TakeNext(false)?.Path == "/m/Beyoncé - Halo.mkv", "Placement=Next: taken at any boundary");
    rs.Placement = "End"; R("eve2", "!sr halo", 400);
    Check(srq.TakeNext(false) == null && srq.TakeNext(true) != null, "Placement=End: only at the end of a pass"); rs.Placement = "Next";
    rs.RequireApproval = true; Check(R("fay", "!sr daft", 500).Contains("approval") && srq.TakeNext(false) == null, "approval required: pending not played");
    var pend = srq.Snapshot()[0]; Check(srq.Approve(pend.Id) && srq.TakeNext(false)?.Id == pend.Id, "approved → played"); rs.RequireApproval = false;
    R("g1", "!sr halo", 600); R("g2", "!sr daft", 600); var snap = srq.Snapshot();
    Check(srq.Move(snap[1].Id, -1) && srq.Snapshot()[0].User == "g2" && srq.Remove(snap[0].Id) && srq.Count == 1, "host reorder/deny");
    // vote skip + wrongsong
    Check(srq.VoteSkip("a", "k1", 10).Skip == false && srq.VoteSkip("a", "k1", 10).Reply!.Contains("already voted") && srq.VoteSkip("b", "k1", 10).Skip, "vote skip: N votes, one per user");
    Check(srq.VoteSkip("a", "k2", 10).Skip == false, "votes reset on a new track");
    rs.SkipPercent = 30; Check(srq.SkipThreshold(10) == 3 && srq.SkipThreshold(1) == 1 && srq.SkipThreshold(0) == 2, "vote skip: % of viewers"); rs.SkipPercent = 0;
    Check(R("g2", "!wrongsong", 700).Contains("removed") && srq.Count == 0 && R("g2", "!wrongsong", 700).Contains("no queued"), "!wrongsong removes own request");
    var sr = srq.Add(lib[2], "host"); srq.TakeNext(false); srq.Scheduled(sr); srq.OnAir(sr.Path);
    Check(srq.Playing?.User == "host" && srq.WrongSong("host").Skip, "!wrongsong while on air skips it");
    Check(srq.Handle(new("bot", "!sr halo", null, IsBot: true), lib, "", "k", 1, t) == null && srq.Handle(new("me", "!sr halo", null, false, true), lib, "", "k", 1, t) == null, "bots/self can't request");
    rs.Enabled = false; Check(srq.Handle(new("a", "!sr halo"), lib, "", "k", 1, t) == null, "requests disabled"); rs.Enabled = true;
    // --- viewer tracker ---
    var vt = new ViewerTracker(new ViewerSettings { LeaveGraceSeconds = 20 }) { Self = "Me" }; var ev = new List<string>();
    vt.Joined += v => ev.Add("+" + v.Name); vt.Left += v => ev.Add("-" + v.Name);
    vt.Update("LiveViewers", new[] { "amy", "Bob", "Me" }, t);
    vt.Update("RoomMembers", new[] { "bob", "cat" }, t.AddSeconds(1));
    vt.Update("LiveViewers", new[] { "amy" }, t.AddSeconds(5));           // bob still in RoomMembers
    vt.Update("RoomMembers", new[] { "cat" }, t.AddSeconds(10));          // bob gone from both
    vt.Tick(t.AddSeconds(15)); Check(!ev.Contains("-bob"), "leave grace period");
    vt.Tick(t.AddSeconds(31)); Check(ev.Contains("-Bob") && string.Join(",", ev.Where(e => e[0] == '+')) == "+amy,+Bob,+cat", "joins/leaves (self excluded, case-insensitive): " + string.Join(",", ev));
    vt.Update("LiveViewers", new[] { "amy", "bob" }, t.AddSeconds(60));
    var bob = vt.Snapshot().First(v => v.Name == "Bob");
    Check(bob.Visits == 2 && bob.Total == TimeSpan.FromSeconds(5) && vt.PresentCount == 3, $"rejoin: visits {bob.Visits}, total {bob.Total}");
    // --- library scan (real ffprobe/ffmpeg) ---
    var ldir = $"{dir}/lib"; if (Directory.Exists(ldir)) Directory.Delete(ldir, true); Directory.CreateDirectory(ldir + "/sub");
    File.Copy("/workspace/epikchat/sample.mp4", ldir + "/sample.mp4"); File.Copy($"{dir}/hd_1080_60.mov", ldir + "/sub/HD Clip (Official Video).mov");
    File.WriteAllText(ldir + "/broken.mp4", "not a video"); File.WriteAllText(ldir + "/notes.txt", "x");
    var store = new LibraryStore { Dir = $"{dir}/libstore" }; if (Directory.Exists(store.Dir)) Directory.Delete(store.Dir, true);
    store.Settings.Folders.Add(ldir);
    var probe = FfmpegLocator.FindProbe(FF)!;
    var res = await LibraryScanner.ScanAsync(store, probe, FF, null, CancellationToken.None);
    var items = store.Snapshot();
    Check(res.total == 3 && res.failed == 1 && items.All(i => !i.Path.EndsWith(".txt")), $"scan: {res}");
    var hd = items.First(i => i.Path.EndsWith(".mov"));
    Check(hd.Height == 1080 && hd.Duration > 7 && hd.VideoCodec.Length > 0 && hd.Title == "HD Clip", $"probe: {hd.Title} {hd.Width}x{hd.Height} {hd.VideoCodec}/{hd.AudioCodec} {hd.DurationText}");
    Check(File.Exists(hd.Thumb), "thumbnail cached: " + hd.Thumb);
    var reload = LibraryStore.Load(store.Dir); Check(reload.Items.Count == 3, "library.json persisted");
    var sw = System.Diagnostics.Stopwatch.StartNew(); res = await LibraryScanner.ScanAsync(reload, probe, FF, null, CancellationToken.None);
    Check(res.probed == 0 && res.failed == 1, $"rescan uses the cache ({sw.ElapsedMilliseconds} ms)");
    Check(LibraryStore.Query(items, "hd clip", "Title", "All").Count() == 1 && LibraryStore.Query(items, "", "Duration", "HD (720p+)").Count() == 2 && LibraryStore.Query(items, "", "Title", "Missing metadata").Single().Path.EndsWith("broken.mp4")
          && LibraryStore.Query(items, "", "Duration", "All").First().Duration <= 0, "search/sort/filter");
    // --- now-playing card render ---
    var cp = MakeProfile(); cp.NowPlaying.Enabled = true; var wd = $"{dir}/npwork"; Directory.CreateDirectory(wd);
    File.Copy(CommandBuilder.DefaultFont(), Path.Combine(wd, CommandBuilder.FontName), true);
    var info = new NowPlayingInfo("A Very Long Song Title That Needs Clipping Because It Goes On", "amy", "Next Song", 30, 120, hd.Thumb);
    Check(NowPlayingCard.Render(FF, wd, cp, info), "card rendered " + NowPlayingCard.LastError);
    var (cw, ch) = NowPlayingCard.Size(cp);
    var px = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(FF, $"-v error -i {wd}/np_card.png -f rawvideo -pix_fmt rgba -") { RedirectStandardOutput = true })!;
    var ms = new MemoryStream(); px.StandardOutput.BaseStream.CopyTo(ms); px.WaitForExit(); var raw = ms.ToArray();
    int Px(int x, int y) => (y * cw + x) * 4;
    int barY = ch - 2; int a1 = Px(cw / 8, barY), a2 = Px(cw * 7 / 8, barY);
    Check(raw.Length == cw * ch * 4, $"card size {cw}x{ch}");
    Check(raw[a1] > 130 && raw[a1 + 1] < 120 && raw[a1 + 2] > 200 && raw[a2] < 100 && raw[a2 + 2] < 100, $"progress bar at 25% in the accent colour ({raw[a1]},{raw[a1 + 1]},{raw[a1 + 2]} / {raw[a2]},{raw[a2 + 1]},{raw[a2 + 2]})");
    Check(raw[Px(cw / 2, ch / 2) + 3] > 150, "card background opaque-ish");
    Check(File.ReadAllText(Path.Combine(wd, NowPlayingCard.SubFile)) == "Requested by amy" && File.ReadAllText(Path.Combine(wd, NowPlayingCard.TimeFile)) == "0:30 / 2:00", "card texts");
    Check(NowPlayingCard.Render(FF, wd, cp, null), "blank card");
    px = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(FF, $"-v error -i {wd}/np_card.png -f rawvideo -pix_fmt rgba -") { RedirectStandardOutput = true })!;
    ms = new MemoryStream(); px.StandardOutput.BaseStream.CopyTo(ms); px.WaitForExit(); raw = ms.ToArray();
    Check(raw.Where((b, i) => i % 4 == 3).All(b => b == 0), "hidden card fully transparent");
    var ea = string.Join(" ", CommandBuilder.EncoderArgs(cp, new List<CommandBuilder.Destination> { new("x", "rtmp://h/x/K") }, false, new(Card: true)));
    Check(ea.Contains("-re -f image2 -loop 1 -framerate 5 -i np_card.png") && ea.Contains("[1:v]format=rgba[npc]") && ea.Contains("overlay@np="), "encoder card input + overlay");
    // session history viewer fields
    var vt2 = new ViewerTracker(new ViewerSettings()); var t2 = new DateTime(2026, 1, 1, 12, 0, 0);
    vt2.Update("LiveViewers", new[] { "amy", "bob" }, t2); vt2.Update("LiveViewers", new[] { "amy" }, t2.AddMinutes(10)); vt2.Tick(t2.AddMinutes(30));
    var st2 = new SessionTracker("P", new[] { "EpikChat" }, t2); st2.SetViewers(vt2.Snapshot(), t2.AddMinutes(30));
    var rec2 = st2.Finish(t2.AddMinutes(30), "stopped");
    Check(rec2.UniqueViewers == 2 && rec2.TopViewers.Count == 2 && rec2.TopViewers[0].StartsWith("amy (30 min)"), "session unique/top viewers: " + string.Join(",", rec2.TopViewers));
    var csv2 = HistoryStore.ToCsv(new[] { rec2 }).Split("\r\n");
    Check(csv2[0].EndsWith(",unique_viewers") && csv2[1].EndsWith(",2"), "csv unique_viewers column");
    Console.WriteLine(fails == 0 ? "ALL V2.3 TESTS PASSED" : $"{fails} FAILED");
    return fails;
}

if (mode == "unit")
{
    {   // v2.4.1
        var m = System.Text.Json.Nodes.JsonNode.Parse(EpikStreamer.EpikChatAutomation.MergeGoLiveDiag("{\"observer\":{\"attached\":true}}", "{\"state\":{\"live\":false},\"selectors\":{\"verified\":false}}"))!;
        Check(m["observer"]!["attached"]!.GetValue<bool>() && m["goLive"]!["selectors"]!["verified"]!.GetValue<bool>() == false, "MergeGoLiveDiag keeps diag + adds goLive");
        Check(System.Text.Json.Nodes.JsonNode.Parse(EpikStreamer.EpikChatAutomation.MergeGoLiveDiag(null, "not json"))!["goLive"]!.GetValue<string>() == "not json", "MergeGoLiveDiag tolerates bad input");
        Check(new ChatSettings().AutoGoLive && new ChatSettings().GoLiveTimeoutSeconds == 60, "AutoGoLive defaults ON, 60 s timeout");
        Check(EpikStreamer.EpikChatAutomation.HelperJs.Contains("finalize") && EpikStreamer.EpikChatAutomation.HelperJs.Contains("goLiveDiag"), "helper has go-live steps");
    }
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
    // --- v2.4 Linux targets (same Core, Os.Windows = false) ---
    Os.Windows = false;
    var lm = string.Join(" ", CommandBuilder.MicInputArgs("alsa_input.usb-Mic-00.analog-stereo"));
    Check(lm.Contains("-f pulse") && lm.Contains("-i alsa_input.usb-Mic-00.analog-stereo") && !lm.Contains("dshow"), "Linux mic → -f pulse <source>: " + lm);
    Check(string.Join(" ", CommandBuilder.MicInputArgs("alsa:hw:1,0")).Contains("-f alsa"), "Linux mic alsa: prefix → -f alsa");
    Check(FfmpegLocator.Encoders.Any(e => e.Id == "h264_vaapi") && !FfmpegLocator.Encoders.Any(e => e.Id == "h264_amf"), "Linux encoder list has VA-API, no AMF");
    var vp = MakeProfile(); vp.Encoder = "h264_vaapi";
    var vj = string.Join(" ", CommandBuilder.EncoderArgs(vp, new List<CommandBuilder.Destination> { new("x", "rtmp://h/x/K") }, false, null));
    Check(vj.Contains("-init_hw_device vaapi=va:") && vj.Contains("-filter_hw_device va") && vj.Contains("format=nv12,hwupload") && vj.Contains("-c:v h264_vaapi"), "VA-API: hw device + nv12/hwupload + h264_vaapi");
    Check(vj.IndexOf("-init_hw_device", StringComparison.Ordinal) < vj.IndexOf("-i pipe:0", StringComparison.Ordinal), "VA-API: device init before inputs");
    var lman = "{\"version\":\"9.2.0\",\"url\":\"https://github.com/nano11b/EpikStreamer/releases/download/v9.2.0/EpikStreamer-9.2.0-setup.exe\",\"sha256\":\"" + new string('a', 64) + "\",\"notes\":\"n\"," +
               "\"linux\":{\"amd64\":{\"url\":\"https://github.com/nano11b/EpikStreamer/releases/download/v9.2.0/epikstreamer_9.2.0_amd64.deb\",\"sha256\":\"" + new string('b', 64) + "\"}," +
               "\"arm64\":{\"url\":\"https://github.com/nano11b/EpikStreamer/releases/download/v9.2.0/epikstreamer_9.2.0_arm64.deb\",\"sha256\":\"" + new string('c', 64) + "\"}}}";
    var lu = Updater.ParseManifest(lman);
    Check(lu.Url.EndsWith("_" + Updater.LinuxArch + ".deb") && lu.Sha256 == new string(Updater.LinuxArch == "arm64" ? 'c' : 'b', 64) && lu.Version == new Version(9, 2, 0, 0), "Linux client picks linux." + Updater.LinuxArch + " .deb + its sha256");
    try { Updater.ParseManifest(lman[..lman.IndexOf(",\"linux\"", StringComparison.Ordinal)] + "}"); Check(false, "Windows-only manifest rejected on Linux"); }
    catch (InvalidOperationException e) { Check(e.Message.Contains("no Linux"), "Windows-only manifest → clear 'no Linux package' error on Linux"); }
    Os.Windows = true;
    var wu = Updater.ParseManifest(lman);
    Check(wu.Url.EndsWith("setup.exe") && wu.Sha256 == new string('a', 64), "Windows client ignores the linux key (manifest stays backward compatible)");
    Os.Windows = OperatingSystem.IsWindows();
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
if (mode == "requests") { prof.NowPlaying.Enabled = true; prof.NowPlaying.Mode = "Always"; prof.RecordEnabled = false; }
else prof.RecordEnabled = true;
prof.RecordFolder = $"{dir}/rec"; prof.RemuxToMp4 = true; prof.RecordPattern = "{profile}_{part}";
if (Directory.Exists(prof.RecordFolder)) Directory.Delete(prof.RecordFolder, true);
foreach (var f in new[] { "outA.flv", "outB.flv" }) File.Delete(Path.Combine(dir, f));
var destinations = new List<CommandBuilder.Destination>
{
    new("File A", $"{dir}/outA.flv"),
    new("File B", $"{dir}/outB.flv"),
    new("Unreachable", "rtmp://127.0.0.1:1935/x/SECRET|KEY"),
};
if (mode == "fatal") prof.MaxRetries = 4;
var eng = new StreamEngine(prof, FF, destinations, new[] { "SECRET|KEY" }, $"{dir}/work");
int hookCalls = 0;
if (mode == "drop")   // v2.4.1: reconnect hook may swap in a new EpikChat session key (index 2 = the rtmp destination)
    eng.BeforeReconnect = async () => { hookCalls++; await Task.Delay(50);
        return new Dictionary<int, (string, string)> { [2] = ("rtmp://127.0.0.1:1935/x/NEWSESSION_key", "NEWSESSION_key") }; };
var logAll = new StringBuilder();
eng.TextOverlays = mode == "audio";
SongRequestQueue? rq = null; var reqLib = new List<LibraryItem>(); var tracks = new List<string>();
if (mode == "requests")
{
    rq = new SongRequestQueue(new RequestSettings { SkipVotes = 2 }); eng.Requests = rq;
    reqLib.AddRange(prof.Playlist.Select(f => new LibraryItem { Path = f, Title = LibraryItem.TitleFromFile(f), Duration = 10 }));
    eng.Library = f => reqLib.FirstOrDefault(i => i.Path == f);
    eng.TrackChanged += f => { lock (tracks) tracks.Add(f); Console.WriteLine("TRACK " + Path.GetFileName(f)); };
}
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
else if (mode == "requests")
{
    // playlist: sample.mp4 (60s) → silent mkv → hd mov. A request for the hd clip must play right after sample.mp4,
    // then a vote skip (threshold 2) cuts it and the playlist continues with the silent mkv. The card is rendered and overlaid.
    At(3, () => Console.WriteLine("REQ " + rq!.Handle(new("amy", "!sr hd 1080 60"), reqLib, eng.OnAirItem, eng.TrackKey, 5, DateTime.Now)?.Reply), "REQUEST");
    At(5, eng.Skip, "SKIP sample");
    At(10, () =>
    {
        Check(tracks.Count >= 2 && tracks[1].EndsWith("hd_1080_60.mov"), "request played next: " + string.Join(" → ", tracks.Select(Path.GetFileName)));
        Check(rq!.Playing?.User == "amy" && eng.NowPlaying()?.RequestedBy == "amy", "now playing knows the requester");
        var a = rq.VoteSkip("x", eng.TrackKey, 5); var b = rq.VoteSkip("y", eng.TrackKey, 5);
        Check(!a.Skip && b.Skip, "vote skip passed"); if (b.Skip) eng.Skip();
        Check(eng.Card != null && eng.Card.Renders > 0, $"card rendered {eng.Card?.Renders}x");
    }, "VOTE SKIP");
    At(16, () => { for (int w = 0; w < 30 && tracks.Count < 3; w++) Thread.Sleep(100); /* on-air switch lands after the 1 s transition */
        Check(tracks.Count >= 3 && tracks[2].EndsWith("silent_640x480_25.mkv"), "after the skip the playlist continues: " + string.Join(" → ", tracks.Select(Path.GetFileName))); }, "CHECK");
    At(20, eng.Stop, "STOP");
}
else if (mode == "drop")
{
    At(8, eng.SimulateDrop, "DROP");
    At(22, eng.Stop, "STOP");
}
if (mode == "fatal")
{
    string? lastState = null; eng.State += s => lastState = s;
    Check(eng.Wait(15000), "engine gave up quickly on an option error");
    var la = logAll.ToString();
    Check(la.Contains("rejected the command line") && !la.Contains("Reconnecting in"), "no reconnect retries burned");
    Check(lastState?.Contains("cannot be applied to input url pipe:0") == true, "state shows the ffmpeg line: " + lastState);
    Console.WriteLine(fails == 0 ? "ENGINE TEST PASSED" : $"{fails} FAILED");
    return fails;
}
Check(eng.Wait(20000), "engine stopped");
Check(!logAll.ToString().Contains("SECRET") && !logAll.ToString().Contains("KEY"), "no stream key in logs");
if (mode == "drop")
{
    Check(hookCalls >= 1, $"BeforeReconnect awaited before reconnecting ({hookCalls}x)");
    Check(logAll.ToString().Contains("now uses the new EpikChat live session") && !logAll.ToString().Contains("NEWSESSION"), "replacement key applied and masked in logs");
}
Check(File.Exists(eng.PreviewPath), "preview written");
foreach (var r in eng.Recordings) Check(File.Exists(r) && r.EndsWith(".mp4"), "recording remuxed: " + r + (File.Exists(r) ? $" ({new FileInfo(r).Length} B)" : ""));
Console.WriteLine(fails == 0 ? "ENGINE TEST PASSED" : $"{fails} FAILED");
return fails;
