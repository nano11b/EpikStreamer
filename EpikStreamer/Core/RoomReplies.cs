namespace EpikStreamer.Core;

/// <summary>
/// 2.8.1: delivers chat replies to the room the command came from, whatever tab EpikChat is showing. Replies only ever go
/// through the page's own composer (never over the socket), so a reply for a background room is: switch the page to that room
/// (its sidebar button), verify the observer now reports it as the visible room, type + send, verify the post came back on the
/// tap as our own message in THAT room, then restore the tab that was active before (unless the user moved meanwhile). One
/// reply at a time (serialized), so replies to different rooms can't interleave; a burst for the same room shares one switch.
/// Independent of whether the 💬 Chat panel is on screen. With switching disabled, background-room replies wait (max 5 min)
/// until their room is the active tab.
/// </summary>
public sealed class RoomReplyDispatcher
{
    public interface IPage
    {
        /// <summary>The chat page is loaded and usable.</summary>
        bool Ready { get; }
        /// <summary>The room the observer reports as visible (active tab), "" if unknown.</summary>
        string VisibleRoom { get; }
        /// <summary>Clicks the room in EpikChat's own room list; true if a matching button was clicked.</summary>
        Task<bool> ClickRoom(string room);
        /// <summary>Types into the composer of the visible room and submits; returns "sent …" or a reason.</summary>
        Task<string> SendNow(string text);
    }

    sealed record Item(string Text, string Room, DateTime At, TaskCompletionSource<string> Done) { public int Tries; }

    readonly IPage _page; readonly ChatFeedRouter _feed;
    readonly object _lk = new(); readonly List<Item> _q = new();
    readonly SemaphoreSlim _busy = new(1, 1);
    public RoomReplyDispatcher(IPage page, ChatFeedRouter feed) { _page = page; _feed = feed; }

    public Func<bool> AllowSwitch { get; set; } = () => true;
    public TimeSpan SwitchTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan EchoTimeout { get; set; } = TimeSpan.FromSeconds(6);
    public TimeSpan Settle { get; set; } = TimeSpan.FromMilliseconds(400);
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxQueue { get; set; } = 30;
    public int MaxTries { get; set; } = 3;
    /// <summary>Status lines for the log (no message text).</summary>
    public event Action<string>? Status;
    public int Count { get { lock (_lk) return _q.Count; } }
    public int Sent { get; private set; }
    public int Failed { get; private set; }

    /// <summary>Queues a reply for <paramref name="room"/> ("" = the visible room). Completes when delivered, failed or expired.</summary>
    public Task<string> Enqueue(string text, string room)
    {
        var it = new Item(text, (room ?? "").Trim(), DateTime.Now, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_lk)
        {
            if (_q.Count >= MaxQueue) { var old = _q[0]; _q.RemoveAt(0); Finish(old, "failed: reply queue full (dropped oldest)", false); }
            _q.Add(it);
        }
        _feed.NoteReply(RoomLabel(it.Room), "queued");
        return it.Done.Task;
    }
    string RoomLabel(string r) => r.Length > 0 ? r : (_page.VisibleRoom is { Length: > 0 } v ? v : "(visible room)");
    void Finish(Item it, string result, bool ok, bool confirmed = false)
    {
        if (ok) { Sent++; _feed.NoteReply(RoomLabel(it.Room), "sent", result); if (confirmed) _feed.NoteReply(RoomLabel(it.Room), "confirmed", result); }
        else { Failed++; _feed.NoteReply(RoomLabel(it.Room), "failed", result); }
        it.Done.TrySetResult(result);
    }
    bool Visible(string room) => ChatFeedRouter.SameRoom(_page.VisibleRoom, room);

    async Task<bool> WaitVisible(string room)
    {
        var until = DateTime.Now + SwitchTimeout;
        while (DateTime.Now < until) { if (Visible(room)) { await Task.Delay(Settle); return Visible(room); } await Task.Delay(100); }
        return false;
    }
    // the tab the user wants to end up on (captured before our first switch; updated if the user picks a tab meanwhile)
    string _origin = ""; string? _expect;
    void TrackUser()
    {
        var v = _page.VisibleRoom;
        if (_expect == null) _origin = v;                                          // not switched by us: whatever is shown is the user's
        else if (v.Length > 0 && !ChatFeedRouter.SameRoom(v, _expect)) _origin = v;  // the page left the room we opened → the user clicked
    }
    async Task<bool> SwitchTo(string room)
    {
        TrackUser();
        if (Visible(room)) return true;
        if (!await _page.ClickRoom(room) || !await WaitVisible(room)) return false;
        _expect = room; return true;
    }

