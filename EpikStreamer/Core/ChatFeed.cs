namespace EpikStreamer.Core;

/// <summary>v2.7 chat feed settings (Settings → Chat).</summary>
public class ChatFeedSettings
{
    /// <summary>Mirror EpikChat's own realtime connection (passive, read-only) so commands and viewers work whatever tab/panel is visible.</summary>
    public bool LiveFeed { get; set; } = true;
    /// <summary>"" = the room you stream to (auto-detected), "*" = all joined rooms, otherwise a room name.</summary>
    public string CommandRoom { get; set; } = AllRooms;
    /// <summary>Reply in the command's room by briefly switching EpikChat to it (sidebar room button → composer → back). On by default
    /// since 2.7.1, but only done while the 💬 Chat panel isn't on screen (no visible disruption) unless <see cref="SwitchWhileVisible"/>;
    /// otherwise the reply waits until that room is visible or the panel is hidden.</summary>
    public bool AllowRoomSwitchToReply { get; set; } = true;
    public bool SwitchWhileVisible { get; set; }
    /// <summary>The first-run "Reply to chat commands in chat?" question was answered.</summary>
    public bool ReplyPromptShown { get; set; }
    /// <summary>Settings migration marker (0 = written by ≤2.7.0).</summary>
    public int DefaultsVersion { get; set; }
    /// <summary>2.7.1 defaults: all joined rooms, room switching on. Applied once to settings saved by 2.7.0 (whose blank = auto room).</summary>
    public bool Migrate()
    {
        if (DefaultsVersion >= 271) return false;
        if (CommandRoom.Length == 0) CommandRoom = AllRooms;
        AllowRoomSwitchToReply = true; DefaultsVersion = 271; return true;
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
    public string TargetRoom => S.CommandRoom is { Length: > 0 } c ? c : (StreamRoom.Length > 0 ? StreamRoom : VisibleRoom);
    public bool AllRooms => S.CommandRoom == ChatFeedSettings.AllRooms;

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
    readonly Dictionary<string, (DateTime at, string src)> _keys = new();
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

    static bool Eq(string a, string b) => a.Length > 0 && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    string Key(ChatMessage m) => m.User.Trim().ToLowerInvariant() + "\n" + m.Text.Trim();

    bool Seen(string? id) => id != null && _ids.Contains(id);
    void Remember(string? id) { if (id == null || !_ids.Add(id)) return; _idQ.Enqueue(id); if (_idQ.Count > 5000) _ids.Remove(_idQ.Dequeue()); }
    bool CrossDup(ChatMessage m, string src, DateTime now)
    {
        var k = Key(m);
        if (_keys.TryGetValue(k, out var e) && e.src != src && now - e.at < CrossSourceWindow) return true;
        _keys[k] = (now, src);
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
            if (Seen(m.Id) || CrossDup(m, "dom", now)) { Duplicates++; Remember(m.Id); }
            else
            {
                Remember(m.Id);
                if (!AllRooms && TargetRoom.Length > 0 && m.Room.Length > 0 && !Eq(m.Room, TargetRoom)) OtherRoom++;
                else { FromDom++; emit = m; }
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
            if (Self.Length > 0 && Eq(m.User, Self) || SelfDisplay.Length > 0 && Eq(m.User, SelfDisplay) || SelfId.Length > 0 && m.UserId == SelfId) m = m with { Self = true };
            if (Seen(m.Id) || CrossDup(m, "tap", now)) { Duplicates++; Remember(m.Id); }
            else
            {
                Remember(m.Id);
                var room = m.Room.Length > 0 ? m.Room : (roomId.Length > 0 && _roomById.TryGetValue(roomId, out var n) ? n : "");
                if (room.Length > 0) m = m with { Room = room };
                if ((AllRooms && (room.Length > 0 || roomId.Length == 0)) || (!AllRooms && (TargetRoom.Length == 0 || Eq(room, TargetRoom) || Eq(roomId, TargetRoom)))) { FromTap++; emit = m; }
                else if (room.Length > 0) OtherRoom++;
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
                if (name.Length > 0) { if (AllRooms || Eq(name, TargetRoom)) { FromTap++; emit.Add(m with { Room = name }); } else OtherRoom++; _pending.RemoveAt(i--); }
                else if (now - at > PendingWait) { if (AllRooms) { FromTap++; emit.Add(m); } else Unmatched++; _pending.RemoveAt(i--); }
            }
        }
        foreach (var m in emit) Message?.Invoke(m);
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
        var room = AllRooms ? "all rooms" : (TargetRoom.Length > 0 ? TargetRoom : "room not detected yet");
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
