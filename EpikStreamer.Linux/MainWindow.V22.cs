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

/// <summary>v2.2: tray, hotkeys, scheduler, chat commands + alerts, audio, history, themes, mini player.</summary>
public partial class MainWindow
{
    Tray? _tray; GlobalHotkeys? _hotkeys; MiniPlayer? _mini;
    readonly DispatcherTimer _schedTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly HashSet<string> _firedSchedules = new();
    ScheduleEntry? _runningSchedule; DateTime _scheduleEnd = DateTime.MaxValue;
    List<string>? _playlistOverride; bool _quiet;
    ChatCommands _commands = new(new ChatSettings()); ViewerAlerts _alerts = new(new ChatSettings());
    SessionTracker? _session; StreamStats? _lastStats; int? _lastViewers;
    readonly HistoryStore _history = new();
    bool _exitRequested, _musicMuted, _micMuted, _overlaysHidden;
    static readonly bool StartedMinimized = Environment.GetCommandLineArgs().Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

    void Init22()
    {
        foreach (var n in ThemeManager.Names) ThemeCombo.Items.Add(n);
        _loading = true; ThemeCombo.SelectedItem = ThemeManager.Names.Contains(_store.Theme) ? _store.Theme : "Dark"; _loading = false;
        ThemeManager.Apply(_store.Theme);
        LoadAppSettings();
        RebuildChatEngines();
        RefreshSchedule(); RefreshHistory();
        _chat.MessageReceived += m => Dispatcher.UIThread.BeginInvoke(() => OnChatMessage(m));
        _chat.ObserverStatus += t => Dispatcher.UIThread.BeginInvoke(() => { ChatObsStatus.Text = "Chat observer: " + t; Log("💬 chat observer: " + t); });
        ApplyChatPanelConfig();
        Init23();
        _schedTimer.Tick += (_, _) => SchedulerTick(); _schedTimer.Start();
        Opened += (_, _) => { if (_hotkeys != null) return; _hotkeys = new GlobalHotkeys(this); if (_hotkeys.Unavailable is { } why) Log("⌨ global hotkeys unavailable: " + why); ApplyHotkeys(); };
        _tray = new Tray(ShowMain, () => Dispatcher.UIThread.BeginInvoke(ToggleStartStop), () => _engine?.Skip(),
            () => SetMusicMuted(!_musicMuted), () => SetMicMuted(!_micMuted), ShowMini, ExitApp) { Notifications = _store.Notifications };
        PropertyChanged += (_, e) => { if (e.Property != WindowStateProperty) return; if (WindowState == WindowState.Minimized && _store.MinimizeToTray && _tray != null) Hide(); };
        Closed += (_, _) => { _hotkeys?.Dispose(); _tray?.Dispose(); _mini?.Close(); };
        if (StartedMinimized) { WindowState = WindowState.Minimized; ShowActivated = false; Loaded += (_, _) => { if (_store.MinimizeToTray) Hide(); }; }
        UpdateControls();
    }

    // ---------- settings <-> UI ----------
    void LoadAppSettings()
    {
        _loading = true;
        MinToTrayCheck.IsChecked = _store.MinimizeToTray; CloseToTrayCheck.IsChecked = _store.CloseToTray; NotifyCheck.IsChecked = _store.Notifications;
        StartupCheck.IsChecked = _store.LaunchAtStartup; StartMinCheck.IsChecked = _store.StartMinimized;
        SchedulerEnableCheck.IsChecked = _store.SchedulerEnabled;
        var h = _store.Hotkeys; HotkeysCheck.IsChecked = h.Enabled;
        HkStart.Text = h.StartStop; HkNext.Text = h.Next; HkMusic.Text = h.MuteMusic; HkMic.Text = h.MuteMic; HkOverlays.Text = h.ToggleOverlays;
        var c = _store.Chat;
        CmdEnableCheck.IsChecked = c.CommandsEnabled; CmdOverlayCheck.IsChecked = c.ShowOnStream; ReplyInChatCheck.IsChecked = c.ReplyInChat; AutoGoLiveCheck.IsChecked = c.AutoGoLive;
        CmdOverlaySecs.Text = c.OverlaySeconds.ToString(); CmdCooldown.Text = c.CooldownSeconds.ToString();
        SongTpl.Text = c.SongTemplate; UptimeTpl.Text = c.UptimeTemplate;
        CustomCmds.Text = string.Join(Environment.NewLine, c.Custom.Select(x => (x.Enabled ? "" : "#") + x.Trigger + " = " + x.Response));
        MyUserBox.Text = c.MyUsername; ReplyIntervalBox.Text = c.ReplyMinIntervalSeconds.ToString(); ChatDebugCheck.IsChecked = c.DebugLog;
        AlertsCheck.IsChecked = c.AlertsEnabled; NewViewerCheck.IsChecked = c.NewViewerAlert; AlertSecs.Text = c.AlertSeconds.ToString();
        NewViewerTpl.Text = c.NewViewerText; MilestonesBox.Text = string.Join(", ", c.Milestones); MilestoneTpl.Text = c.MilestoneText; AlertSoundBox.Text = c.AlertSound;
        _loading = false;
    }

