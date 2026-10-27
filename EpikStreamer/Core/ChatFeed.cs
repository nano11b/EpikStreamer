namespace EpikStreamer.Core;

/// <summary>v2.7 chat feed settings (Settings → Chat).</summary>
public class ChatFeedSettings
{
    /// <summary>Mirror EpikChat's own realtime connection (passive, read-only) so commands and viewers work whatever tab/panel is visible.</summary>
    public bool LiveFeed { get; set; } = true;
    /// <summary>"*" (or blank) = all joined rooms, otherwise one room name. 2.8.1: blank no longer means "the room shown when going
    /// live" — that tied commands to the active tab (a background room's !help was dropped); it is treated as all rooms.</summary>
    public string CommandRoom { get; set; } = AllRooms;
    /// <summary>Reply in the command's room by briefly switching EpikChat to it (sidebar room button → composer → back). 2.8.1: done
    /// whether or not the 💬 Chat panel is on screen (serialized, verified, then the previous tab is restored). Off = replies to a
    /// background room wait until that room is the active tab.</summary>
    public bool AllowRoomSwitchToReply { get; set; } = true;
    [Obsolete("2.8.1: switching no longer depends on the panel being visible")] public bool SwitchWhileVisible { get; set; }
    /// <summary>The first-run "Reply to chat commands in chat?" question was answered.</summary>
    public bool ReplyPromptShown { get; set; }
    /// <summary>Settings migration marker (0 = written by ≤2.7.0).</summary>
    public int DefaultsVersion { get; set; }
    /// <summary>2.7.1 defaults: all joined rooms, room switching on. Applied once to settings saved by 2.7.0 (whose blank = auto room).</summary>
    /// <para>2.8.1: settings written by 2.7.1–2.8.0 could carry a blank room with DefaultsVersion 271 — the settings form was filled
    /// before this migration ran, so the next save wrote the old blank back (the background-room bug). Blank is repaired once more.</para>
    public bool Migrate()
    {
        if (DefaultsVersion >= 281) return false;
        if (DefaultsVersion < 271) AllowRoomSwitchToReply = true;
        if (string.IsNullOrWhiteSpace(CommandRoom)) CommandRoom = AllRooms;
        DefaultsVersion = 281; return true;
    }
    /// <summary>Viewers fallback: repeat the viewer-list request EpikChat itself made (read-only, every ≥15 s) while no live viewer events arrive.</summary>
    public bool ViewersRestFallback { get; set; } = true;
    public int ViewersRestSeconds { get; set; } = 20;
    /// <summary>Advanced: JSON object overriding the tap's event names / field paths (see README). Blank = built-in guesses.</summary>
    public string ProtocolMapJson { get; set; } = "";
    public const string AllRooms = "*";
}

/// <summary>
/// Merges the two chat sources (the DOM observer of the visible pane and the passive network tap) into one stream:
/// de-duplicates by message id (and by user+text across sources), keeps only the command room, learns tap room ids ↔ room
/// names, and turns tap viewer join/leave events into the same snapshots the DOM viewer list produces. Pure + unit-tested.
/// </summary>
public sealed class ChatFeedRouter
{
    readonly object _lk = new();
    public ChatFeedSettings S { get; set; }
    public ChatFeedRouter(ChatFeedSettings s) => S = s;

    /// <summary>Accepted messages (exactly once each).</summary>
    public event Action<ChatMessage>? Message;
    /// <summary>(source, names) viewer snapshots for <see cref="ViewerTracker"/>.</summary>
    public event Action<string, List<string>>? Viewers;

    public string VisibleRoom { get; private set; } = "";
    /// <summary>The room the stream goes to: set when going live (the room the chat showed then), or by the host.</summary>
    public string StreamRoom { get; set; } = "";
    public string Self { get; set; } = "";
    public string SelfDisplay { get; set; } = "";
    /// <summary>v2.8: the signed-in user's EpikChat userId (from the socket handshake) and a name → userId map learned from
    /// chat messages and the viewers ack (Engage points are keyed by userId).</summary>
    public string SelfId { get; set; } = "";
    readonly Dictionary<string, string> _nameIds = new(StringComparer.OrdinalIgnoreCase);
    public void LearnUserId(string name, string id)
    {
        if (string.IsNullOrWhiteSpace(name) || !System.Text.RegularExpressions.Regex.IsMatch(id ?? "", "^[A-Za-z0-9_-]{6,40}$")) return;
        lock (_nameIds) { if (_nameIds.Count > 20000) _nameIds.Clear(); _nameIds[name.Trim()] = id!; }
    }
    public string UserIdOf(string name) { lock (_nameIds) return _nameIds.TryGetValue((name ?? "").Trim(), out var id) ? id : ""; }
    /// <summary>The command-room filter: "*" (all joined rooms) or one room name. Never derived from the active tab (2.8.1).</summary>
    public string TargetRoom => AllRooms ? ChatFeedSettings.AllRooms : S.CommandRoom.Trim();
    public bool AllRooms => string.IsNullOrWhiteSpace(S.CommandRoom) || S.CommandRoom.Trim() == ChatFeedSettings.AllRooms;
    /// <summary>Where unsolicited posts (auto trivia, polls started from the app) go: the named command room, else the room the
    /// stream started in, else the active tab.</summary>
    public string HomeRoom => !AllRooms ? TargetRoom : (StreamRoom.Length > 0 ? StreamRoom : VisibleRoom);

