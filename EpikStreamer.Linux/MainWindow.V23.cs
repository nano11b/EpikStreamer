using System.Diagnostics;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EpikStreamer.Core;
namespace EpikStreamer;

/// <summary>v2.3: media library, saved playlists, song requests, now-playing card settings, viewer list.</summary>
public partial class MainWindow
{
    LibraryStore _library = new();
    SongRequestQueue _requests = new(new RequestSettings());
    ViewerTracker _viewers = new(new ViewerSettings());
    CancellationTokenSource? _scanCts;
    readonly DispatcherTimer _v23Timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _libQueryTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    Point _dragStart;

    void Init23()
    {
        try { _library = LibraryStore.Load(); } catch (Exception ex) { Log("Could not load the library: " + ex.Message); }
        _requests = new SongRequestQueue(_store.Requests);
        _viewers = new ViewerTracker(_store.Viewers);
        foreach (var s in LibraryStore.Sorts) LibSort.Items.Add(s);
        foreach (var f in LibraryStore.Filters) LibFilter.Items.Add(f);
        _loading = true; LibSort.SelectedIndex = 0; LibFilter.SelectedIndex = 0; _loading = false;
        LoadLibrarySettings(); LoadRequestSettings(); LoadViewerSettings();
        LibBox.ItemTemplate = ListTemplate();
        RefreshLibrary(); RefreshPlaylists();
        _requests.Changed += () => Dispatcher.UIThread.BeginInvoke(RefreshRequests);
        _viewers.Joined += v => Dispatcher.UIThread.BeginInvoke(() => OnViewerJoined(v));
        _viewers.Left += v => Dispatcher.UIThread.BeginInvoke(() => { if (_store.Chat.DebugLog) Log($"👤 {v.Name} left"); });
        _chat.ViewersReceived += (src, names) => Dispatcher.UIThread.BeginInvoke(() => OnViewerNames(src, names));
        _libQueryTimer.Tick += (_, _) => { _libQueryTimer.Stop(); RefreshLibrary(); };
        _v23Timer.Tick += (_, _) => { _viewers.Tick(DateTime.Now); RefreshViewers(); RefreshNowText(); };
        _v23Timer.Start();
        RefreshRequests();
        Init25(); Init26(); Init28(); Init282();
        Closed += (_, _) => { _scanCts?.Cancel(); try { _library.Save(); } catch { } };
    }

    /// <summary>Called right after the engine is created (before Start).</summary>
    void HookEngine23(StreamEngine eng)
    {
        eng.Requests = _store.Requests.Enabled ? _requests : null;
        eng.Library = path => _library.Find(path);
        eng.TrackChanged += path => Dispatcher.UIThread.BeginInvoke(() =>
        {
            _library.MarkPlayed(path);
            RefreshRequests(); RefreshNowText();
        });
    }

    void EndSession23() { try { _session?.SetViewers(_viewers.Snapshot(), DateTime.Now); _library.Save(); } catch { } }

