using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EpikStreamer.Core;

namespace EpikStreamer;

/// <summary>v2.8 Engage: viewer points, rewards, mini-games, polls & predictions (logic in Core/Engage*.cs).
/// Replies obey the existing "reply in chat" opt-in and the chat panel's rate limit, batched per room and chunked to 250 chars.</summary>
public partial class MainWindow
{
    EngageEngine? _engage;
    readonly ReplyBatcher _engageReplies = new();
    readonly DispatcherTimer _engageTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    string _engageRoom = "";
    DateTime _engageLastSave = DateTime.Now, _engageLastUi;
    bool _engageLive;

    sealed class EngageHostImpl : IEngageHost
    {
        readonly MainWindow w; public EngageHostImpl(MainWindow w) { this.w = w; }
        public (bool Ok, string Reply) RequestSong(string user, string query)
        {
            var m = new ChatMessage(user, "!request " + query, null, false, false, w._engageRoom);
            bool ok = w.HandleRadioRequest(m) || w.HandleRequestCommand(m);   // those post their own confirmation
            return (ok, ok ? "" : "song requests are off right now");
        }
        public bool BallPit(string user, string command) => w._pit != null && w._cur.BallPit.Active && w._pit.Command(user, command, "", true);
        public bool PitActive => w._pit != null && w._cur.BallPit.Active && w._engine?.PitActive == true;
        public IReadOnlyList<string> PitNames() => w._pit?.Scene.Balls.Select(b => b.Name).Where(n => n.Length > 0).Distinct().ToList() ?? new List<string>();
        public void Alert(string text) => UI(() => w.ShowAlert(text));
        public void Sound(string path) { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) UI(() => EngagePlaySound(path)); }
        public void Race(RaceGame? race) => UI(() => w._pit?.Scene.StartRace(race));
    }

    void Init28()
    {
        var dir = Path.Combine(ProfileStore.Dir, "engage"); Directory.CreateDirectory(dir);
        PointsLedger ledger;
        try { ledger = new PointsLedger(Path.Combine(dir, "points.json")); }
        catch (Exception ex) { Log("⚠ Could not load the points ledger: " + ex.Message); ledger = new PointsLedger(); }
        _engage = new EngageEngine(_cur.Engage, ledger) { Host = new EngageHostImpl(this), DefaultRoom = () => _chat.Feed.TargetRoom };
        _engage.Log += t => UI(() => Log("🎮 " + t));
        _engage.Changed += () => _engageDirtyUi = true;
        _commands.ExtraTriggers = () => _engage?.Triggers() ?? Array.Empty<string>();
        _chat.ViewersReceived += (src, names) => UI(() =>
        {
            if (_engage == null || src != "LiveViewers" || !_engageLive) return;
            _engage.OnViewers(names.Select(n => (_chat.Feed.UserIdOf(n), n)), DateTime.Now);
        });
        if (_pit != null) _pit.Scene.Overlay = (c, w, h) =>
        {
            var e = _engage; if (e == null || !e.S.Enabled || !e.S.Overlay.Enabled) return;
            var v = e.View(DateTime.Now); if (!v.Empty) EngageOverlay.Draw(c, w, h, v, e.S.Overlay);
        };
        _engageTimer.Tick += (_, _) => EngageTick();
        _engageTimer.Start();
        Closed += (_, _) => { try { _engage?.Ledger.SaveIfDirty(); } catch { } };
        BuildEngageUi();
    }
    bool _engageDirtyUi = true;

    /// <summary>Runs first in the chat pipeline; true if Engage consumed the message (a command, a vote or a trivia answer).</summary>
    bool HandleEngage(ChatMessage m)
    {
        var e = _engage; if (e == null || !e.S.Enabled) return false;
        if (m.UserId.Length == 0) { var id = _chat.Feed.UserIdOf(m.User); if (id.Length > 0) m = m with { UserId = id }; }
        e.SelfName = _store.Chat.MyUsername.Length > 0 ? _store.Chat.MyUsername : _chat.Feed.Self;
        e.SelfId = _chat.Feed.SelfId;
        _engageRoom = m.Room;
        var o = e.Handle(m, DateTime.Now);
        Deliver(o);
        return o.Handled;
    }

    void Deliver(EngageOutput o) { foreach (var (room, text) in o.Replies) _engageReplies.Add(room, text); }

    void EngageTick()
    {
        var e = _engage; if (e == null) return;
        var now = DateTime.Now;
        if (!ReferenceEquals(e.S, _cur.Engage)) e.Apply(_cur.Engage);
        bool live = _engine?.IsRunning == true;
        if (live && !_engageLive) { e.StartStream(now); _engageLive = true; }
        else if (!live && _engageLive) { e.EndStream(); _engageLive = false; }
        Deliver(e.Tick(now));
        foreach (var (room, text) in _engageReplies.Flush())
        {
            if (_store.Chat.ReplyInChat) _ = ReplyAsync(text, room);
            else if (live) _engine!.ShowChatResponse(EngageOverlay.Strip(text), _store.Chat.OverlaySeconds);
            if (_store.Chat.DebugLog || !_store.Chat.ReplyInChat) Log("🎮 → " + text);
        }
        if (live && e.S.Enabled && e.S.Overlay.Enabled && _engine is { PitActive: false }) _engine.SetEngageText(EngageOverlay.Text(e.View(now), e.S.Overlay));
        else if (live && _engine is { PitActive: false }) _engine.SetEngageText("");
        if ((now - _engageLastSave).TotalSeconds >= 60) { _engageLastSave = now; e.Ledger.SaveIfDirty(); }
        if (IsVisible && Tabs.SelectedItem == EngageTab && (_engageDirtyUi || (now - _engageLastUi).TotalSeconds >= 3)) { _engageDirtyUi = false; _engageLastUi = now; RefreshEngageUi(); }
    }

    // ================= UI (built in code so both apps share it) =================
    TextBox _ptSearch = new(), _ptBalance = new(), _pollQ = new(), _pollOpts = new(), _pollSecs = new(), _predQ = new(), _predOpts = new(), _predWinner = new();
    ListBox _ptList = new(), _rwList = new(), _pendList = new(), _pollHist = new();
    TextBlock _ptStatus = new(), _pollStatus = new(), _gameStatus = new();
    StackPanel _rwEditor = new();

    static TextBlock H(string t) => new() { Text = t, FontSize = 15, Margin = new Thickness(0, 10, 0, 4) };
    static TextBlock Note(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 6) };
    static Button Btn(string t, Action a) { var b = new Button { Content = t, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3, 10, 3) }; b.Click += (_, _) => a(); return b; }
    static StackPanel Row(params Control[] cs) { var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) }; foreach (var c in cs) p.Children.Add(c); return p; }
    static TextBox Box(TextBox b, double w, string tip = "") { b.Width = w; b.Margin = new Thickness(0, 0, 6, 6); if (tip.Length > 0) ToolTip.SetTip(b, tip); return b; }
    static ScrollViewer Scroll(Control c) => new() { Content = c, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    /// <summary>A label + editor for each simple property (bool/number/string/enum); edits apply live and persist.</summary>
    Panel PropEditor(object obj, params string[] skip)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var pi in obj.GetType().GetProperties().Where(p => p.CanWrite && p.CanRead && !skip.Contains(p.Name)))
        {
            var t = pi.PropertyType; Control? ed = null;
            if (t == typeof(bool))
            {
                var cb = new CheckBox { Content = Nice(pi.Name), IsChecked = (bool)pi.GetValue(obj)!, Margin = new Thickness(0, 0, 14, 6) };
                cb.Click += (_, _) => { pi.SetValue(obj, cb.IsChecked == true); Persist(); };
                wrap.Children.Add(cb); continue;
            }
            if (t.IsEnum)
            {
                var co = new ComboBox { Width = 130 }; foreach (var v in Enum.GetValues(t)) co.Items.Add(v); co.SelectedItem = pi.GetValue(obj);
                co.SelectionChanged += (_, _) => { if (co.SelectedItem != null) { pi.SetValue(obj, co.SelectedItem); Persist(); } };
                ed = co;
            }
            else if (t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(string))
            {
                var tb = new TextBox { Width = t == typeof(string) ? 220 : 70, Text = Convert.ToString(pi.GetValue(obj), System.Globalization.CultureInfo.InvariantCulture) ?? "" };
                tb.TextChanged += (_, _) =>
                {
                    var s = tb.Text ?? ""; var ci = System.Globalization.CultureInfo.InvariantCulture;
                    try
                    {
                        if (t == typeof(string)) pi.SetValue(obj, s);
                        else if (t == typeof(int) && int.TryParse(s, System.Globalization.NumberStyles.Integer, ci, out var i) && i >= 0) pi.SetValue(obj, i);
                        else if (t == typeof(long) && long.TryParse(s, System.Globalization.NumberStyles.Integer, ci, out var l) && l >= 0) pi.SetValue(obj, l);
                        else if (t == typeof(double) && double.TryParse(s, System.Globalization.NumberStyles.Float, ci, out var d) && d >= 0) pi.SetValue(obj, d);
                        else return;
                        Persist();
                    }
                    catch { }
                };
                ed = tb;
            }
            if (ed == null) continue;
            ed.Margin = new Thickness(4, 0, 14, 6);
            wrap.Children.Add(Row(new TextBlock { Text = Nice(pi.Name), VerticalAlignment = VerticalAlignment.Center }, ed));
        }
        return wrap;
    }
    static string Nice(string n) => System.Text.RegularExpressions.Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ");

    void BuildEngageUi()
    {
        var s = _cur.Engage;
        var tabs = new TabControl();
        // ---- Points ----
        var pts = new StackPanel { Margin = new Thickness(10) };
        pts.Children.Add(Note("Viewers earn points per minute watching while you're live (from the viewers list), for chatting (with cooldown / no repeats / per-minute cap), for chatting first and for streak days. Stored per EpikChat user id in " + Path.Combine(ProfileStore.Dir, "engage", "points.json") + " (daily backups)."));
        pts.Children.Add(PropEditor(s, nameof(EngageSettings.Rewards), nameof(EngageSettings.Games), nameof(EngageSettings.Polls), nameof(EngageSettings.Overlay)));
        pts.Children.Add(H("Viewers"));
        _ptSearch.TextChanged += (_, _) => RefreshEngageUi();
        pts.Children.Add(Row(Box(_ptSearch, 200, "search by name or id"), Box(_ptBalance, 90, "new balance"),
            Btn("Set balance", () => EditSelected(u => { if (long.TryParse(_ptBalance.Text, out var b) && b >= 0) _engage!.Ledger.Set(u.Key, b); })),
            Btn("Remove", () => EditSelected(u => _engage!.Ledger.Remove(u.Key))),
            Btn("Reset all…", () => { if (MessageBox.Show(this, "Reset every viewer's points to 0?", "Engage", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { _engage!.Ledger.ResetAll(); _engage.Ledger.SaveIfDirty(); RefreshEngageUi(); } }),
            Btn("Export CSV…", ExportPoints), Btn("Import CSV…", ImportPoints)));
        _ptList.Height = 260; pts.Children.Add(_ptList); pts.Children.Add(_ptStatus);
        tabs.Items.Add(new TabItem { Header = "Points", Content = Scroll(pts) });
        // ---- Rewards ----
        var rw = new StackPanel { Margin = new Thickness(10) };
        rw.Children.Add(Note("!redeem <name> [text]. Kinds: Song (Arg unused; viewer's text is the request, through your request queue), BallPit (Arg = bigball / shake…), Alert (Arg = alert text, {user} {text}), Sound (Arg = sound file)."));
        _rwList.Height = 140; _rwList.SelectionChanged += (_, _) => ShowRewardEditor();
        rw.Children.Add(_rwList);
        rw.Children.Add(Row(Btn("Add reward", () => { s.Rewards.Add(new Reward { Name = "new" + (s.Rewards.Count + 1), Title = "New reward", Kind = "Alert", Arg = "{user} redeemed!", Cost = 100 }); Persist(); RefreshRewards(); }),
            Btn("Delete reward", () => { if (_rwList.SelectedItem is ListBoxItem { Tag: Reward r }) { s.Rewards.Remove(r); Persist(); RefreshRewards(); } })));
        rw.Children.Add(_rwEditor);
        rw.Children.Add(H("Pending approvals"));
        _pendList.Height = 110; rw.Children.Add(_pendList);
        rw.Children.Add(Row(Btn("Approve", () => { if (_pendList.SelectedItem is ListBoxItem { Tag: int id }) Deliver(_engage!.Approve(id, DateTime.Now)); RefreshEngageUi(); }),
            Btn("Deny (refund)", () => { if (_pendList.SelectedItem is ListBoxItem { Tag: int id }) Deliver(_engage!.Deny(id)); RefreshEngageUi(); })));
        tabs.Items.Add(new TabItem { Header = "Rewards", Content = Scroll(rw) });
        // ---- Games ----
        var gm = new StackPanel { Margin = new Thickness(10) };
        gm.Children.Add(Note("!gamble <amount|all|50%>, !slots <amount>, !duel @user <amount> → !accept, trivia (answer in chat), !heist <amount> (odds grow with crew), Ball Pit race: !bet <racer> <amount> while betting is open. Per-user cooldown and a global rate limit apply; you (the host) and the excluded list never play or earn."));
        gm.Children.Add(PropEditor(s.Games));
        gm.Children.Add(Row(Btn("Start trivia now", () => Deliver(_engage!.StartTrivia(_chat.Feed.TargetRoom, DateTime.Now))),
            Btn("Reload trivia pack", () => _engage!.ReloadTrivia()),
            Btn("Start Ball Pit race", () => Deliver(_engage!.StartRace(_chat.Feed.TargetRoom, DateTime.Now)))));
        var packs = new TextBox { Width = 520, Text = string.Join("; ", s.Games.TriviaPacks) };
        packs.TextChanged += (_, _) => { s.Games.TriviaPacks = (packs.Text ?? "").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList(); _engage?.ReloadTrivia(); Persist(); };
        gm.Children.Add(Row(new TextBlock { Text = "Trivia packs (JSON files, ; separated)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, packs,
            Btn("Add…", () => { var d = new OpenFileDialog { Filter = "JSON|*.json" }; if (d.ShowDialog(this) == true) packs.Text = string.Join("; ", s.Games.TriviaPacks.Append(d.FileName)); })));
        gm.Children.Add(Note("Pack format: [{\"q\": \"Question?\", \"a\": [\"answer\", \"alt answer\"], \"category\": \"music\"}] (or {\"questions\": [...]})."));
        gm.Children.Add(_gameStatus);
        gm.Children.Add(H("On-stream overlay"));
        gm.Children.Add(PropEditor(s.Overlay));
        tabs.Items.Add(new TabItem { Header = "Games", Content = Scroll(gm) });
        // ---- Polls ----
        var pl = new StackPanel { Margin = new Thickness(10) };
        pl.Children.Add(Note("Chat: !poll \"Question\" a | b | c [seconds] (you/mods), votes with !vote N (or a bare number / option text if enabled). One vote per viewer, changeable. Predictions: !predict <outcome#> <points>; you lock and resolve; winners split the pool (parimutuel)."));
        pl.Children.Add(PropEditor(s.Polls));
        pl.Children.Add(H("Poll"));
        _pollSecs.Text = s.Polls.DefaultSeconds.ToString();
        pl.Children.Add(Row(Box(_pollQ, 260, "question"), Box(_pollOpts, 260, "options separated by |"), Box(_pollSecs, 60, "seconds")));
        pl.Children.Add(Row(Btn("Start poll", () =>
        {
            var opts = (_pollOpts.Text ?? "").Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (string.IsNullOrWhiteSpace(_pollQ.Text) || opts.Count < 2) { _pollStatus.Text = "Enter a question and at least 2 options (a | b)."; return; }
            Deliver(_engage!.StartPoll(_pollQ.Text!.Trim(), opts, int.TryParse(_pollSecs.Text, out var sec) ? sec : s.Polls.DefaultSeconds, _chat.Feed.TargetRoom, DateTime.Now));
        }), Btn("End poll now", () => Deliver(_engage!.EndPoll(DateTime.Now)))));
        pl.Children.Add(H("Prediction"));
        pl.Children.Add(Row(Box(_predQ, 260, "question"), Box(_predOpts, 260, "outcomes separated by |")));
        pl.Children.Add(Row(Btn("Open", () =>
        {
            var opts = (_predOpts.Text ?? "").Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (string.IsNullOrWhiteSpace(_predQ.Text) || opts.Count < 2) { _pollStatus.Text = "Enter a question and at least 2 outcomes."; return; }
            Deliver(_engage!.OpenPrediction(_predQ.Text!.Trim(), opts, _chat.Feed.TargetRoom, DateTime.Now));
        }), Btn("Lock", () => Deliver(_engage!.LockPrediction())), Box(_predWinner, 40, "winning outcome #"),
            Btn("Resolve", () => { if (int.TryParse(_predWinner.Text, out var wn)) Deliver(_engage!.ResolvePrediction(wn, DateTime.Now)); }),
            Btn("Cancel (refund)", () => Deliver(_engage!.CancelPrediction()))));
        pl.Children.Add(_pollStatus);
        pl.Children.Add(H("History"));
        _pollHist.Height = 150; pl.Children.Add(_pollHist);
        tabs.Items.Add(new TabItem { Header = "Polls", Content = Scroll(pl) });
        EngageHost.Content = tabs;
        RefreshRewards(); RefreshEngageUi();
    }
    void RebuildEngageUiForProfile() { if (_engage == null) return; _engage.Apply(_cur.Engage); BuildEngageUi(); }

    void EditSelected(Action<PointsUser> a)
    {
        if (_ptList.SelectedItem is not ListBoxItem { Tag: PointsUser u }) { _ptStatus.Text = "Select a viewer first."; return; }
        a(u); _engage!.Ledger.SaveIfDirty(); RefreshEngageUi();
    }
    void ExportPoints()
    {
        var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"epik-points-{DateTime.Now:yyyyMMdd}.csv" };
        if (d.ShowDialog(this) != true) return;
        File.WriteAllText(d.FileName, _engage!.Ledger.ExportCsv()); _ptStatus.Text = "Exported to " + d.FileName;
    }
    void ImportPoints()
    {
        var d = new OpenFileDialog { Filter = "CSV|*.csv" }; if (d.ShowDialog(this) != true) return;
        var r = MessageBox.Show(this, "Replace existing balances with the file's values?\n\nYes = replace, No = add to existing balances.", "Import points", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return;
        try { var n = _engage!.Ledger.ImportCsv(File.ReadAllText(d.FileName), r == MessageBoxResult.Yes); _engage.Ledger.SaveIfDirty(); _ptStatus.Text = $"Imported {n} rows."; }
        catch (Exception ex) { _ptStatus.Text = "Import failed: " + ex.Message; }
        RefreshEngageUi();
    }
    void RefreshRewards()
    {
        _rwList.Items.Clear();
        foreach (var r in _cur.Engage.Rewards) _rwList.Items.Add(new ListBoxItem { Content = $"{(r.Enabled ? "✔" : "✖")} {r.Name} — {r.Title} ({r.Kind}) · {r.Cost} · {r.CooldownSeconds}s{(r.RequireApproval ? " · approval" : "")}", Tag = r });
        _rwEditor.Children.Clear();
    }
    void ShowRewardEditor()
    {
        _rwEditor.Children.Clear();
        if (_rwList.SelectedItem is ListBoxItem { Tag: Reward r }) { _rwEditor.Children.Add(H("Edit: " + r.Name)); _rwEditor.Children.Add(PropEditor(r)); _rwEditor.Children.Add(Btn("Done", RefreshRewards)); }
    }
    void RefreshEngageUi()
    {
        var e = _engage; if (e == null) return;
        var q = (_ptSearch.Text ?? "").Trim();
        var users = e.Ledger.Snapshot().Where(u => q.Length == 0 || u.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || u.Id.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(500).ToList();
        var sel = (_ptList.SelectedItem as ListBoxItem)?.Tag is PointsUser su ? su.Key : null;
        _ptList.Items.Clear();
        foreach (var u in users) { var it = new ListBoxItem { Content = $"{u.Name}  ·  {u.Points:N0}  (lifetime {u.Lifetime:N0}, streak {u.Streak}){(u.Id.Length > 0 ? "  · id " + u.Id : "")}", Tag = u }; _ptList.Items.Add(it); if (u.Key == sel) _ptList.SelectedItem = it; }
        _ptStatus.Text = $"{e.Ledger.Count} viewers · {e.S.CurrencyName}" + (_engageLive ? " · earning (live)" : " · not live (no watch-time points)");
        _pendList.Items.Clear(); foreach (var p in e.Pending()) _pendList.Items.Add(new ListBoxItem { Content = p.ToString(), Tag = p.Id });
        _pollHist.Items.Clear(); foreach (var h in e.PollHistory.AsEnumerable().Reverse()) _pollHist.Items.Add(h);
        var poll = e.CurrentPoll; var pred = e.CurrentPrediction;
        _pollStatus.Text = (poll != null ? "Poll: " + poll.ResultText() : "No poll running.") + (pred != null ? $"   Prediction: {pred.Question} — {(pred.Locked ? "locked" : "open")}, pool {pred.Pool}" : "");
        _gameStatus.Text = e.RaceRunning ? "Race in progress." : (_pit != null && _cur.BallPit.Active ? "Ball Pit is on: races available." : "Races need the Ball Pit (Source/Overlay mode) live.");
    }
}

public partial class MainWindow
{
    static void EngagePlaySound(string path) => PlaySound(path);
}