    // stats / source indicator
    public DateTime TapLastEvent { get; private set; } = DateTime.MinValue;
    public DateTime TapLastViewerEvent { get; private set; } = DateTime.MinValue;
    public DateTime DomLastMessage { get; private set; } = DateTime.MinValue;
    public bool TapSocketOpen { get; set; }
    public int FromTap { get; private set; }
    public int FromDom { get; private set; }
    public int Duplicates { get; private set; }
    public int OtherRoom { get; private set; }
    public int Unmatched { get; private set; }
    public int TapReconnects { get; private set; }
    public IReadOnlyDictionary<string, string> RoomIds { get { lock (_lk) return new Dictionary<string, string>(_roomById); } }

    readonly HashSet<string> _ids = new(); readonly Queue<string> _idQ = new();
    readonly Dictionary<string, (DateTime at, string src, string room)> _keys = new();
    readonly Dictionary<string, string> _roomById = new(StringComparer.Ordinal);       // tap room id → room name
    readonly Dictionary<string, string> _tapRoomOfId = new(StringComparer.Ordinal);    // message id → tap room id (for learning)
    readonly Dictionary<string, string> _domRoomOfId = new(StringComparer.Ordinal);    // message id → DOM room name
    readonly List<(ChatMessage m, string roomId, DateTime at)> _pending = new();
    readonly HashSet<string> _tapViewers = new(StringComparer.OrdinalIgnoreCase);
    public TimeSpan PendingWait { get; set; } = TimeSpan.FromSeconds(2.5);
    public TimeSpan CrossSourceWindow { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>v2.7.1: room id → name learned by the tap (connectionAccepted favorites, sidebar/room-list acks).</summary>
    public void LearnRoom(string id, string name)
    {
        id = id?.Trim() ?? ""; name = name?.Trim() ?? ""; if (id.Length == 0 || name.Length == 0) return;
        lock (_lk) { if (_roomById.Count < 5000) _roomById[id] = name; }
        Flush(DateTime.Now);
    }
    public string RoomName(string id) { lock (_lk) return _roomById.TryGetValue(id, out var n) ? n : ""; }
    public void SetVisibleRoom(string room) { lock (_lk) VisibleRoom = room?.Trim() ?? ""; }
    /// <summary>Call when the stream starts: an auto command room becomes the room the chat is showing now.</summary>
    public void MarkStreamStart() { lock (_lk) if (StreamRoom.Length == 0) StreamRoom = VisibleRoom; }
    public void MarkStreamStop() { lock (_lk) StreamRoom = ""; }

    static bool Eq(string a, string b) => SameRoom(a, b);
    /// <summary>Room names compare case-insensitively with quotes normalized: the socket can carry a doubled apostrophe
    /// ("Drugs''R''Us") or a typographic one where the page shows "Drugs'R'Us".</summary>
    public static string NormRoom(string? s)
    {
        s = (s ?? "").Trim().Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u02BC', '\'').Replace('`', '\'');
        while (s.Contains("''")) s = s.Replace("''", "'");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").ToLowerInvariant();
    }
    public static bool SameRoom(string? a, string? b) { var x = NormRoom(a); return x.Length > 0 && x == NormRoom(b); }
    string Key(ChatMessage m) => m.User.Trim().ToLowerInvariant() + "\n" + m.Text.Trim();