    // ---------- now-playing card (per profile, Overlays tab) ----------
    void LoadNowPlaying(Profile p)
    {
        var n = p.NowPlaying;
        NpEnable.IsChecked = n.Enabled; NpMode.SelectedIndex = n.Mode == "Always" ? 1 : 0; NpSeconds.Text = n.ShowSeconds.ToString();
        NpCorner.SelectedItem = n.Corner; NpWidth.Value = n.WidthFraction * 100; NpBg.Text = n.Background; NpOpacity.Value = n.Opacity * 100;
        NpText.Text = n.TextColor; NpAccent.Text = n.AccentColor; NpThumb.IsChecked = n.ShowThumbnail; NpProgress.IsChecked = n.ShowProgress;
        NpNext.IsChecked = n.ShowUpNext; NpReq.IsChecked = n.ShowRequester; NpLabel.Text = n.Label;
    }
    void SaveNowPlaying(Profile p)
    {
        var n = p.NowPlaying;
        n.Enabled = NpEnable.IsChecked == true; n.Mode = NpMode.SelectedIndex == 1 ? "Always" : "Auto"; n.ShowSeconds = Int(NpSeconds.Text, 12, 2, 3600);
        n.Corner = (Corner)(NpCorner.SelectedItem ?? Corner.BottomLeft); n.WidthFraction = NpWidth.Value / 100.0;
        n.Background = Color(NpBg.Text, "#101018"); n.Opacity = NpOpacity.Value / 100.0; n.TextColor = Color(NpText.Text, "#FFFFFF");
        n.AccentColor = Color(NpAccent.Text, "#A855F7"); n.ShowThumbnail = NpThumb.IsChecked == true; n.ShowProgress = NpProgress.IsChecked == true;
        n.ShowUpNext = NpNext.IsChecked == true; n.ShowRequester = NpReq.IsChecked == true; n.Label = NpLabel.Text.Trim();
    }
    static string Color(string t, string def) => System.Text.RegularExpressions.Regex.IsMatch(t.Trim(), "^#[0-9A-Fa-f]{6}$|^[A-Za-z]{3,20}$") ? t.Trim() : def;
    void NpShow_Click(object? s, RoutedEventArgs e)
    {
        if (_engine?.Card is { } c) c.ShowNow();
        else MessageBox.Show(this, "The card shows while streaming with \"Show a now-playing card\" turned on (the setting applies when the stream starts).");
    }

    // ---------- library ----------
    void LoadLibrarySettings()
    {
        var s = _library.Settings;
        LibFolders.Items.Clear(); foreach (var f in s.Folders) LibFolders.Items.Add(f);
        LibSubfolders.IsChecked = s.Subfolders; LibThumbs.IsChecked = s.Thumbnails; LibExt.Text = s.Extensions;
    }
    void SaveLibrarySettings()
    {
        var s = _library.Settings;
        s.Folders = LibFolders.Items.Cast<string>().ToList(); s.Subfolders = LibSubfolders.IsChecked == true; s.Thumbnails = LibThumbs.IsChecked == true;
        if (LibExt.Text.Trim().Length > 0) s.Extensions = LibExt.Text.Trim();
        try { _library.Save(); } catch (Exception ex) { Log("Could not save the library: " + ex.Message); }
    }
    void LibAddFolder_Click(object? s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Watch a media folder", Multiselect = true };
        if (d.ShowDialog(this) != true) return;
        foreach (var f in d.FolderNames) if (!LibFolders.Items.Cast<string>().Contains(f, StringComparer.OrdinalIgnoreCase)) LibFolders.Items.Add(f);
        SaveLibrarySettings();
    }
    void LibRemoveFolder_Click(object? s, RoutedEventArgs e) { if (LibFolders.SelectedItem is string f) { LibFolders.Items.Remove(f); SaveLibrarySettings(); } }

    async void LibScan_Click(object? s, RoutedEventArgs e)
    {
        if (_scanCts != null) { _scanCts.Cancel(); return; }
        SaveLibrarySettings();
        if (_library.Settings.Folders.Count == 0) { MessageBox.Show(this, "Add a folder to watch first."); return; }
        var ff = await RequireFfmpeg(); if (ff == null) return;
        var probe = FfmpegLocator.FindProbe(ff);
        if (probe == null) { MessageBox.Show(this, "ffprobe wasn't found next to ffmpeg (" + ff + "). The bundled/downloaded builds include it."); return; }
        _scanCts = new CancellationTokenSource(); LibScanBtn.Content = "Cancel scan";
        var prog = new Progress<string>(t => LibStatus.Text = t);
        try
        {
            var (total, probed, failed) = await LibraryScanner.ScanAsync(_library, probe, _library.Settings.Thumbnails ? ff : null, prog, _scanCts.Token);
            LibStatus.Text = $"{total} items ({probed} new/changed, {failed} unreadable).";
        }
        catch (OperationCanceledException) { LibStatus.Text = "Scan cancelled (finished items were kept)."; }
        catch (Exception ex) { LibStatus.Text = "Scan failed: " + ex.Message; }
        finally { _scanCts = null; LibScanBtn.Content = "Scan now"; try { _library.Save(); } catch { } RefreshLibrary(); RefreshPlaylists(); }
    }