    void SaveAppSettings()
    {
        _store.MinimizeToTray = MinToTrayCheck.IsChecked == true; _store.CloseToTray = CloseToTrayCheck.IsChecked == true;
        _store.Notifications = NotifyCheck.IsChecked == true; if (_tray != null) _tray.Notifications = _store.Notifications;
        bool startup = StartupCheck.IsChecked == true, startMin = StartMinCheck.IsChecked == true;
        if (startup != _store.LaunchAtStartup || startMin != _store.StartMinimized || (startup && !StartupRegistration.IsSet())) StartupRegistration.Set(startup, startMin);
        _store.LaunchAtStartup = startup; _store.StartMinimized = startMin;
        _store.SchedulerEnabled = SchedulerEnableCheck.IsChecked == true;
        _store.Theme = ThemeCombo.SelectedItem as string ?? "Dark";
        var h = _store.Hotkeys; h.Enabled = HotkeysCheck.IsChecked == true;
        h.StartStop = HkStart.Text.Trim(); h.Next = HkNext.Text.Trim(); h.MuteMusic = HkMusic.Text.Trim(); h.MuteMic = HkMic.Text.Trim(); h.ToggleOverlays = HkOverlays.Text.Trim();
        var c = _store.Chat;
        c.CommandsEnabled = CmdEnableCheck.IsChecked == true; c.ShowOnStream = CmdOverlayCheck.IsChecked == true; c.ReplyInChat = ReplyInChatCheck.IsChecked == true; c.AutoGoLive = AutoGoLiveCheck.IsChecked == true;
        c.OverlaySeconds = Int(CmdOverlaySecs.Text, 8, 1, 120); c.CooldownSeconds = Int(CmdCooldown.Text, 10, 0, 3600);
        c.SongTemplate = SongTpl.Text; c.UptimeTemplate = UptimeTpl.Text;
        c.Custom = CustomCmds.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 1 && l.Contains('='))
            .Select(l => { bool en = !l.StartsWith('#'); var t = l.TrimStart('#').Trim(); int i = t.IndexOf('=');
                           var trig = t[..i].Trim(); if (!trig.StartsWith('!')) trig = "!" + trig;
                           return new ChatCommand { Trigger = trig.Split(' ')[0], Response = t[(i + 1)..].Trim(), Enabled = en }; }).ToList();
        bool obsChanged = c.MyUsername != MyUserBox.Text.Trim() || c.DebugLog != (ChatDebugCheck.IsChecked == true);
        c.MyUsername = MyUserBox.Text.Trim(); c.ReplyMinIntervalSeconds = Int(ReplyIntervalBox.Text, 5, 1, 600); c.DebugLog = ChatDebugCheck.IsChecked == true;
        ApplyChatPanelConfig(); if (obsChanged) _ = _chat.ReloadObserver();
        c.AlertsEnabled = AlertsCheck.IsChecked == true; c.NewViewerAlert = NewViewerCheck.IsChecked == true; c.AlertSeconds = Int(AlertSecs.Text, 6, 1, 60);
        c.NewViewerText = NewViewerTpl.Text; c.MilestoneText = MilestoneTpl.Text; c.AlertSound = AlertSoundBox.Text.Trim();
        c.Milestones = MilestonesBox.Text.Split(',', ';', ' ').Select(x => int.TryParse(x.Trim(), out var v) ? v : 0).Where(v => v > 0).Distinct().OrderBy(v => v).ToList();
        RebuildChatEngines();
    }
    void RebuildChatEngines() { _commands = new ChatCommands(_store.Chat); _alerts = new ViewerAlerts(_store.Chat); HookBallPitCommands(); }

    void LoadAudio(Profile p)
    {
        var a = p.Audio;
        foreach (ComboBoxItem i in NormCombo.Items) if ((string)i.Tag == a.Normalize) NormCombo.SelectedItem = i;
        if (NormCombo.SelectedItem == null) NormCombo.SelectedIndex = 0;
        LufsBox.Text = a.TargetLufs.ToString(CultureInfo.InvariantCulture); MainVolSlider.Value = a.MainVolume * 100;
        MusicEnable.IsChecked = a.MusicEnabled; MusicShuffleCheck.IsChecked = a.MusicShuffle; MusicVolSlider.Value = a.MusicVolume * 100;
        DuckCheck.IsChecked = a.Ducking; DuckSlider.Value = Math.Round(a.DuckThreshold * 100); DuckRatioSlider.Value = a.DuckRatio;
        MusicList.Items.Clear(); foreach (var f in a.MusicPlaylist) MusicList.Items.Add(f);
        MicEnable.IsChecked = a.MicEnabled; MicCombo.Text = a.MicDevice; MicVolSlider.Value = a.MicVolume * 100;
        GateCheck.IsChecked = a.MicNoiseGate; GateSlider.Value = Math.Round(a.GateThreshold * 200);
    }
    void SaveAudio(Profile p)
    {
        var a = p.Audio;
        a.Normalize = (NormCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Off";
        a.TargetLufs = double.TryParse(LufsBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, -70, -5) : -16;
        a.MainVolume = MainVolSlider.Value / 100; a.MusicEnabled = MusicEnable.IsChecked == true; a.MusicShuffle = MusicShuffleCheck.IsChecked == true;
        a.MusicVolume = MusicVolSlider.Value / 100; a.Ducking = DuckCheck.IsChecked == true; a.DuckThreshold = DuckSlider.Value / 100; a.DuckRatio = DuckRatioSlider.Value;
        a.MusicPlaylist = MusicList.Items.Cast<string>().ToList();
        a.MicEnabled = MicEnable.IsChecked == true; a.MicDevice = MicCombo.Text.Trim(); a.MicVolume = MicVolSlider.Value / 100;
        a.MicNoiseGate = GateCheck.IsChecked == true; a.GateThreshold = GateSlider.Value / 200;
    }

    void SaveSettings_Click(object? s, RoutedEventArgs e) { Persist(); ApplyHotkeys(); StatusText.Text = "Settings saved."; }
    void ThemeCombo_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedItem is not string t) return;
        ThemeManager.Apply(t); _store.Theme = t; try { _store.Save(); } catch { }
    }
    void ReplyInChat_Click(object? s, RoutedEventArgs e)
    {
        if (ReplyInChatCheck.IsChecked != true) return;
        var r = MessageBox.Show(this, "EpikStreamer will type command responses into EpikChat's chat box as you, using your signed-in session.\n\nOnly turn this on if you're comfortable with automated messages from your account.",
            "Reply in chat", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) ReplyInChatCheck.IsChecked = false;
    }

    // ---------- tray / window ----------
    void ShowMain() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    void ExitApp() { _exitRequested = true; ShowMain(); Close(); }
    /// <summary>Returns true if closing was turned into hide-to-tray.</summary>
    bool HideInsteadOfClose()
    {
        if (_exitRequested || !_store.CloseToTray || _tray == null) return false;
        Hide(); _tray.Notify("EpikStreamer is still running", "Right-click the tray icon to exit."); return true;
    }
    void Notify(string title, string text, bool error = false) => _tray?.Notify(title, text, error);

    // ---------- hotkeys ----------
    void HotkeysApply_Click(object? s, RoutedEventArgs e) { SaveAppSettings(); ApplyHotkeys(); try { _store.Save(); } catch { } }
    void ApplyHotkeys()
    {
        if (_hotkeys == null) return;
        _hotkeys.Clear(); var h = _store.Hotkeys;
        if (!h.Enabled) { HotkeyStatus.Text = "Hotkeys are off."; return; }
        var errs = new[]
        {
            _hotkeys.Register(h.StartStop, ToggleStartStop), _hotkeys.Register(h.Next, () => _engine?.Skip()),
            _hotkeys.Register(h.MuteMusic, () => SetMusicMuted(!_musicMuted)), _hotkeys.Register(h.MuteMic, () => SetMicMuted(!_micMuted)),
            _hotkeys.Register(h.ToggleOverlays, () => SetOverlaysHidden(!_overlaysHidden)),
        }.Where(x => x != null).ToList();
        HotkeyStatus.Text = errs.Count == 0 ? "Hotkeys active." : "⚠ " + string.Join("; ", errs);
    }
    void ToggleStartStop() { if (_engine?.IsRunning == true) _ = StopEngine(); else Start_Click(this, new RoutedEventArgs()); }

    // ---------- live mutes ----------
    void MusicMute_Click(object? s, RoutedEventArgs e) => SetMusicMuted(MusicMuteBtn.IsChecked == true);
    void MicMute_Click(object? s, RoutedEventArgs e) => SetMicMuted(MicMuteBtn.IsChecked == true);
    void OverlayHide_Click(object? s, RoutedEventArgs e) => SetOverlaysHidden(OverlayHideBtn.IsChecked == true);
    void SetMusicMuted(bool m) { _musicMuted = m; if (_engine != null && !_engine.SetMusicMuted(m)) StatusText.Text = "Couldn't change music volume live (see log)."; UpdateControls(); }
    void SetMicMuted(bool m) { _micMuted = m; if (_engine != null && !_engine.SetMicMuted(m)) StatusText.Text = "Couldn't change mic volume live (see log)."; UpdateControls(); }
    void SetOverlaysHidden(bool h) { _overlaysHidden = h; if (_engine?.IsRunning == true && !_engine.SetOverlaysHidden(h)) StatusText.Text = "Couldn't toggle overlays live (see log)."; UpdateControls(); }
    void MusicVol_Changed(object? s, RangeBaseValueChangedEventArgs e) { if (!_loading && _engine?.IsRunning == true) _engine.SetMusicVolume(e.NewValue / 100); }
    void MicVol_Changed(object? s, RangeBaseValueChangedEventArgs e) { if (!_loading && _engine?.IsRunning == true) _engine.SetMicVolume(e.NewValue / 100); }

    void UpdateControls()
    {
        bool running = _engine?.IsRunning == true;
        MusicMuteBtn.IsChecked = _musicMuted; MicMuteBtn.IsChecked = _micMuted; OverlayHideBtn.IsChecked = _overlaysHidden;
        var st = _lastStats; var line1 = running ? (st?.ItemName ?? "Starting…") : "Not streaming";
        var music = _engine?.CurrentMusic; 
        var line2 = running && st != null ? $"{st.Uptime:hh\\:mm\\:ss} · {st.BitrateKbps:0} kbps · {st.Fps:0.#} fps" + (_lastViewers is int v ? $" · {v} 👁" : "") + (string.IsNullOrEmpty(music) ? "" : $" · ♪ {music}") : StatusText.Text;
        _tray?.SetState(running, _musicMuted, _micMuted, running ? "LIVE " + (st?.ItemName ?? "") : "idle");
        _mini?.Update(PreviewImg.Source, line1, line2, running, _musicMuted, _micMuted, _overlaysHidden, _cur.Audio.MusicEnabled, _cur.Audio.MicEnabled);
    }

    // ---------- mini player ----------
    void Mini_Click(object? s, RoutedEventArgs e) => ShowMini();
    void ShowMini()
    {
        if (_mini == null)
        {
            _mini = new MiniPlayer(() => Dispatcher.UIThread.BeginInvoke(ToggleStartStop), () => _engine?.Skip(), SetMusicMuted, SetMicMuted, SetOverlaysHidden, ShowMain);
            _mini.Closed += (_, _) => _mini = null;
            if ((Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea is { } wa) { var sc = (Screens.ScreenFromWindow(this) ?? Screens.Primary)!.Scaling; _mini.Position = new PixelPoint(wa.Right - (int)((_mini.Width + 16) * sc), wa.Bottom - (int)((_mini.Height + 16) * sc)); }
        }
        _mini.Show(); _mini.Activate(); UpdateControls();
        if (_store.MinimizeToTray && IsVisible) WindowState = WindowState.Minimized;
    }

    // ---------- audio tab ----------
    const string AudioFilter = "Audio files|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus;*.wma|All files|*.*";
    void AddMusic_Click(object? s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = AudioFilter, Multiselect = true };
        if (d.ShowDialog(this) == true) { foreach (var f in d.FileNames) MusicList.Items.Add(f); MusicEnable.IsChecked = true; }
    }
    void MusicList_Drop(object? s, DragEventArgs e) { if (FileDrops(e) is string[] fs) foreach (var f in fs) MusicList.Items.Add(f); }
    void RemoveMusic_Click(object? s, RoutedEventArgs e) { foreach (var i in MusicList.SelectedItems.Cast<object>().ToList()) MusicList.Items.Remove(i); }
    void ClearMusic_Click(object? s, RoutedEventArgs e) => MusicList.Items.Clear();
    async void RefreshMics_Click(object? s, RoutedEventArgs e)
    {
        var ff = FfmpegLocator.Find(FfmpegBox.Text.Trim());
        _ = ff;
        var text = await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(ff) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                psi.FileName = "pactl"; foreach (var a in new[] { "list", "short", "sources" }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(8000); return o;
            }
            catch (Exception ex) { return ex.Message; }
        });
        var devs = PulseSources(text); var cur = MicCombo.Text;
        MicCombo.ItemsSource = devs;
        MicCombo.Text = devs.Contains(cur) || devs.Count == 0 ? cur : devs[0];
        StatusText.Text = devs.Count == 0 ? "No audio capture devices found." : $"Found {devs.Count} audio device(s).";
    }

    // ---------- chat commands + alerts ----------
    string? HandleChatCommand(ChatMessage m, ChatCommands.Context ctx) => _commands.Handle(m, ctx, DateTime.Now);

    void ApplyChatPanelConfig() { var c = _store.Chat; _chat.SelfName = c.MyUsername; _chat.DebugMessages = c.DebugLog; _chat.ReplyMinIntervalSeconds = c.ReplyMinIntervalSeconds; }

    void OnChatMessage(ChatMessage m)
    {
        var c = _store.Chat;
        if (c.DebugLog) Log($"💬 [{m.Room}] {m.User}{(m.IsBot ? " (bot)" : "")}{(m.Self ? " (you)" : "")}: {m.Text}");
        if (HandleRadioRequest(m)) return;   // v2.6: radio on + radio requests → AzuraCast
        if (HandleRequestCommand(m)) return;
        bool live = _engine?.IsRunning == true;
        var ctx = new ChatCommands.Context { Song = System.IO.Path.GetFileNameWithoutExtension(_lastStats?.ItemName ?? ""), Uptime = live ? _lastStats?.Uptime ?? TimeSpan.Zero : TimeSpan.Zero, Viewers = _lastViewers, Music = _engine?.CurrentMusic ?? "" };
        RadioContext(ctx);
        var resp = HandleChatCommand(m, ctx);
        if (resp == null) return;
        Log($"💬 {m.User}: {m.Text} → {resp}");
        bool shown = false;
        if (c.ShowOnStream && live) { _engine!.ShowChatResponse(resp, c.OverlaySeconds); shown = true; }
        if (c.ReplyInChat) { _ = ReplyAsync(resp); shown = true; }
        if (!shown) Log("   (not streaming, so the response isn't shown anywhere; turn on \"reply in chat\" to answer while offline)");
    }
    async Task ReplyAsync(string text) { var r = await _chat.SendChat(text); Log(r.StartsWith("sent") ? "💬 replied in chat " + r[4..].Trim() : "⚠ chat reply " + r); }

    async void ChatDiag_Click(object? s, RoutedEventArgs e)
    {
        var d = await _chat.Diagnostics();
        if (d == null) { MessageBox.Show(this, "Open the 💬 Chat panel (signed in) first."); return; }
        var dlg = new SaveFileDialog { Filter = "JSON|*.json", FileName = $"epikchat-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, d);
        StatusText.Text = "Chat diagnostics saved (structure only; no message text or names): " + dlg.FileName;
    }

    void OnViewerCount(int? viewers)
    {
        _lastViewers = viewers;
        if (viewers is not int v || _engine?.IsRunning != true) return;
        _session?.OnViewers(v);
        var alert = _alerts.Update(v, DateTime.Now);
        if (alert != null) ShowAlert(alert);
    }
    void ShowAlert(string text)
    {
        _engine?.ShowAlert(text, _store.Chat.AlertSeconds);
        Log("🔔 " + text);
        var snd = _store.Chat.AlertSound;
        if (!string.IsNullOrWhiteSpace(snd) && File.Exists(snd)) { PlaySound(snd); }
    }
    void AlertSoundBrowse_Click(object? s, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Sounds|*.wav;*.ogg;*.oga;*.mp3|All files|*.*" }; if (d.ShowDialog(this) == true) AlertSoundBox.Text = d.FileName; }
    void AlertTest_Click(object? s, RoutedEventArgs e)
    {
        SaveAppSettings();
        var text = ChatCommands.Expand(_store.Chat.MilestoneText, new ChatCommands.Context { Viewers = 42 });
        if (_engine?.IsRunning == true) ShowAlert(text);
        else { ShowAlert(text); StatusText.Text = "Test alert: \"" + text + "\" (shows on stream while live)."; }
    }

    // ---------- session history ----------
    void BeginSession(Profile run, IEnumerable<string> destNames)
    {
        _session = new SessionTracker(run.Name, destNames.Concat(run.RecordEnabled ? new[] { "Recording" } : Array.Empty<string>()), DateTime.Now);
        _alerts.Reset(); _lastStats = null;
    }
    void EndSession(string reason)
    {
        EndSession23();
        var s = _session; _session = null; if (s == null) return;
        try { var r = s.Finish(DateTime.Now, reason); if (r.DurationSec >= 5) _history.Append(r); } catch (Exception ex) { Log("Could not save session history: " + ex.Message); }
        RefreshHistory();
    }
    List<SessionRecord> _historyCache = new();
    void RefreshHistory()
    {
        _historyCache = _history.Load();
        HistoryList.Items.Clear();
        foreach (var r in Enumerable.Reverse(_historyCache))
            HistoryList.Items.Add($"{r.Start:yyyy-MM-dd HH:mm}  {TimeSpan.FromSeconds(r.DurationSec):hh\\:mm\\:ss}  {r.Profile,-14} peak {r.PeakViewers,3} avg {r.AvgViewers,6:0.0}  {r.AvgBitrateKbps,5:0} kbps  drop {r.DroppedFrames,4}  reconn {r.Reconnects,2}  files {r.FilesPlayed.Count,3}  {r.EndReason}");
        var total = TimeSpan.FromSeconds(_historyCache.Sum(r => r.DurationSec));
        HistorySummary.Text = _historyCache.Count == 0 ? "No sessions yet." : $"{_historyCache.Count} sessions · {(int)total.TotalHours}h {total.Minutes:00}m streamed · best peak {_historyCache.Max(r => r.PeakViewers)} viewers";
        DrawHistoryChart();
    }
    void HistoryChart_SizeChanged(object? s, SizeChangedEventArgs e) => DrawHistoryChart();
    void DrawHistoryChart()
    {
        HistoryChart.Children.Clear();
        var rs = _historyCache.TakeLast(30).ToList(); double w = HistoryChart.Bounds.Width, h = HistoryChart.Bounds.Height;
        if (rs.Count == 0 || w <= 0 || h <= 0) return;
        double maxMin = Math.Max(1, rs.Max(r => r.DurationSec / 60)), maxV = Math.Max(1, rs.Max(r => r.PeakViewers)), slot = w / 30;
        var accent = Res("Accent"); var dim = Res("Dim");
        var line = new Polyline { Stroke = Brushes.Orange, StrokeThickness = 1.5 };
        for (int i = 0; i < rs.Count; i++)
        {
            double bh = rs[i].DurationSec / 60 / maxMin * (h - 20), x = i * slot + slot * 0.15;
            var bar = new Rectangle { Width = slot * 0.7, Height = Math.Max(1, bh), Fill = accent, Opacity = 0.8};
            ToolTip.SetTip(bar, $"{rs[i].Start:g}: {rs[i].DurationSec / 60:0} min, peak {rs[i].PeakViewers}");
            Canvas.SetLeft(bar, x); Canvas.SetTop(bar, h - bar.Height); HistoryChart.Children.Add(bar);
            double vy = h - rs[i].PeakViewers / maxV * (h - 20) - 2;
            line.Points.Add(new Point(x + slot * 0.35, vy));
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Brushes.Orange }; Canvas.SetLeft(dot, x + slot * 0.35 - 3); Canvas.SetTop(dot, vy - 3); HistoryChart.Children.Add(dot);
        }
        HistoryChart.Children.Add(line);
        var lbl = new TextBlock { Text = $"max {maxMin:0} min · {maxV:0} viewers", Foreground = dim, FontSize = 10 }; Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, 2); HistoryChart.Children.Add(lbl);
    }
    void HistoryRefresh_Click(object? s, RoutedEventArgs e) => RefreshHistory();
    void HistoryExport_Click(object? s, RoutedEventArgs e)
    {
        var d = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"EpikStreamer-history-{DateTime.Now:yyyy-MM-dd}.csv" };
        if (d.ShowDialog(this) != true) return;
        try { File.WriteAllText(d.FileName, HistoryStore.ToCsv(_history.Load()), new System.Text.UTF8Encoding(true)); StatusText.Text = "Exported " + d.FileName; }
        catch (Exception ex) { MessageBox.Show(this, "Export failed: " + ex.Message); }
    }
    void HistoryClear_Click(object? s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Delete all session history?", "History", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _history.Clear(); RefreshHistory();
    }

    // ---------- schedule ----------
    void RefreshSchedule(ScheduleEntry? select = null)
    {
        _loading = true;
        SchedProfile.Items.Clear(); SchedProfile.Items.Add("(active profile)"); foreach (var p in _store.Profiles) SchedProfile.Items.Add(p.Name);
        ScheduleList.Items.Clear();
        foreach (var e in _store.Schedule) ScheduleList.Items.Add(new ListBoxItem { Content = $"{(e.Enabled ? "●" : "○")} {e.Name} — {Scheduler.Describe(e)}" + (e.Profile.Length > 0 ? $" · {e.Profile}" : ""), Tag = e });
        _loading = false;
        if (select != null) foreach (ListBoxItem i in ScheduleList.Items) if (i.Tag == select) ScheduleList.SelectedItem = i;
        if (ScheduleList.SelectedItem == null) LoadScheduleEditor(new ScheduleEntry());
        UpdateNextScheduled();
    }
    ScheduleEntry? SelectedSchedule => (ScheduleList.SelectedItem as ListBoxItem)?.Tag as ScheduleEntry;
    void ScheduleList_SelectionChanged(object? s, SelectionChangedEventArgs e) { if (!_loading && SelectedSchedule is { } x) LoadScheduleEditor(x); }
    void LoadScheduleEditor(ScheduleEntry e)
    {
        SchedName.Text = e.Name; SchedRepeat.SelectedIndex = (int)e.Repeat; SchedEnabled.IsChecked = e.Enabled; SchedDate.SelectedDate = e.Date;
        foreach (CheckBox c in SchedDays.Children) c.IsChecked = e.Days.Contains(Enum.Parse<DayOfWeek>((string)c.Tag));
        SchedStart.Text = e.Start.ToString(@"hh\:mm"); SchedStop.Text = e.Stop?.ToString(@"hh\:mm") ?? "";
        SchedProfile.SelectedItem = e.Profile.Length > 0 && SchedProfile.Items.Contains(e.Profile) ? e.Profile : "(active profile)";
        SchedPlaylist.Text = e.PlaylistFile; SchedRepeat_Changed(this, null!);
    }
    void SchedRepeat_Changed(object? s, SelectionChangedEventArgs e) { if (SchedDays != null) SchedDays.IsEnabled = SchedRepeat.SelectedIndex == 2; }
    bool ReadScheduleEditor(ScheduleEntry e)
    {
        if (!TimeSpan.TryParseExact(SchedStart.Text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var start)) { MessageBox.Show(this, "Start time must be HH:mm (24h)."); return false; }
        TimeSpan? stop = null;
        if (SchedStop.Text.Trim().Length > 0)
        {
            if (!TimeSpan.TryParseExact(SchedStop.Text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var st)) { MessageBox.Show(this, "Stop time must be HH:mm (24h) or blank."); return false; }
            stop = st;
        }
        if (SchedPlaylist.Text.Trim().Length > 0 && !File.Exists(SchedPlaylist.Text.Trim())) { MessageBox.Show(this, "Playlist file not found."); return false; }
        e.Name = SchedName.Text.Trim().Length > 0 ? SchedName.Text.Trim() : "Scheduled stream";
        e.Repeat = (Recurrence)Math.Max(0, SchedRepeat.SelectedIndex); e.Enabled = SchedEnabled.IsChecked == true; e.Date = (SchedDate.SelectedDate ?? DateTime.Today).Date;
        e.Days = SchedDays.Children.Cast<CheckBox>().Where(c => c.IsChecked == true).Select(c => Enum.Parse<DayOfWeek>((string)c.Tag)).ToList();
        e.Start = start; e.Stop = stop;
        e.Profile = SchedProfile.SelectedItem is string p && p != "(active profile)" ? p : "";
        e.PlaylistFile = SchedPlaylist.Text.Trim();
        return true;
    }
    void SchedAdd_Click(object? s, RoutedEventArgs e) { var x = new ScheduleEntry(); if (!ReadScheduleEditor(x)) return; _store.Schedule.Add(x); Persist(); RefreshSchedule(x); }
    void SchedUpdate_Click(object? s, RoutedEventArgs e) { if (SelectedSchedule is not { } x) { SchedAdd_Click(s, e); return; } if (!ReadScheduleEditor(x)) return; Persist(); RefreshSchedule(x); }
    void SchedDelete_Click(object? s, RoutedEventArgs e) { if (SelectedSchedule is not { } x) return; _store.Schedule.Remove(x); Persist(); RefreshSchedule(); }
    void SchedBrowse_Click(object? s, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Playlists|*.m3u;*.m3u8;*.txt|All files|*.*" }; if (d.ShowDialog(this) == true) SchedPlaylist.Text = d.FileName; }
    void SchedulerEnable_Click(object? s, RoutedEventArgs e) { _store.SchedulerEnabled = SchedulerEnableCheck.IsChecked == true; try { _store.Save(); } catch { } UpdateNextScheduled(); }

    void UpdateNextScheduled()
    {
        var now = DateTime.Now;
        var next = _store.Schedule.Select(x => (x, t: Scheduler.NextStart(x, now))).Where(x => x.t != null && x.t > now).OrderBy(x => x.t).FirstOrDefault();
        SchedNextText.Text = !_store.SchedulerEnabled ? "Scheduler is off." :
            _runningSchedule != null ? $"Running \"{_runningSchedule.Name}\"" + (_scheduleEnd < DateTime.MaxValue ? $" until {_scheduleEnd:HH:mm}" : "") :
            next.t is DateTime t ? $"Next: \"{next.x.Name}\" {t:ddd yyyy-MM-dd HH:mm}" : "Nothing scheduled.";
    }

    void SchedulerTick()
    {
        UpdateNextScheduled();
        if (!_store.SchedulerEnabled) return;
        var now = DateTime.Now; bool running = _engine?.IsRunning == true;
        if (_runningSchedule != null && running && now >= _scheduleEnd)
        {
            Log($"⏰ Scheduled stop: {_runningSchedule.Name}"); Notify("Scheduled stop", _runningSchedule.Name);
            _runningSchedule = null; _ = StopEngine(); return;
        }
        if (!running && _runningSchedule != null) _runningSchedule = null;
        foreach (var (entry, start) in Scheduler.Due(_store.Schedule, now, _firedSchedules))
        {
            if (running) { Log($"⏰ Skipped \"{entry.Name}\": a stream is already running."); continue; }
            StartScheduled(entry, start); break;
        }
    }

    void StartScheduled(ScheduleEntry e, DateTime start)
    {
        Log($"⏰ Scheduled start: {e.Name}");
        if (e.Profile.Length > 0 && _store.Profiles.FirstOrDefault(p => p.Name == e.Profile) is { } prof && prof != _cur)
        { SaveUiTo(_cur); _cur = prof; RefreshProfileCombo(); LoadProfile(_cur); }
        _playlistOverride = null;
        if (e.PlaylistFile.Length > 0)
        {
            try { _playlistOverride = Scheduler.LoadPlaylistFile(e.PlaylistFile); }
            catch (Exception ex) { Log("Schedule playlist failed: " + ex.Message); Notify("Scheduled stream failed", ex.Message, true); return; }
        }
        _runningSchedule = e; _scheduleEnd = Scheduler.End(e, start);
        _quiet = true;
        try { Start_Click(this, new RoutedEventArgs()); } finally { _quiet = false; }
    }

    /// <summary>MessageBox for interactive starts; a log line + notification for scheduled/hotkey starts while hidden.</summary>
    void Warn(string msg)
    {
        if (_quiet || !IsVisible) { Log("⚠ " + msg); Notify("Can't start stream", msg, true); _runningSchedule = null; }
        else MessageBox.Show(this, msg);
    }
}