    bool Seen(string? id) => id != null && _ids.Contains(id);
    void Remember(string? id) { if (id == null || !_ids.Add(id)) return; _idQ.Enqueue(id); if (_idQ.Count > 5000) _ids.Remove(_idQ.Dequeue()); }
    /// <summary>DOM↔tap twin by user+text. 2.8.1: only within the same room (or when one side's room is still unknown) — the same
    /// user typing "!help" in Lobby and then in a background room is two commands, not a duplicate.</summary>
    bool CrossDup(ChatMessage m, string room, string src, DateTime now)
    {
        var k = Key(m);
        if (_keys.TryGetValue(k, out var e) && e.src != src && now - e.at < CrossSourceWindow && (e.room.Length == 0 || room.Length == 0 || SameRoom(e.room, room))) return true;
        _keys[k] = (now, src, room);
        if (_keys.Count > 2000) foreach (var old in _keys.Where(x => now - x.Value.at > CrossSourceWindow).Select(x => x.Key).ToList()) _keys.Remove(old);
        return false;
    }

    /// <summary>A message from the DOM observer (its Room is the visible room's name).</summary>
    public void FromDomMessage(ChatMessage m, DateTime now)
    {
        ChatMessage? emit = null;
        lock (_lk)
        {
            DomLastMessage = now;
            if (m.Room.Length > 0) VisibleRoom = m.Room;
            // learn: the tap saw this id under some room id → that id is this room
            if (m.Id != null && m.Room.Length > 0) { _domRoomOfId[m.Id] = m.Room; if (_domRoomOfId.Count > 3000) _domRoomOfId.Clear(); }
            if (m.Id != null && _tapRoomOfId.TryGetValue(m.Id, out var rid) && rid.Length > 0 && m.Room.Length > 0) _roomById[rid] = m.Room;
            _pending.RemoveAll(p => p.m.Id != null && p.m.Id == m.Id);   // pending tap twin resolved by the DOM copy
            var st = Stats(m.Room); st.Received++; if (IsCmd(m)) st.CommandsSeen++;
            if (m.Self) NoteSelfPost(m.Room, m.Text, now);
            if (Seen(m.Id)) { Duplicates++; Remember(m.Id); st.Duplicates++; }
            else if (CrossDup(m, m.Room, "dom", now)) { Duplicates++; Remember(m.Id); st.Duplicates++; }
            else
            {
                Remember(m.Id);
                if (!AllRooms && m.Room.Length > 0 && !Eq(m.Room, TargetRoom)) { OtherRoom++; Drop(st, "not the command room", now); }
                else { FromDom++; st.Delivered++; emit = m; }
            }
        }
        if (emit != null) Message?.Invoke(emit);
        Flush(now);
    }

    /// <summary>A message from the network tap (room name may be blank; roomId is the protocol's room/channel/topic id).</summary>
    public void FromTapMessage(ChatMessage m, string roomId, DateTime now)
    {
        ChatMessage? emit = null;
        lock (_lk)
        {
            TapLastEvent = now;
            if (m.Id != null && roomId.Length > 0) { _tapRoomOfId[m.Id] = roomId; if (_tapRoomOfId.Count > 3000) _tapRoomOfId.Clear(); }
            if (roomId.Length > 0 && m.Room.Length > 0) _roomById[roomId] = m.Room;
            else if (roomId.Length > 0 && m.Id != null && _domRoomOfId.TryGetValue(m.Id, out var dn)) _roomById[roomId] = dn;
            if (UserEq(m.User, Self) || UserEq(m.User, SelfDisplay) || SelfId.Length > 0 && m.UserId == SelfId) m = m with { Self = true };
            // resolve the room BEFORE dedupe/filtering so both are per room (2.8.1)
            var room = m.Room.Length > 0 ? m.Room : (roomId.Length > 0 && _roomById.TryGetValue(roomId, out var n) ? n : "");
            if (room.Length > 0) m = m with { Room = room };
            var st = Stats(room.Length > 0 ? room : "(room " + (roomId.Length > 0 ? "id " + roomId[..Math.Min(6, roomId.Length)] + "…" : "unknown") + ")");
            st.Received++; if (IsCmd(m)) st.CommandsSeen++;
            if (m.Self) NoteSelfPost(room, m.Text, now);
            if (Seen(m.Id)) { Duplicates++; Remember(m.Id); st.Duplicates++; }
            else if (CrossDup(m, room, "tap", now)) { Duplicates++; Remember(m.Id); st.Duplicates++; }
            else
            {
                Remember(m.Id);
                if (room.Length > 0 ? (AllRooms || Eq(room, TargetRoom)) : (roomId.Length == 0 ? AllRooms : false)) { FromTap++; st.Delivered++; emit = m; }
                else if (room.Length > 0) { OtherRoom++; Drop(st, "not the command room", now); }
                else if (!AllRooms && Eq(roomId, TargetRoom)) { FromTap++; st.Delivered++; emit = m; }
                else _pending.Add((m, roomId, now));   // unknown room id: wait briefly for the DOM twin / a learned mapping
            }
        }
        if (emit != null) Message?.Invoke(emit);
        Flush(now);
    }