    /// <summary>Delivers what can be delivered now. Safe to call often (timer, after Enqueue, on room change); runs one at a time.</summary>
    public async Task Pump()
    {
        if (!await _busy.WaitAsync(0)) return;
        try
        {
            while (true)
            {
                if (!_page.Ready) return;
                Item? first;
                lock (_lk)
                {
                    var now = DateTime.Now;
                    foreach (var x in _q.Where(x => now - x.At > MaxAge).ToList()) { _q.Remove(x); Finish(x, $"failed: expired after {MaxAge.TotalMinutes:0} min waiting for {RoomLabel(x.Room)}", false); }
                    // FIFO, but with switching off only replies for the visible room can go
                    first = _q.FirstOrDefault(x => x.Room.Length == 0 || Visible(x.Room) || AllowSwitch());
                }
                if (first == null) break;
                await DeliverRoom(first.Room);
            }
            await Restore();
        }
        finally { _busy.Release(); }
    }

    async Task DeliverRoom(string room)
    {
        TrackUser();
        bool here = room.Length == 0 || Visible(room), switched = false;
        if (!here)
        {
            if (!await SwitchTo(room))
            {
                lock (_lk) foreach (var x in _q.Where(x => ChatFeedRouter.SameRoom(x.Room, room)).ToList())
                    if (++x.Tries >= MaxTries) { _q.Remove(x); Finish(x, $"failed: couldn't open {room} (room button not found or the page didn't switch)", false); }
                Status?.Invoke($"reply: couldn't switch to {room}");
                return;
            }
            switched = true;
        }
        string target = room.Length > 0 ? room : _page.VisibleRoom;
        while (true)
        {
            Item? it;
            lock (_lk) it = _q.FirstOrDefault(x => room.Length == 0 ? x.Room.Length == 0 : ChatFeedRouter.SameRoom(x.Room, room));
            if (it == null) break;
            // the user may have clicked another tab while we were typing the previous reply: re-verify before every post
            if (room.Length > 0 && !Visible(room) && !await SwitchTo(room)) { if (++it.Tries >= MaxTries) { lock (_lk) _q.Remove(it); Finish(it, $"failed: {room} not visible", false); } break; }
            lock (_lk) _q.Remove(it);
            var since = DateTime.Now.AddMilliseconds(-50);
            var r = await _page.SendNow(it.Text);
            if (!r.StartsWith("sent")) { Finish(it, "not sent: " + r, false); continue; }
            var seen = await WaitEcho(it.Text, since);
            var how = switched ? $" (switched to {target} briefly)" : "";
            if (seen == null) Finish(it, $"sent{how}; not confirmed (own message not seen on the feed within {EchoTimeout.TotalSeconds:0}s)", true);
            else if (seen.Length > 0 && target.Length > 0 && !ChatFeedRouter.SameRoom(seen, target)) Finish(it, $"failed: posted in {seen} instead of {target}", false);
            else Finish(it, $"sent ✓ in {(seen.Length > 0 ? seen : target)}{how}", true, true);
        }
    }

    async Task<string?> WaitEcho(string text, DateTime since)
    {
        var until = DateTime.Now + EchoTimeout;
        while (DateTime.Now < until) { var r = _feed.SelfPostRoom(text, since); if (r != null) return r; await Task.Delay(100); }
        return null;
    }

    /// <summary>After the queue is done: back to the user's tab (the one active before our first switch, or the one they clicked
    /// while we were replying). Nothing to do if we never switched.</summary>
    async Task Restore()
    {
        if (_expect == null) return;
        await Task.Delay(Settle);   // a tab click the user made while we were posting may still be landing
        TrackUser();
        var back = _origin; _expect = null;
        if (back.Length == 0 || Visible(back)) return;
        if (!await _page.ClickRoom(back) || !await WaitVisible(back)) Status?.Invoke($"reply: couldn't switch back to {back}");
    }
}