    void LibQuery_Changed(object? s, RoutedEventArgs e) { if (_loading) return; _libQueryTimer.Stop(); _libQueryTimer.Start(); }
    void RefreshLibrary()
    {
        var all = _library.Snapshot();
        var list = LibraryStore.Query(all, LibSearch.Text, LibSort.SelectedItem as string ?? "Title", LibFilter.SelectedItem as string ?? "All").ToList();
        LibBox.ItemsSource = list;
        LibCount.Text = list.Count == all.Count ? $"{all.Count} items" : $"{list.Count} of {all.Count}";
        if (all.Count == 0 && string.IsNullOrEmpty(LibStatus.Text)) LibStatus.Text = "Add folders and press Scan.";
    }
    void LibView_Click(object? s, RoutedEventArgs e)
    {
        bool grid = LibGrid.IsChecked == true;
        LibBox.ItemsPanel = grid ? new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new WrapPanel()) : new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        LibBox.ItemTemplate = grid ? GridTemplate() : ListTemplate();
    }
    static readonly Dictionary<string, Bitmap?> ThumbCache = new();
    static Bitmap? LoadThumb(string? f)
    {
        if (string.IsNullOrEmpty(f) || !File.Exists(f)) return null;
        lock (ThumbCache)
        {
            if (ThumbCache.TryGetValue(f, out var b)) return b;
            try { using var st = File.OpenRead(f); b = Bitmap.DecodeToWidth(st, 160); } catch { b = null; }
            if (ThumbCache.Count > 2000) ThumbCache.Clear();
            return ThumbCache[f] = b;
        }
    }
    static Control Thumb(LibraryItem it, double w, double h) => new Image { Width = w, Height = h, Stretch = Stretch.UniformToFill, Source = LoadThumb(it.Thumb) };
    static Control Text(string? text, bool dim = false, double? width = null)
    {
        var t = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        if (dim) t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("Dim"));
        if (width is double w) t.Width = w;
        return t;
    }
    static Avalonia.Controls.Templates.IDataTemplate ListTemplate() => new Avalonia.Controls.Templates.FuncDataTemplate<LibraryItem>((it, _) => it == null ? new Panel() :
        new StackPanel { Orientation = Orientation.Horizontal, Children = { Thumb(it, 64, 36), Text(it.Display, false, 380), Text(it.DurationText, true, 60), Text(it.Resolution, true, 50), Text(it.Folder, true) } });
    static Avalonia.Controls.Templates.IDataTemplate GridTemplate() => new Avalonia.Controls.Templates.FuncDataTemplate<LibraryItem>((it, _) => it == null ? new Panel() :
        new StackPanel { Width = 168, Margin = new Thickness(4), Children = { Thumb(it, 160, 90), Text(it.Display),
            new StackPanel { Orientation = Orientation.Horizontal, Children = { Text(it.DurationText, true), Text(it.Resolution, true) } } } });
    List<LibraryItem> SelectedLib() => LibBox.SelectedItems.Cast<LibraryItem>().ToList();
    PointerPressedEventArgs? _dragPress;
    void LibBox_MouseDown(object? s, PointerPressedEventArgs e) { if (e.GetCurrentPoint(LibBox).Properties.IsLeftButtonPressed) { _dragStart = e.GetPosition(null); _dragPress = e; } }
    async void LibBox_MouseMove(object? s, PointerEventArgs e)
    {
        if (_dragPress == null || !e.GetCurrentPoint(LibBox).Properties.IsLeftButtonPressed) { _dragPress = null; return; }
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
        var sel = SelectedLib(); var press = _dragPress; _dragPress = null; if (sel.Count == 0) return;
        var data = new DataObject(); data.Set("EpikLibItems", sel.Select(i => i.Path).ToArray());
#pragma warning disable CS0618
        await DragDrop.DoDragDrop(press, data, DragDropEffects.Copy);
#pragma warning restore CS0618
    }
    void LibQueue_Click(object? s, RoutedEventArgs e)
    {
        foreach (var it in SelectedLib()) _requests.Add(it);
        if (!_store.Requests.Enabled) MessageBox.Show(this, "Queued. Turn on song requests (Requests tab) for the queue to play during the stream.");
    }
    void LibToPlaylist_Click(object? s, RoutedEventArgs e)
    {
        var pl = CurrentPlaylist() ?? NewPlaylist(); if (pl == null) return;
        pl.Items.AddRange(SelectedLib().Select(i => i.Path)); SavePlaylists(pl);
    }
    void LibToStream_Click(object? s, RoutedEventArgs e) { foreach (var it in SelectedLib()) PlaylistBox.Items.Add(it.Path); Persist(); }

    // ---------- saved playlists ----------
    NamedPlaylist? CurrentPlaylist() => PlCombo.SelectedItem as NamedPlaylist;
    void RefreshPlaylists(NamedPlaylist? select = null)
    {
        var cur = select ?? CurrentPlaylist();
        _loading = true; PlCombo.Items.Clear();
        foreach (var p in _library.Playlists) PlCombo.Items.Add(p);
        PlCombo.SelectedItem = cur != null && _library.Playlists.Contains(cur) ? cur : _library.Playlists.FirstOrDefault();
        _loading = false; RefreshPlItems();
    }
    void RefreshPlItems()
    {
        PlItems.Items.Clear();
        if (CurrentPlaylist() is { } p) foreach (var f in p.Items) { var li = new ListBoxItem { Content = _library.Find(f)?.Display ?? LibraryItem.TitleFromFile(f), Tag = f }; ToolTip.SetTip(li, f); PlItems.Items.Add(li); }
    }
    void SavePlaylists(NamedPlaylist? select = null) { try { _library.Save(); } catch (Exception ex) { Log("Could not save playlists: " + ex.Message); } RefreshPlaylists(select); }
    void PlCombo_Changed(object? s, SelectionChangedEventArgs e) { if (!_loading) RefreshPlItems(); }
    NamedPlaylist? NewPlaylist()
    {
        var n = AskText("New playlist", "Name:", $"Playlist {_library.Playlists.Count + 1}"); if (n == null) return null;
        var p = new NamedPlaylist { Name = n }; _library.Playlists.Add(p); SavePlaylists(p); return p;
    }
    void PlNew_Click(object? s, RoutedEventArgs e) => NewPlaylist();
    void PlRename_Click(object? s, RoutedEventArgs e) { if (CurrentPlaylist() is { } p && AskText("Rename playlist", "Name:", p.Name) is { } n) { p.Name = n; SavePlaylists(p); } }
    void PlDelete_Click(object? s, RoutedEventArgs e)
    {
        if (CurrentPlaylist() is not { } p) return;
        if (MessageBox.Show(this, $"Delete playlist \"{p.Name}\"? (The media files are not touched.)", "Delete", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _library.Playlists.Remove(p); SavePlaylists();
    }
    void PlItems_Drop(object? s, DragEventArgs e)
    {
        string[]? paths = e.Data.Get("EpikLibItems") as string[] ?? FileDrops(e);
        if (paths == null || paths.Length == 0) return;
        var p = CurrentPlaylist() ?? NewPlaylist(); if (p == null) return;
        p.Items.AddRange(paths.Where(File.Exists)); SavePlaylists(p);
    }
    void PlMove(int d)
    {
        if (CurrentPlaylist() is not { } p || PlItems.SelectedIndex < 0) return;
        int i = PlItems.SelectedIndex, j = i + d; if (j < 0 || j >= p.Items.Count) return;
        (p.Items[i], p.Items[j]) = (p.Items[j], p.Items[i]); SavePlaylists(p); PlItems.SelectedIndex = j;
    }
    void PlUp_Click(object? s, RoutedEventArgs e) => PlMove(-1);
    void PlDown_Click(object? s, RoutedEventArgs e) => PlMove(1);
    void PlRemoveItem_Click(object? s, RoutedEventArgs e)
    {
        if (CurrentPlaylist() is not { } p) return;
        var idx = PlItems.SelectedItems.Cast<ListBoxItem>().Select(x => PlItems.Items.IndexOf(x)).OrderByDescending(x => x).ToList();
        foreach (var i in idx) p.Items.RemoveAt(i); SavePlaylists(p);
    }
    void PlUse_Click(object? s, RoutedEventArgs e)
    {
        if (CurrentPlaylist() is not { } p) return;
        PlaylistBox.Items.Clear(); foreach (var f in p.Items) PlaylistBox.Items.Add(f); Persist();
        StatusText.Text = $"Stream playlist replaced with \"{p.Name}\" ({p.Items.Count} items).";
    }
    void PlQueue_Click(object? s, RoutedEventArgs e)
    {
        if (CurrentPlaylist() is not { } p) return;
        foreach (var f in p.Items) _requests.Add(_library.Find(f) ?? new LibraryItem { Path = f, Title = LibraryItem.TitleFromFile(f) });
    }

    string? AskText(string title, string label, string value) => Dialogs.AskText(this, title, label, value);

    // ---------- requests ----------
    void LoadRequestSettings()
    {
        var r = _store.Requests;
        ReqEnable.IsChecked = r.Enabled; ReqPlacement.SelectedIndex = r.Placement == "End" ? 1 : 0;
        ReqPerUser.Text = r.PerUserLimit.ToString(); ReqCooldown.Text = r.UserCooldownSeconds.ToString(); ReqMax.Text = r.MaxQueue.ToString();
        ReqMaxDur.Text = r.MaxDurationMinutes.ToString(); ReqScore.Text = r.MinMatchScore.ToString(CultureInfo.InvariantCulture);
        ReqApproval.IsChecked = r.RequireApproval; ReqAllowed.Text = r.AllowedUsers; ReqBlocked.Text = r.BlockedUsers;
        ReqVoteSkip.IsChecked = r.VoteSkipEnabled; ReqVotes.Text = r.SkipVotes.ToString(); ReqPercent.Text = r.SkipPercent.ToString(); ReqWrong.IsChecked = r.WrongSongEnabled;
    }
    static double Dbl(string t, double def, double min, double max) => double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : def;
    void ReqSave_Click(object? s, RoutedEventArgs e)
    {
        var r = _store.Requests;
        r.Enabled = ReqEnable.IsChecked == true; r.Placement = ReqPlacement.SelectedIndex == 1 ? "End" : "Next";
        r.PerUserLimit = Int(ReqPerUser.Text, 2, 0, 100); r.UserCooldownSeconds = Int(ReqCooldown.Text, 60, 0, 86400); r.MaxQueue = Int(ReqMax.Text, 20, 1, 1000);
        r.MaxDurationMinutes = Int(ReqMaxDur.Text, 0, 0, 1440); r.MinMatchScore = Dbl(ReqScore.Text, 0.55, 0.1, 1);
        r.RequireApproval = ReqApproval.IsChecked == true; r.AllowedUsers = ReqAllowed.Text.Trim(); r.BlockedUsers = ReqBlocked.Text.Trim();
        r.VoteSkipEnabled = ReqVoteSkip.IsChecked == true; r.SkipVotes = Int(ReqVotes.Text, 3, 1, 1000); r.SkipPercent = Int(ReqPercent.Text, 0, 0, 100); r.WrongSongEnabled = ReqWrong.IsChecked == true;
        _requests.S = r; if (_engine != null) _engine.Requests = r.Enabled ? _requests : null;
        Persist(); LoadRequestSettings(); StatusText.Text = "Request settings saved.";
    }
    /// <summary>Handles !request/!sr/!queue/!skip/!wrongsong. Returns true if the message was a request command.</summary>
    readonly Dictionary<string, DateTime> _lastNoPlaylistReply = new();
    bool HandleRequestCommand(ChatMessage m)
    {
        if (!_store.Requests.Enabled || !SongRequestQueue.IsRequestCommand(m.Text)) return false;
        if (m.IsBot || m.Self || (_store.Chat.MyUsername.Length > 0 && string.Equals(m.User, _store.Chat.MyUsername, StringComparison.OrdinalIgnoreCase))) return true;
        if (_engine?.IsRunning == true ? _engine.PitOnly : SourceMode.PitOnly(_cur))
        {   // 2.8.2: Ball Pit scene with no playlist — nothing to queue (radio requests, when on, were handled before this)
            var why = SourceMode.NoPlaylistReply + (_cur.Radio.Active && !_cur.Radio.Requests ? " (radio requests are off)." : "");
            if (_lastNoPlaylistReply.TryGetValue(m.Room, out var t0) && (DateTime.Now - t0).TotalSeconds < 30) return true;
            _lastNoPlaylistReply[m.Room] = DateTime.Now;
            Log($"🎵 {m.User}: {m.Text} → {why}");
            if (_store.Chat.ReplyInChat) _ = ReplyAsync($"@{m.User} {why}", m.Room);
            return true;
        }
        var res = _requests.Handle(m, _library.Snapshot(), _engine?.OnAirItem ?? "", _engine?.TrackKey ?? "", _lastViewers ?? _viewers.PresentCount, DateTime.Now);
        if (res == null) return true;
        if (res.Skip && _engine?.IsRunning == true) { Log($"⏭ skipped by chat ({m.User}: {m.Text})"); _engine.Skip(); }
        if (res.Reply is { Length: > 0 } reply)
        {
            Log($"🎵 {m.User}: {m.Text} → {reply}");
            var c = _store.Chat; bool live = _engine?.IsRunning == true;
            if (live) _engine!.ShowChatResponse(reply, c.OverlaySeconds);
            if (c.ReplyInChat) _ = ReplyAsync(reply, m.Room);
        }
        return true;
    }
    void RefreshRequests()
    {
        var sel = (ReqBox.SelectedItem as ListBoxItem)?.Tag as int?;
        ReqBox.Items.Clear(); int n = 0;
        foreach (var r in _requests.Snapshot())
        {
            var li = new ListBoxItem { Content = $"{++n}. {r}  [{LibraryItem.FormatTime(r.Duration)}]", Tag = r.Id };
            ReqBox.Items.Add(li); if (r.Id == sel) ReqBox.SelectedItem = li;
        }
        if (n == 0) ReqBox.Items.Add(new ListBoxItem { Content = "(queue empty: viewers type !request <song name>)", IsEnabled = false });
        RequestsTab.Header = n > 0 ? $"Requests ({n})" : "Requests";
    }
    void RefreshNowText()
    {
        if (_engine?.IsRunning != true) { ReqNow.Text = "Not streaming."; return; }
        var on = _engine.OnAirItem; var p = _requests.Playing;
        var by = p != null && string.Equals(p.Path, on, StringComparison.OrdinalIgnoreCase) ? $" (requested by {p.User})" : "";
        var next = _requests.PeekNext()?.Path ?? _engine.UpNextItem;
        ReqNow.Text = $"▶ {_engine.TitleOf(on)}{by}" + (string.IsNullOrEmpty(next) ? "" : $"     Up next: {_engine.TitleOf(next)}");
    }
    int? SelReq() => (ReqBox.SelectedItem as ListBoxItem)?.Tag as int?;
    void ReqApprove_Click(object? s, RoutedEventArgs e) { if (SelReq() is int id) _requests.Approve(id); }
    void ReqRemove_Click(object? s, RoutedEventArgs e) { if (SelReq() is int id) _requests.Remove(id); }
    void ReqUp_Click(object? s, RoutedEventArgs e) { if (SelReq() is int id) _requests.Move(id, -1); }
    void ReqDown_Click(object? s, RoutedEventArgs e) { if (SelReq() is int id) _requests.Move(id, 1); }
    void ReqClear_Click(object? s, RoutedEventArgs e) { if (MessageBox.Show(this, "Clear the whole request queue?", "Clear", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _requests.Clear(); }

    // ---------- viewers ----------
    void LoadViewerSettings()
    {
        var v = _store.Viewers;
        VwEnable.IsChecked = v.Enabled; VwSource.SelectedIndex = v.Source switch { "LiveViewers" => 1, "RoomMembers" => 2, _ => 0 };
        VwGrace.Text = v.LeaveGraceSeconds.ToString(); VwJoinAlert.IsChecked = v.JoinAlerts; VwJoinText.Text = v.JoinText;
    }
    void VwSave_Click(object? s, RoutedEventArgs e)
    {
        var v = _store.Viewers;
        v.Enabled = VwEnable.IsChecked == true; v.Source = VwSource.SelectedIndex switch { 1 => "LiveViewers", 2 => "RoomMembers", _ => "Both" };
        v.LeaveGraceSeconds = Int(VwGrace.Text, 20, 0, 3600); v.JoinAlerts = VwJoinAlert.IsChecked == true;
        v.JoinText = VwJoinText.Text.Trim().Length > 0 ? VwJoinText.Text.Trim() : "👋 Welcome {user}!";
        _viewers.S = v; Persist(); StatusText.Text = "Viewer settings saved.";
    }
    void OnViewerNames(string source, List<string> names)
    {
        if (!_store.Viewers.Enabled) return;
        var mine = _store.Chat.MyUsername;
        _viewers.Update(source, names.Where(n => mine.Length == 0 || !string.Equals(n, mine, StringComparison.OrdinalIgnoreCase)), DateTime.Now);
    }
    void OnViewerJoined(ViewerSession v)
    {
        if (_store.Chat.DebugLog || v.Visits == 1) Log($"👤 {v.Name} joined" + (v.Visits > 1 ? $" (visit {v.Visits})" : "") + $" via {v.Source}");
        var s = _store.Viewers;
        if (s.JoinAlerts && v.Visits == 1 && _engine?.IsRunning == true)
            ShowAlert(s.JoinText.Replace("{user}", v.Name).Replace("{viewers}", (_lastViewers ?? _viewers.PresentCount).ToString()));
    }
    void RefreshViewers()
    {
        if (!IsVisible) return;
        var now = DateTime.Now; var all = _viewers.Snapshot();
        var sel = VwBox.SelectedItem is ListBoxItem si ? si.Tag as string : null;
        VwBox.Items.Clear();
        foreach (var v in all)
        {
            var state = v.Present ? "● here " : "○ left ";
            var when = v.Present ? $"joined {v.JoinedAt:HH:mm}" : $"left {v.LeftAt:HH:mm}";
            var tot = v.TotalNow(now);
            var li = new ListBoxItem { Content = $"{state} {v.Name,-24} {when,-13} total {(int)tot.TotalHours}:{tot.Minutes:00}:{tot.Seconds:00}   visits {v.Visits}   ({v.Source})", Tag = v.Name };
            VwBox.Items.Add(li); if (v.Name == sel) VwBox.SelectedItem = li;
        }
        int here = all.Count(x => x.Present);
        VwSummary.Text = all.Count == 0 ? (_store.Viewers.Enabled ? "No names seen yet (open the 💬 Chat panel)." : "Viewer tracking is off.")
            : $"{here} here now · {all.Count} unique this session" + (_lastViewers is int lv ? $" · EpikChat count {lv}" : "");
        ViewersTab.Header = here > 0 ? $"Viewers ({here})" : "Viewers";
    }
    async void VwOpen_Click(object? s, RoutedEventArgs e)
    {
        if ((VwBox.SelectedItem as ListBoxItem)?.Tag is not string name) return;
        var r = await _chat.OpenProfile(name);
        StatusText.Text = r == "opened" ? $"Opened {name}'s profile in the chat panel." : $"Couldn't open {name}'s profile: {r}";
    }
    void VwReset_Click(object? s, RoutedEventArgs e) { _viewers.Reset(); RefreshViewers(); }

}