    /// <summary>Resolve pending tap messages whose room became known; drop those still unknown after <see cref="PendingWait"/>.</summary>
    public void Flush(DateTime now)
    {
        var emit = new List<ChatMessage>();
        lock (_lk)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                var (m, rid, at) = _pending[i];
                string name = _roomById.TryGetValue(rid, out var n) ? n : "";
                if (name.Length > 0) { var st = Stats(name); if (AllRooms || Eq(name, TargetRoom)) { FromTap++; st.Delivered++; emit.Add(m with { Room = name }); } else { OtherRoom++; Drop(st, "not the command room", now); } _pending.RemoveAt(i--); }
                else if (now - at > PendingWait) { if (AllRooms) { FromTap++; emit.Add(m); } else { Unmatched++; Drop(Stats("(room unknown)"), "room id never resolved", now); } _pending.RemoveAt(i--); }
            }
        }
        foreach (var m in emit) Message?.Invoke(m);
    }

    // ---- 2.8.1 per-room diagnostics (counts and reasons only; no message text or names) ----
    public sealed class RoomStats
    {
        public int Received { get; set; }
        public int CommandsSeen { get; set; }
        public int Delivered { get; set; }
        public int Duplicates { get; set; }
        public Dictionary<string, int> Dropped { get; } = new();
        public int RepliesQueued { get; set; }
        public int RepliesSent { get; set; }
        public int RepliesConfirmed { get; set; }
        public int RepliesFailed { get; set; }
        public string LastDropReason { get; set; } = "";
        public string LastReplyResult { get; set; } = "";
    }
    readonly Dictionary<string, RoomStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    RoomStats Stats(string room)
    {
        var k = room.Length > 0 ? room.Trim() : "(room unknown)";
        lock (_stats)
        {
            var hit = _stats.Keys.FirstOrDefault(x => SameRoom(x, k));
            if (hit != null) return _stats[hit];
            if (_stats.Count > 200) _stats.Clear();
            return _stats[k] = new RoomStats();
        }
    }
    static bool IsCmd(ChatMessage m) => m.Text.TrimStart().StartsWith('!');
    static bool UserEq(string a, string b) => b.Length > 0 && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    void Drop(RoomStats st, string reason, DateTime now) { lock (_stats) { st.Dropped[reason] = st.Dropped.GetValueOrDefault(reason) + 1; st.LastDropReason = reason; } }
    /// <summary>Downstream drops (command handler: cooldown, own message, bot, unknown command…) are recorded per room too.</summary>
    public void NoteDrop(string room, string reason) => Drop(Stats(room), reason, DateTime.Now);
    /// <summary>kind: queued | sent | confirmed | failed.</summary>
    public void NoteReply(string room, string kind, string result = "")
    {
        var st = Stats(room);
        lock (_stats)
        {
            switch (kind) { case "queued": st.RepliesQueued++; break; case "sent": st.RepliesSent++; break; case "confirmed": st.RepliesConfirmed++; break; default: st.RepliesFailed++; break; }
            if (result.Length > 0) st.LastReplyResult = System.Text.RegularExpressions.Regex.Replace(result, "\"[^\"]*\"", "\"…\"");
        }
    }
    public Dictionary<string, RoomStats> RoomStatsSnapshot() { lock (_stats) return _stats.ToDictionary(x => x.Key, x => x.Value); }
    /// <summary>JSON-able per-room diagnostics (room names are the user's own joined rooms; no message text).</summary>
    public object RoomDiagnostics() { lock (_stats) return _stats.ToDictionary(x => x.Key, x => (object)new { received = x.Value.Received, commandsSeen = x.Value.CommandsSeen, delivered = x.Value.Delivered, duplicates = x.Value.Duplicates,
        dropped = new Dictionary<string, int>(x.Value.Dropped), repliesQueued = x.Value.RepliesQueued, repliesSent = x.Value.RepliesSent, repliesConfirmed = x.Value.RepliesConfirmed, repliesFailed = x.Value.RepliesFailed,
        lastDropReason = x.Value.LastDropReason, lastReplyResult = x.Value.LastReplyResult }); }

    // ---- 2.8.1 own posts as seen by the tap (message(self) with its targetId) or the DOM: reply delivery confirmation ----
    readonly List<(string room, string text, DateTime at)> _selfPosts = new();
    public static string NormText(string t) { t = System.Text.RegularExpressions.Regex.Replace((t ?? "").Replace('\n', ' '), @"\s+", " ").Trim(); return t.Length > 250 ? t[..250].Trim() : t; }
    void NoteSelfPost(string room, string text, DateTime now)
    {
        lock (_selfPosts) { _selfPosts.Add((room, NormText(text), now)); if (_selfPosts.Count > 100) _selfPosts.RemoveAt(0); }
    }
    /// <summary>Where (if anywhere) our own post of <paramref name="text"/> was seen since <paramref name="since"/>: the room name,
    /// "" if seen in an unknown room, null if not seen.</summary>
    public string? SelfPostRoom(string text, DateTime since)
    {
        var t = NormText(text);
        lock (_selfPosts) { for (int i = _selfPosts.Count - 1; i >= 0; i--) { var p = _selfPosts[i]; if (p.at >= since && p.text == t) return p.room; } }
        return null;
    }

    // ---- viewers ----
    /// <summary>A tap viewer event: op = join | leave | snapshot.</summary>
    public void FromTapViewers(string op, IEnumerable<string> names, DateTime now)
    {
        List<string> snap;
        lock (_lk)
        {
            TapLastViewerEvent = now; TapLastEvent = now;
            var ns = names.Select(x => x.Trim().TrimStart('@')).Where(x => x.Length is > 0 and <= 64).ToList();
            if (op == "snapshot") { _tapViewers.Clear(); foreach (var n in ns) _tapViewers.Add(n); }
            else if (op == "join") foreach (var n in ns) _tapViewers.Add(n);
            else if (op == "leave") foreach (var n in ns) _tapViewers.Remove(n);
            else return;
            snap = _tapViewers.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        Viewers?.Invoke("LiveViewers", snap);
    }
    /// <summary>Tap viewer data counts as live for this long after its last event.</summary>
    public TimeSpan TapViewerFresh { get; set; } = TimeSpan.FromMinutes(10);
    public bool TapViewersLive(DateTime now) => now - TapLastViewerEvent < TapViewerFresh;
    /// <summary>A DOM viewer/member snapshot. While the tap delivers viewer events, the open "Viewing me" panel only re-seeds the tap's set.</summary>
    public void FromDomViewers(string source, List<string> names, DateTime now)
    {
        if (source == "LiveViewers" && TapViewersLive(now))
        {
            lock (_lk) { _tapViewers.Clear(); foreach (var n in names) _tapViewers.Add(n); }
        }
        Viewers?.Invoke(source, names);
    }
    public void SocketReconnected() { lock (_lk) TapReconnects++; }

    /// <summary>"Chat: live feed ✓" when the tap delivered something recently, else "DOM fallback".</summary>
    public string SourceStatus(DateTime now)
    {
        bool tap = TapSocketOpen && now - TapLastEvent < TimeSpan.FromMinutes(5) || now - TapLastEvent < TimeSpan.FromMinutes(2);
        var room = AllRooms ? "all rooms" : TargetRoom;
        var v = TapViewersLive(now) ? ", viewers live" : "";
        return tap ? $"Chat: live feed ✓ ({room}{v})" : $"Chat: DOM fallback — only the visible room (command room: {room})";
    }
}

/// <summary>Replies waiting for their room to be visible in the chat panel (default; no automatic room switching).</summary>
public sealed class ReplyQueue
{
    readonly object _lk = new();
    readonly List<(string text, string room, DateTime at)> _q = new();
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(5);
    public int Max { get; set; } = 20;
    public int Count { get { lock (_lk) return _q.Count; } }
    public void Enqueue(string text, string room, DateTime now)
    {
        lock (_lk) { _q.RemoveAll(x => now - x.at > MaxAge); if (_q.Count >= Max) _q.RemoveAt(0); _q.Add((text, room, now)); }
    }
    /// <summary>Takes the queued replies for <paramref name="visibleRoom"/> (oldest first); expired ones are dropped.</summary>
    public List<string> TakeFor(string visibleRoom, DateTime now)
    {
        lock (_lk)
        {
            _q.RemoveAll(x => now - x.at > MaxAge);
            var r = _q.Where(x => string.Equals(x.room.Trim(), visibleRoom.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var x in r) _q.Remove(x);
            return r.Select(x => x.text).ToList();
        }
    }
    public List<string> Rooms() { lock (_lk) return _q.Select(x => x.room).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
}
