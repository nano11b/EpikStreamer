using EpikStreamer.Core;
/// <summary>2.8.1: commands from background rooms (Lobby active), room-name normalization, room-aware dedupe, per-room stats and
/// the serialized switch → verify → post → confirm → restore reply dispatcher against a fake chat page (dotnet T feed / rooms).</summary>
static class RoomsTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    static ChatMessage M(string id, string user, string text, string room = "") => new(user, text, id, false, false, room, "");

    /// <summary>Fake EpikChat page: a room sidebar, a visible room, a composer that posts in the visible room and echoes our own
    /// message back through the tap (like the site's message(self) with targetId = the visible room).</summary>
    sealed class FakePage : RoomReplyDispatcher.IPage
    {
        public readonly ChatFeedRouter Feed; public readonly Dictionary<string, string> Ids;   // display name → room id
        public readonly List<(string room, string text)> Posted = new(); public readonly List<string> Clicks = new();
        public Action<string>? OnSend;   // test hook (e.g. the user clicks a tab while we type)
        public int SwitchDelayMs = 30; public bool EchoOn = true; int _n;
        string _vis;
        public FakePage(ChatFeedRouter feed, Dictionary<string, string> ids, string visible) { Feed = feed; Ids = ids; _vis = visible; feed.SetVisibleRoom(visible); }
        public bool Ready => true;
        public string VisibleRoom => Feed.VisibleRoom;
        public void UserClicks(string room) { _vis = room; Feed.SetVisibleRoom(room); }
        public async Task<bool> ClickRoom(string room)
        {
            var hit = Ids.Keys.FirstOrDefault(k => ChatFeedRouter.SameRoom(k, room)); Clicks.Add(room);
            if (hit == null) return false;
            await Task.Delay(SwitchDelayMs); UserClicks(hit); return true;   // the observer re-attaches and reports the room shown
        }
        public async Task<string> SendNow(string text)
        {
            OnSend?.Invoke(text);
            var room = _vis; Posted.Add((room, text)); await Task.Delay(20);
            if (EchoOn) Feed.FromTapMessage(M("self-" + ++_n, "meowington", text), Ids[room], DateTime.Now);
            return "sent";
        }
    }

    public static int Run()
    {
        fails = 0;
        var t0 = new DateTime(2026, 10, 27, 12, 0, 0);

        // --- the reported bug: a 2.7.0-era blank command room that survived the 2.7.1 migration (form saved it back) ---
        var bug = System.Text.Json.JsonSerializer.Deserialize<ChatFeedSettings>("{\"CommandRoom\":\"\",\"DefaultsVersion\":271,\"AllowRoomSwitchToReply\":true}")!;
        var rb = new ChatFeedRouter(bug); var gb = new List<ChatMessage>(); rb.Message += gb.Add;
        rb.SetVisibleRoom("Lobby"); rb.MarkStreamStart();
        rb.FromTapMessage(M("x1", "nano11b", "!help", "ImpulsiveThots"), "r-it", t0);
        Check(gb.Count == 1 && gb[0].Room == "ImpulsiveThots", "blank command room = all rooms: !help from background ImpulsiveThots delivered while Lobby is active");
        Check(bug.Migrate() && bug.CommandRoom == "*" && bug.DefaultsVersion >= 281 && !bug.Migrate(), "2.8.1 migration repairs a blank room saved with DefaultsVersion 271");
        var keep = System.Text.Json.JsonSerializer.Deserialize<ChatFeedSettings>("{\"CommandRoom\":\"Lobby\",\"DefaultsVersion\":271}")!; keep.Migrate();
        Check(keep.CommandRoom == "Lobby", "a named command room survives the 2.8.1 migration");

        // --- room names: apostrophes / case ---
        Check(ChatFeedRouter.SameRoom("Drugs''R''Us", "Drugs'R'Us") && ChatFeedRouter.SameRoom("drugs’r’us", "Drugs'R'Us") && ChatFeedRouter.SameRoom(" impulsivethots ", "ImpulsiveThots") && !ChatFeedRouter.SameRoom("Lobby", "Lobby 2"),
            "room names compared with quotes/case/space normalized");
        var sn = new ChatFeedSettings { CommandRoom = "Drugs'R'Us" }; var rn = new ChatFeedRouter(sn); var gn = new List<ChatMessage>(); rn.Message += gn.Add;
        rn.FromTapMessage(M("n1", "zoe", "!song", "Drugs''R''Us"), "r-d", t0);
        Check(gn.Count == 1, "named command room with apostrophes matches the socket's doubled-quote name");

        // --- room-aware cross-source dedupe; per-room stats ---
        var s = new ChatFeedSettings(); var r = new ChatFeedRouter(s); var got = new List<ChatMessage>(); r.Message += got.Add;
        r.SetVisibleRoom("Lobby");
        r.FromDomMessage(M("dom-1", "nano11b", "!help", "Lobby"), t0);
        r.FromTapMessage(M("tap-1", "nano11b", "!help", "Lobby"), "r-lobby", t0.AddSeconds(0.3));
        r.FromTapMessage(M("tap-2", "nano11b", "!help", "ImpulsiveThots"), "r-it", t0.AddSeconds(1));
        r.FromTapMessage(M("tap-3", "nano11b", "!help", "Drugs''R''Us"), "r-d", t0.AddSeconds(1.5));
        Check(got.Count == 3 && got.Select(m => m.Room).SequenceEqual(new[] { "Lobby", "ImpulsiveThots", "Drugs''R''Us" }), "same user+text in another room within 15 s is NOT a duplicate (same room is)");
        var st = r.RoomStatsSnapshot();
        Check(st.Values.Sum(x => x.Received) >= 4 && st.Values.Sum(x => x.CommandsSeen) >= 3 && st.Values.Sum(x => x.Duplicates) == 1, "per-room received/commandsSeen/duplicates counted");
        var s2 = new ChatFeedSettings { CommandRoom = "Lobby" }; var r2 = new ChatFeedRouter(s2);
        r2.FromTapMessage(M("o1", "nano11b", "!help", "ImpulsiveThots"), "r-it", t0);
        var o = r2.RoomStatsSnapshot().Values.FirstOrDefault(x => x.Dropped.Count > 0);
        Check(o != null && o.Dropped.Keys.Any(k => k.Contains("command room")) && (o.LastDropReason ?? "").Length > 0, "drops counted per room with a reason");
        var diag = System.Text.Json.JsonSerializer.Serialize(r.RoomDiagnostics());
        Check(!diag.Contains("!help"), "per-room diagnostics carry no message text");

        // --- per-room command cooldown ---
        var cs = new ChatSettings { CommandsEnabled = true, CooldownSeconds = 30 }; var cc = new ChatCommands(cs);
        var ctx = new ChatCommands.Context();
        var a1 = cc.Handle(M("h1", "nano11b", "!help", "Lobby"), ctx, t0);
        var a2 = cc.Handle(M("h2", "nano11b", "!help", "ImpulsiveThots"), ctx, t0.AddSeconds(1));
        var a3 = cc.Handle(M("h3", "nano11b", "!help", "Lobby"), ctx, t0.AddSeconds(2));
        Check(a1 != null && a2 != null && a3 == null && cc.LastDrop == "cooldown", "command cooldown is per room (Lobby !help doesn't silence ImpulsiveThots)");

        // --- dispatcher: Lobby active, commands from background rooms ---
        Dispatcher().GetAwaiter().GetResult();
        Console.WriteLine(fails == 0 ? "ROOMS UNIT PASSED" : $"{fails} ROOMS FAILED");
        return fails;
    }

    static (ChatFeedRouter, FakePage, RoomReplyDispatcher) Rig(string visible = "Lobby")
    {
        var r = new ChatFeedRouter(new ChatFeedSettings()) { Self = "meowington" };
        var ids = new Dictionary<string, string> { ["Lobby"] = "r-lobby", ["ImpulsiveThots"] = "r-it", ["Drugs'R'Us"] = "r-d" };
        foreach (var (n, id) in ids) r.FromTapMessage(M("seed-" + id, "seed", "hi", n), id, DateTime.Now.AddMinutes(-1));
        var p = new FakePage(r, ids, visible);
        var d = new RoomReplyDispatcher(p, r) { Settle = TimeSpan.FromMilliseconds(10), EchoTimeout = TimeSpan.FromSeconds(1.5), SwitchTimeout = TimeSpan.FromSeconds(1) };
        return (r, p, d);
    }

    static async Task Dispatcher()
    {
        // 1) the reported scenario end to end: Lobby active, !help in background ImpulsiveThots → reply there, Lobby restored
        {
            var (r, p, d) = Rig();
            var cmds = new ChatCommands(new ChatSettings { CommandsEnabled = true });
            ChatMessage? seen = null; r.Message += m => { if (m.User == "nano11b") seen = m; };
            r.FromTapMessage(M("m1", "nano11b", "!help", "ImpulsiveThots"), "r-it", DateTime.Now);
            var reply = seen == null ? null : cmds.Handle(seen, new ChatCommands.Context(), DateTime.Now);
            var t = d.Enqueue(reply ?? "?", seen?.Room ?? ""); await d.Pump(); var res = await t;
            Check(seen?.Room == "ImpulsiveThots" && reply != null, "!help from background ImpulsiveThots reaches the command handler (Lobby active)");
            Check(p.Posted.Count == 1 && p.Posted[0].room == "ImpulsiveThots" && res.StartsWith("sent ✓"), "reply posted in ImpulsiveThots, confirmed by own message echo: " + res);
            Check(p.VisibleRoom == "Lobby", "active tab restored to Lobby");
            var rs = r.RoomStatsSnapshot().First(x => ChatFeedRouter.SameRoom(x.Key, "ImpulsiveThots")).Value;
            Check(rs.RepliesQueued == 1 && rs.RepliesSent == 1 && rs.RepliesFailed == 0, "per-room replies queued/sent counted");
        }
        // 2) apostrophe room (socket name doubled quotes) + several rooms queued at once: serialized, each in its room, one restore
        {
            var (r, p, d) = Rig();
            var t1 = d.Enqueue("reply A", "Drugs''R''Us"); var t2 = d.Enqueue("reply B", "ImpulsiveThots"); var t3 = d.Enqueue("reply C", "Drugs'R'Us"); var t4 = d.Enqueue("reply L", "Lobby");
            await Task.WhenAll(d.Pump(), d.Pump(), d.Pump());   // concurrent pumps: only one runs
            while (d.Count > 0) await d.Pump();
            var res = await Task.WhenAll(t1, t2, t3, t4);
            Check(res.All(x => x.StartsWith("sent ✓")), "all replies confirmed: " + string.Join(" | ", res));
            Check(p.Posted.Select(x => x.room + ":" + x.text).SequenceEqual(new[] { "Drugs'R'Us:reply A", "Drugs'R'Us:reply C", "ImpulsiveThots:reply B", "Lobby:reply L" })
               || p.Posted.All(x => (x.text == "reply A" || x.text == "reply C") ? x.room == "Drugs'R'Us" : x.text == "reply B" ? x.room == "ImpulsiveThots" : x.room == "Lobby"),
                "each reply landed in its origin room (apostrophe room included): " + string.Join(", ", p.Posted.Select(x => x.room + ":" + x.text)));
            Check(p.VisibleRoom == "Lobby", "active tab back on Lobby after a multi-room batch");
        }
        // 3) the user switches tabs mid-reply: reply still lands in its room, and the user's new choice is kept
        {
            var (r, p, d) = Rig();
            p.OnSend = txt => { p.OnSend = null; _ = Task.Run(async () => { await Task.Delay(5); p.UserClicks("Drugs'R'Us"); }); };
            var t = d.Enqueue("mid reply", "ImpulsiveThots"); await d.Pump(); var res = await t;
            Check(p.Posted.Count == 1 && p.Posted[0].room == "ImpulsiveThots" && res.StartsWith("sent ✓"), "tab switched while posting: reply still in ImpulsiveThots: " + res);
            Check(p.VisibleRoom == "Drugs'R'Us", "the tab the user picked mid-reply is kept (not yanked back to Lobby)");
        }
        // 4) user switches tabs after our switch but before we type → re-verify, switch back, post in the right room, keep user's tab
        {
            var (r, p, d) = Rig(); int clicks = 0;
            var orig = p.SwitchDelayMs;
            var t = d.Enqueue("verify reply", "ImpulsiveThots");
            var pump = d.Pump();
            await Task.Delay(orig + 2); if (++clicks == 1) p.UserClicks("Lobby");
            while (d.Count > 0) await d.Pump();
            await pump; var res = await t;
            Check(p.Posted.All(x => x.room == "ImpulsiveThots") && p.Posted.Count == 1, "visibility re-verified before posting (never posts in the wrong room): " + res + " posted=" + string.Join(",", p.Posted.Select(x => x.room)));
        }
        // 5) echo shows the message landed elsewhere → failed, counted
        {
            var (r, p, d) = Rig("ImpulsiveThots");
            d.Enqueue("x", "ImpulsiveThots");   // warm
            await d.Pump();
            var res = "";
            p.OnSend = _ => p.UserClicks("Lobby");   // composer of another room got it
            var t = d.Enqueue("wrong room", "ImpulsiveThots"); await d.Pump(); res = await t;
            Check(res.StartsWith("failed: posted in Lobby"), "own message echoed in another room → reported failed: " + res);
            Check(d.Failed == 1, "failure counted");
        }
        // 6) room switching disabled: background replies wait for the user, visible-room replies still go
        {
            var (r, p, d) = Rig(); d.AllowSwitch = () => false;
            var tb = d.Enqueue("bg", "ImpulsiveThots"); var tl = d.Enqueue("fg", "Lobby"); await d.Pump();
            Check(p.Posted.Count == 1 && p.Posted[0].room == "Lobby" && !tb.IsCompleted && p.Clicks.Count == 0, "switching off: no tab clicks, visible-room reply sent, other waits");
            p.UserClicks("ImpulsiveThots"); await d.Pump();
            Check(tb.IsCompleted && p.Posted.Last().room == "ImpulsiveThots", "…and goes when the user opens that room");
        }
        // 7) never via the socket: the dispatcher only has ClickRoom + composer SendNow (compile-time: IPage has no raw send)
        Check(typeof(RoomReplyDispatcher.IPage).GetMethods().All(m => m.Name is "get_Ready" or "get_VisibleRoom" or "ClickRoom" or "SendNow"), "page interface = room click + composer only (no socket send)");
    }
}
