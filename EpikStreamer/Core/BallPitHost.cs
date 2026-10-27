using System.IO;
namespace EpikStreamer.Core;

/// <summary>v2.5 Ball Pit glue shared by the Windows and Linux windows: one scene + roster + avatar cache, fed by the chat
/// panel (member list, LIVE viewers, chat authors, chat commands) and drawn either by the stream feed or the offline preview.</summary>
public sealed class BallPitHost : IDisposable
{
    public BallPitScene Scene { get; }
    public BallPitRoster Roster { get; }
    public AvatarCache Avatars { get; }
    public string MyName { get; set; } = "";
    public event Action<string>? Log;
    BallPitRenderer? _preview; DateTime _lastStep = DateTime.UtcNow;
    readonly HashSet<string> _tests = new(StringComparer.OrdinalIgnoreCase);

    public BallPitHost(BallPitSettings s, string cacheDir, Func<string, Task<byte[]?>>? pageFetch = null)
    {
        Scene = new BallPitScene(s);
        Roster = new BallPitRoster(s);
        Avatars = new AvatarCache(cacheDir) { PageFetch = pageFetch };
        Scene.TextureFor = url => Avatars.Get(url);
        Avatars.Ready += (url, tex) => Scene.AvatarReady(url, tex);
        Roster.OnJoin = m => { Scene.Join(m.Name, m.Avatar, m.Color, announce: Scene.Settings.JoinEvents, viewer: Roster.IsViewer(m.Name)); };
        Roster.OnLeave = n => Scene.Leave(n);
    }
    bool IsMe(string n) => MyName.Length > 0 && string.Equals(n.Trim(), MyName.Trim(), StringComparison.OrdinalIgnoreCase);

    public void Apply(BallPitSettings s) { s.Migrate(); Scene.Settings = s; Roster.Settings = s; }
    public int People => Scene.Count + Scene.Waiting;

    public void OnChat(ChatMessage m)
    {
        if (m.IsBot || string.IsNullOrWhiteSpace(m.User)) return;
        Roster.Chatter(m.User, DateTime.Now);
        if (Scene.Settings.ChatBubbles && !m.Text.TrimStart().StartsWith('!')) Scene.Chat(m.User, m.Text);
    }
    public void OnViewers(IEnumerable<string> names) => Roster.Viewers(names, DateTime.Now);
    public void OnMembers(List<BallPitRoster.Member> list, bool complete, int total) => Roster.Members(list, complete, DateTime.Now, total > 0 ? total : null);
    public void Tick() => Roster.Tick(DateTime.Now);

    /// <summary>Chat command hook (ChatCommands.BallPit).</summary>
    public bool Command(string user, string cmd, string arg, bool host)
    {
        var r = Scene.Command(user, cmd, arg, host);
        if (r != null) Log?.Invoke($"⚽ {user}: {cmd} {arg}".TrimEnd());
        return r != null;
    }

    public void AddTestUsers(int n)
    {
        var before = Scene.Names().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Scene.AddTestUsers(n);
        foreach (var x in Scene.Names()) if (!before.Contains(x)) _tests.Add(x);
    }
    public void ClearTestUsers() { foreach (var n in _tests) Scene.Leave(n); _tests.Clear(); }

    /// <summary>Offline preview (when not streaming): steps the scene by wall time and renders at w×h. Returns premultiplied BGRA.</summary>
    public byte[] RenderPreview(int w, int h, bool transparent)
    {
        var now = DateTime.UtcNow; var dt = Math.Clamp((now - _lastStep).TotalSeconds, 0, 0.1); _lastStep = now;
        Scene.Step(dt);
        if (_preview == null || _preview.Width != w || _preview.Height != h) { _preview?.Dispose(); _preview = new BallPitRenderer(w, h); }
        _preview.Transparent = transparent;
        _preview.Render(Scene);
        return _preview.Bytes.ToArray();
    }
    public void Dispose() { _preview?.Dispose(); }
}
