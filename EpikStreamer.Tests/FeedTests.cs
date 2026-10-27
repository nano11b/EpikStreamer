using EpikStreamer.Core;
/// <summary>v2.7 ChatFeedRouter / ReplyQueue unit tests (dotnet T feed).</summary>
static class FeedTests
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
    public static int Run()
    {
        var t0 = new DateTime(2026, 10, 27, 12, 0, 0);
        var s = new ChatFeedSettings();
        var r = new ChatFeedRouter(s); var got = new List<ChatMessage>(); r.Message += got.Add;
        var views = new List<string>(); r.Viewers += (src, n) => views.Add(src + ":" + string.Join(",", n));
        ChatMessage M(string id, string user, string text, string room = "") => new(user, text, id, false, false, room, "");

        // Sam streams in Lobby, then looks at Room B
        r.SetVisibleRoom("Lobby"); r.MarkStreamStart(); r.SetVisibleRoom("Room B");
        Check(r.TargetRoom == "Lobby", "auto command room = room shown when going live");
        // tap message for Lobby with id only: unknown → pending; DOM (Room B) never shows it → mapping learned later
        r.FromTapMessage(M("a1", "alice", "!song"), "r-lobby", t0);
        Check(got.Count == 0, "tap message from an unknown room id waits (pending)");
        r.FromDomMessage(M("b1", "bob", "!song", "Room B"), t0.AddSeconds(0.5));
        r.FromTapMessage(M("b1", "bob", "!song"), "r-b", t0.AddSeconds(0.6));
        Check(got.Count == 0 && r.RoomIds.TryGetValue("r-b", out var nb) && nb == "Room B", "visible other room ignored; DOM↔tap id twin teaches r-b = Room B");
        r.Flush(t0.AddSeconds(3));
        Check(got.Count == 0 && r.Unmatched == 1, "unresolved pending message dropped after the wait (counted)");
        // a tap frame that carries the room name teaches the Lobby id
        r.FromTapMessage(M("a2", "carol", "!uptime", "Lobby"), "r-lobby", t0.AddSeconds(4));
        r.FromTapMessage(M("a3", "dave", "!help"), "r-lobby", t0.AddSeconds(5));
        Check(got.Select(m => m.Id).SequenceEqual(new[] { "a2", "a3" }) && got[1].Room == "Lobby", "Lobby commands from the tap while Room B is visible (room learned by id)");
        // DOM later shows Lobby too: same ids → no duplicates
        r.SetVisibleRoom("Lobby");
        r.FromDomMessage(M("a3", "dave", "!help", "Lobby"), t0.AddSeconds(6));
        r.FromTapMessage(M("a3", "dave", "!help"), "r-lobby", t0.AddSeconds(6.1));
        Check(got.Count == 2 && r.Duplicates >= 2, "same message from DOM and tap delivered once");
        // DOM first, tap second (other id format) → cross-source user+text dedupe
        r.FromDomMessage(M("dom-x", "erin", "!song", "Lobby"), t0.AddSeconds(7));
        r.FromTapMessage(M("tap-x", "erin", "!song"), "r-lobby", t0.AddSeconds(7.5));
        Check(got.Count(m => m.User == "erin") == 1, "different id formats: user+text twin within 15 s deduped");
        // but the same user repeating later from the same source is a new message
        r.FromTapMessage(M("tap-y", "erin", "!song"), "r-lobby", t0.AddSeconds(30));
        Check(got.Count(m => m.User == "erin") == 2, "a later repeat is a new message");
        // all rooms
        s.CommandRoom = ChatFeedSettings.AllRooms;
        r.FromTapMessage(M("c1", "frank", "!song"), "r-b", t0.AddSeconds(31));
        Check(got.Last().Id == "c1" && got.Last().Room == "Room B", "all-rooms mode accepts other rooms");
        s.CommandRoom = "Room B";
        r.FromTapMessage(M("c2", "gina", "!song"), "r-lobby", t0.AddSeconds(32));
        r.FromTapMessage(M("c3", "hank", "!song"), "r-b", t0.AddSeconds(32));
        Check(got.Last().Id == "c3", "explicit command room filters");
        // self
        r.Self = "MeStreamer"; r.FromTapMessage(M("c4", "MeStreamer", "!song"), "r-b", t0.AddSeconds(33));
        Check(got.Last().Id == "c4" && got.Last().Self, "own messages flagged self (by name)");

        // viewers
        r.FromTapViewers("join", new[] { "zed" }, t0); r.FromTapViewers("join", new[] { "bob" }, t0); r.FromTapViewers("leave", new[] { "bob" }, t0);
        r.FromTapViewers("snapshot", new[] { "carol", "zed" }, t0);
        Check(string.Join(" ", views) == "LiveViewers:zed LiveViewers:bob,zed LiveViewers:zed LiveViewers:carol,zed", "tap viewer join/leave/snapshot → snapshots: " + string.Join(" ", views));
        Check(r.TapViewersLive(t0.AddMinutes(5)) && !r.TapViewersLive(t0.AddMinutes(11)), "tap viewers count as live for 10 min after an event");
        var tr = new ViewerTracker(new ViewerSettings { LeaveGraceSeconds = 0 }); var joins = new List<string>(); var leaves = new List<string>();
        tr.Joined += v => joins.Add(v.Name); tr.Left += v => leaves.Add(v.Name);
        var r2 = new ChatFeedRouter(new()); r2.Viewers += (src, n) => tr.Update(src, n, t0);
        r2.FromTapViewers("join", new[] { "zed" }, t0); r2.FromTapViewers("join", new[] { "bob" }, t0); r2.FromTapViewers("leave", new[] { "bob" }, t0);
        Check(string.Join(",", joins) == "zed,bob" && string.Join(",", leaves) == "bob", "ViewerTracker gets join/leave from tap events (panel closed)");
        Check(r.SourceStatus(t0.AddSeconds(40)).StartsWith("Chat: live feed ✓") && new ChatFeedRouter(new()).SourceStatus(t0).Contains("DOM fallback"), "source indicator: live feed vs DOM fallback");

        // reply queue
        var q = new ReplyQueue(); q.Enqueue("Now playing: X", "Lobby", t0); q.Enqueue("Live for 1h", "Room B", t0);
        Check(q.TakeFor("Room C", t0).Count == 0 && q.TakeFor("lobby", t0.AddSeconds(5)).SequenceEqual(new[] { "Now playing: X" }) && q.Count == 1, "reply queued until its room is visible");
        Check(q.TakeFor("Room B", t0.AddMinutes(6)).Count == 0 && q.Count == 0, "queued replies expire after 5 min");
        Console.WriteLine(fails == 0 ? "FEED UNIT PASSED" : $"{fails} FAILED");
        return fails;
    }
}
