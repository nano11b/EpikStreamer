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

public partial class MainWindow : Window
{
    ProfileStore _store;
    Profile _cur;
    StreamEngine? _engine;
    bool _loading;
    readonly Dictionary<OutputTarget, string> _keys = new(); // in-memory keys per output; persisted only (keyring) when "Remember" is on
    OutputTarget? _editOut;
    readonly ChatPanel _chat = new();
    readonly Dictionary<int, (string Name, DestState State, string Msg)> _destStatus = new();
    readonly List<double> _bitrates = new();
    readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _lastPreviewWrite;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const string VideoFilter = "Video files|*.mp4;*.mkv;*.mov;*.m4v;*.avi;*.webm;*.flv;*.ts;*.wmv;*.mpg;*.mpeg|All files|*.*";

    public MainWindow()
    {
        InitializeComponent();
        foreach (var c in Enum.GetValues<Corner>()) { ImgCorner.Items.Add(c); TxtCorner.Items.Add(c); ClkCorner.Items.Add(c); NpCorner.Items.Add(c); }
        foreach (var (id, label) in FfmpegLocator.Encoders) EncoderCombo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        foreach (var t in CommandBuilder.Transitions) TransitionCombo.Items.Add(t);
        foreach (var pr in OutputTarget.Presets) OutPresetCombo.Items.Add(pr.Name);
        _store = ProfileStore.Load();
        UpdateUrlBox.Text = _store.UpdateManifestUrl; CheckOnStartupCheck.IsChecked = _store.CheckUpdatesOnStartup;
        GhTokenBox.Text = Secret.Unprotect(_store.ProtectedGitHubToken);
        _chat.Compact = _store.ChatCompact;
        _chat.StatusChanged += (v, live) => Dispatcher.UIThread.BeginInvoke(() => ShowChatStatus(v, live));
        Loaded += async (_, _) =>
        {
            if (_store.ChatPanelVisible) { ChatToggle.IsChecked = true; await ShowChat(true); }
            if (_store.CheckUpdatesOnStartup) await CheckForUpdates(interactive: false);
        };
        _cur = _store.Current;
        RefreshProfileCombo();
        LoadProfile(_cur);
        UpdateFfmpegStatus();
        _previewTimer.Tick += (_, _) => RefreshPreview();
        Init22();
        WireLinux();
    }

    // ---------- profile <-> UI ----------
    void RefreshProfileCombo()
    {
        _loading = true;
        ProfileCombo.Items.Clear();
        foreach (var p in _store.Profiles) ProfileCombo.Items.Add(p.Name);
        ProfileCombo.SelectedItem = _cur.Name;
        _loading = false;
    }

    void LoadProfile(Profile p)
    {
        _loading = true;
        p.EnsureOutputs();
        RememberKeyCheck.IsChecked = p.RememberKey;
        foreach (var o2 in p.Outputs) if (!_keys.ContainsKey(o2) && p.RememberKey) _keys[o2] = Secret.Unprotect(o2.ProtectedKey);
        _editOut = null; RefreshOutputs(p.Outputs.FirstOrDefault());
        TransitionCombo.SelectedItem = CommandBuilder.Transitions.Contains(p.Transition) ? p.Transition : "fade";
        XfadeSlider.Value = Math.Clamp(p.CrossfadeSeconds, 0, 5);
        RecEnableCheck.IsChecked = p.RecordEnabled; RemuxCheck.IsChecked = p.RemuxToMp4; RecFolderBox.Text = p.RecordFolder; RecPatternBox.Text = p.RecordPattern;
        foreach (ComboBoxItem i in EncoderCombo.Items) if ((string)i.Tag == p.Encoder) EncoderCombo.SelectedItem = i;
        if (EncoderCombo.SelectedItem == null) EncoderCombo.SelectedIndex = 0;
        WidthBox.Text = p.Width.ToString(); HeightBox.Text = p.Height.ToString(); FpsBox.Text = p.Fps.ToString();
        VBitBox.Text = p.VideoKbps.ToString(); ABitBox.Text = p.AudioKbps.ToString(); KeyIntBox.Text = p.KeyframeSeconds.ToString();
        AudienceCombo.SelectedIndex = p.Audience == "Private" ? 1 : 0;
        RetriesBox.Text = p.MaxRetries.ToString(); FfmpegBox.Text = p.FfmpegPath;
        LoopCheck.IsChecked = p.LoopPlaylist; ShuffleCheck.IsChecked = p.Shuffle;
        PlaylistBox.Items.Clear(); foreach (var f in p.Playlist) PlaylistBox.Items.Add(f);
        var o = p.Overlays;
        ImgEnable.IsChecked = o.ImageEnabled; ImgPath.Text = o.ImagePath; ImgCorner.SelectedItem = o.ImageCorner;
        ImgScale.Value = o.ImageScale * 100; ImgOpacity.Value = o.ImageOpacity * 100;
        TxtEnable.IsChecked = o.TextEnabled; TxtText.Text = o.Text; TxtSize.Text = o.FontSize.ToString(); TxtColor.Text = o.FontColor;
        TxtCorner.SelectedItem = o.TextCorner; TxtBox.IsChecked = o.TextBackground; TxtTicker.IsChecked = o.Ticker; TxtSpeed.Text = o.TickerSpeed.ToString();
        FontPath.Text = o.FontFile;
        ClkEnable.IsChecked = o.ClockEnabled; ClkFormat.Text = o.ClockFormat; ClkSize.Text = o.ClockFontSize.ToString(); ClkCorner.SelectedItem = o.ClockCorner;
        LoadAudio(p); LoadNowPlaying(p); LoadBallPit(p);
        _loading = false;
    }

    static int Int(string t, int def, int min = 1, int max = 100000) => int.TryParse(t.Trim(), out var v) ? Math.Clamp(v, min, max) : def;

    void SaveUiTo(Profile p)
    {
        CommitOutputEditor();
        p.RememberKey = RememberKeyCheck.IsChecked == true;
        foreach (var o2 in p.Outputs) o2.ProtectedKey = p.RememberKey ? Secret.Protect(_keys.GetValueOrDefault(o2, "")) : null;
        var first = p.Outputs.FirstOrDefault(); if (first != null) { p.ServerUrl = first.ServerUrl; p.ProtectedKey = null; }  // legacy fields: no key duplication
        p.Transition = TransitionCombo.SelectedItem as string ?? "fade"; p.CrossfadeSeconds = XfadeSlider.Value;
        p.RecordEnabled = RecEnableCheck.IsChecked == true; p.RemuxToMp4 = RemuxCheck.IsChecked == true;
        p.RecordFolder = RecFolderBox.Text.Trim(); p.RecordPattern = RecPatternBox.Text.Trim();
        p.Encoder = (EncoderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "libx264";
        p.Width = Int(WidthBox.Text, 1280, 16, 7680) / 2 * 2; p.Height = Int(HeightBox.Text, 720, 16, 4320) / 2 * 2;
        p.Fps = Int(FpsBox.Text, 30, 1, 120); p.VideoKbps = Int(VBitBox.Text, 2500, 100, 100000);
        p.AudioKbps = Int(ABitBox.Text, 128, 32, 512); p.KeyframeSeconds = Int(KeyIntBox.Text, 2, 1, 20);
        p.Audience = AudienceCombo.SelectedIndex == 1 ? "Private" : "Public";
        p.MaxRetries = Int(RetriesBox.Text, 10, 0, 1000); p.FfmpegPath = FfmpegBox.Text.Trim();
        p.LoopPlaylist = LoopCheck.IsChecked == true; p.Shuffle = ShuffleCheck.IsChecked == true;
        p.Playlist = PlaylistBox.Items.Cast<string>().ToList();
        var o = p.Overlays;
        o.ImageEnabled = ImgEnable.IsChecked == true; o.ImagePath = ImgPath.Text.Trim(); o.ImageCorner = (Corner)(ImgCorner.SelectedItem ?? Corner.TopRight);
        o.ImageScale = ImgScale.Value / 100.0; o.ImageOpacity = ImgOpacity.Value / 100.0;
        o.TextEnabled = TxtEnable.IsChecked == true; o.Text = TxtText.Text; o.FontSize = Int(TxtSize.Text, 36, 6, 400); o.FontColor = TxtColor.Text.Trim();
        o.TextCorner = (Corner)(TxtCorner.SelectedItem ?? Corner.BottomLeft); o.TextBackground = TxtBox.IsChecked == true;
        o.Ticker = TxtTicker.IsChecked == true; o.TickerSpeed = Int(TxtSpeed.Text, 120, 10, 5000); o.FontFile = FontPath.Text.Trim();
        o.ClockEnabled = ClkEnable.IsChecked == true; o.ClockFormat = ClkFormat.Text; o.ClockFontSize = Int(ClkSize.Text, 28, 6, 400);
        o.ClockCorner = (Corner)(ClkCorner.SelectedItem ?? Corner.TopLeft);
        SaveAudio(p); SaveNowPlaying(p); if (_pit != null) SaveBallPit(p);
    }

    void Persist()
    {
        SaveUiTo(_cur);
        _store.UpdateManifestUrl = string.IsNullOrWhiteSpace(UpdateUrlBox.Text) ? Updater.DefaultManifestUrl : UpdateUrlBox.Text.Trim();
        _store.CheckUpdatesOnStartup = CheckOnStartupCheck.IsChecked == true;
        _store.ProtectedGitHubToken = GhTokenBox.Text.Length > 0 ? Secret.Protect(GhTokenBox.Text) : null;
        _store.ChatPanelVisible = ChatToggle.IsChecked == true; _store.ChatCompact = _chat.Compact;
        SaveAppSettings();
        try { _store.Active = _cur.Name; _store.Save(); } catch (Exception ex) { Log("Could not save profiles: " + ex.Message); }
    }

    // ---------- profile management ----------
    void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileCombo.SelectedItem is not string name || name == _cur.Name) return;
        if (_engine?.IsRunning == true) { MessageBox.Show(this, "Stop the stream before switching profiles."); RefreshProfileCombo(); return; }
        SaveUiTo(_cur);
        _cur = _store.Profiles.First(p => p.Name == name);
        LoadProfile(_cur); Persist();
    }

    string? AskName(string title, string initial)
    {
        while (true)
        {
            var name = Prompt.Show(this, title, initial)?.Trim();
            if (string.IsNullOrEmpty(name)) return null;
            bool renaming = title == "Rename profile";
            if (_store.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (!renaming || p != _cur)))
            { MessageBox.Show(this, "A profile with that name already exists."); initial = name; continue; }
            return name;
        }
    }
    string Unique(string baseName) { var n = baseName; int i = 2; while (_store.Profiles.Any(p => p.Name == n)) n = $"{baseName} {i++}"; return n; }

    void NewProfile_Click(object? s, RoutedEventArgs e)
    {
        if (Busy()) return;
        var name = AskName("New profile name", Unique("Profile")); if (name == null) return;
        SaveUiTo(_cur);
        _cur = new Profile { Name = name }; _store.Profiles.Add(_cur);
        RefreshProfileCombo(); LoadProfile(_cur); Persist();
    }
    void RenameProfile_Click(object? s, RoutedEventArgs e)
    {
        if (Busy()) return;
        var name = AskName("Rename profile", _cur.Name); if (name == null || name == _cur.Name) return;
        _cur.Name = name; RefreshProfileCombo(); Persist();
    }
    void DuplicateProfile_Click(object? s, RoutedEventArgs e)
    {
        if (Busy()) return;
        SaveUiTo(_cur);
        var name = AskName("Duplicate profile as", Unique(_cur.Name + " copy")); if (name == null) return;
        var copy = _cur.Clone(name); _store.Profiles.Add(copy);
        for (int i = 0; i < copy.Outputs.Count && i < _cur.Outputs.Count; i++) _keys[copy.Outputs[i]] = _keys.GetValueOrDefault(_cur.Outputs[i], "");
        _cur = copy; RefreshProfileCombo(); LoadProfile(_cur); Persist();
    }
    void DeleteProfile_Click(object? s, RoutedEventArgs e)
    {
        if (Busy()) return;
        if (_store.Profiles.Count <= 1) { MessageBox.Show(this, "You need at least one profile."); return; }
        if (MessageBox.Show(this, $"Delete profile \"{_cur.Name}\"?", "EpikStreamer", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _store.Profiles.Remove(_cur); foreach (var o2 in _cur.Outputs) _keys.Remove(o2);
        _cur = _store.Profiles[0]; RefreshProfileCombo(); LoadProfile(_cur); Persist();
    }
    void SaveProfile_Click(object? s, RoutedEventArgs e) { Persist(); StatusText.Text = $"Saved profile \"{_cur.Name}\"."; }
    bool Busy() { if (_engine?.IsRunning == true) { MessageBox.Show(this, "Stop the stream first."); return true; } return false; }

    // ---------- playlist ----------
    void AddFiles_Click(object? s, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Filter = VideoFilter, Multiselect = true };
        if (d.ShowDialog(this) == true) foreach (var f in d.FileNames) PlaylistBox.Items.Add(f);
    }
    void Playlist_Drop(object? s, DragEventArgs e)
    {
        if (FileDrops(e) is string[] files) foreach (var f in files) if (File.Exists(f)) PlaylistBox.Items.Add(f);
    }
    void RemoveFile_Click(object? s, RoutedEventArgs e) { foreach (var i in PlaylistBox.SelectedItems.Cast<object>().ToList()) PlaylistBox.Items.Remove(i); }
    void ClearList_Click(object? s, RoutedEventArgs e) => PlaylistBox.Items.Clear();
    void MoveUp_Click(object? s, RoutedEventArgs e) => Move(-1);
    void MoveDown_Click(object? s, RoutedEventArgs e) => Move(1);
    void Move(int d)
    {
        int i = PlaylistBox.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= PlaylistBox.Items.Count) return;
        var item = PlaylistBox.Items[i]; PlaylistBox.Items.RemoveAt(i); PlaylistBox.Items.Insert(j, item); PlaylistBox.SelectedIndex = j;
    }

    // ---------- EpikChat sign-in ----------
    void EpikSignIn_Click(object? s, RoutedEventArgs e)
    {
        if (Busy()) return;
        var audience = AudienceCombo.SelectedIndex == 1 ? "Private" : "Public";
        var w = new SignInWindow(this, audience);
        if (w.ShowDialog() == true && w.StreamKey != null)
        {
            CommitOutputEditor();
            var target = _editOut?.Preset == "EpikChat" ? _editOut : _cur.Outputs.FirstOrDefault(o => o.Preset == "EpikChat");
            if (target == null) { target = new OutputTarget(); _cur.Outputs.Insert(0, target); }
            target.ServerUrl = w.ServerUrl ?? EpikChatAutomation.ExpectedServer; target.Enabled = true;
            _keys[target] = w.StreamKey;
            _editOut = null; RefreshOutputs(target);
            Persist();
            StatusText.Text = "Stream key received from EpikChat and filled into profile \"" + _cur.Name + "\".";
        }
    }
    void EpikSignOut_Click(object? s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Clear EpikStreamer's saved EpikChat sign-in (WebView2 profile)?", "Sign out", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        if (_chat.IsInitialized2) { _ = _chat.ClearBrowsingData(); StatusText.Text = "Signed out of EpikChat (cookies and site data cleared)."; return; }
        StatusText.Text = SignInWindow.DeleteProfileFolder() ? "Signed out of EpikChat (browser profile cleared)." : "Couldn't clear the browser profile (close the sign-in window and try again).";
    }

    // ---------- browse ----------
    void BrowseImage_Click(object? s, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*" }; if (d.ShowDialog(this) == true) { ImgPath.Text = d.FileName; ImgEnable.IsChecked = true; } }
    void BrowseFont_Click(object? s, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "Fonts|*.ttf;*.otf;*.ttc|All files|*.*", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts) }; if (d.ShowDialog(this) == true) FontPath.Text = d.FileName; }
    void BrowseFfmpeg_Click(object? s, RoutedEventArgs e) { var d = new OpenFileDialog { Filter = "ffmpeg|ffmpeg|All files|*" }; if (d.ShowDialog(this) == true) { FfmpegBox.Text = d.FileName; UpdateFfmpegStatus(); } }

    // ---------- ffmpeg ----------
    string? UpdateFfmpegStatus()
    {
        var ff = FfmpegLocator.Find(FfmpegBox.Text.Trim());
        FfmpegStatus.Text = ff != null ? "Using: " + ff : "ffmpeg not found. Install it with: sudo apt install ffmpeg  (or browse to an ffmpeg binary).";
        FfmpegStatus.Foreground = ff != null ? Res("Dim") : Brushes.OrangeRed;
        return ff;
    }

    async void Download_Click(object? s, RoutedEventArgs e) => await DownloadFfmpeg();
    Task<string?> DownloadFfmpeg()
    {
        // Linux: ffmpeg comes from the distribution (the .deb depends on it); never download binaries ourselves
        MessageBox.Show(this, "EpikStreamer uses the system ffmpeg on Linux.\n\nInstall it with:\n    sudo apt install ffmpeg\n\nthen click Refresh / Detect again.", "ffmpeg", MessageBoxButton.OK, MessageBoxImage.Information);
        UpdateFfmpegStatus();
        return Task.FromResult(UpdateFfmpegStatus());
    }

    async Task<string?> RequireFfmpeg()
    {
        var ff = UpdateFfmpegStatus();
        if (ff != null) return ff;
        var msg = string.IsNullOrWhiteSpace(FfmpegBox.Text) ? "ffmpeg wasn't found on PATH." : $"ffmpeg not found at:\n{FfmpegBox.Text}";
        MessageBox.Show(this, msg + "\n\nInstall it with:\n    sudo apt install ffmpeg", "ffmpeg missing", MessageBoxButton.OK, MessageBoxImage.Warning);
        return await DownloadFfmpeg();
    }

    async void Detect_Click(object? s, RoutedEventArgs e)
    {
        var ff = await RequireFfmpeg(); if (ff == null) return;
        DetectBtn.IsEnabled = false; DetectText.Text = "Testing encoders…";
        var ok = await FfmpegLocator.DetectEncodersAsync(ff);
        DetectBtn.IsEnabled = true;
        DetectText.Text = ok.Count == 0 ? "No encoder worked (is this ffmpeg build missing libx264?)" :
            "Working: " + string.Join(", ", FfmpegLocator.Encoders.Where(x => ok.Contains(x.Id)).Select(x => x.Label));
        // prefer hardware if available
        var pick = new[] { "h264_nvenc", "h264_vaapi", "h264_qsv", "libx264" }.FirstOrDefault(ok.Contains);
        if (pick != null) foreach (ComboBoxItem i in EncoderCombo.Items) if ((string)i.Tag == pick) EncoderCombo.SelectedItem = i;
    }

    // ---------- streaming ----------
    async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_engine?.IsRunning == true) return;
        SaveUiTo(_cur);
        var p = _cur;
        var playlistOverride = _playlistOverride; _playlistOverride = null;
        if (playlistOverride != null) { p = p.Clone(p.Name); p.Playlist = playlistOverride; }
        if (p.Playlist.Count == 0 && p.BallPit.Mode != "Source") { Warn("Add at least one video to the playlist."); Tabs.SelectedIndex = 0; return; }
        var missing = p.Playlist.Where(f => !File.Exists(f)).ToList();
        if (missing.Count == p.Playlist.Count) { Warn("None of the playlist files exist."); return; }
        var dests = new List<CommandBuilder.Destination>(); var secrets = new List<string>(); var destNames = new List<string>();
        foreach (var o2 in p.Outputs.Where(x => x.Enabled))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(o2.ServerUrl, "^rtmps?://", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            { Warn($"Output \"{o2.Name}\": server URL must start with rtmp:// or rtmps://"); Tabs.SelectedIndex = 2; RefreshOutputs(o2); return; }
            var k = _keys.GetValueOrDefault(o2, "").Trim();
            if (k.Length == 0 && _store.Chat.AutoGoLive && IsEpikOutput(o2)) { dests.Add(new CommandBuilder.Destination(o2.Name, "")); destNames.Add(o2.Name); continue; }   // v2.4.1: key comes from Go live
            if (k.Length == 0 && o2.Preset != "Custom") { Warn($"Enter the stream key for \"{o2.Name}\"."); Tabs.SelectedIndex = 2; RefreshOutputs(o2); return; }
            dests.Add(new CommandBuilder.Destination(o2.Name, CommandBuilder.JoinUrl(o2.ServerUrl, k))); destNames.Add(o2.Name);
            if (k.Length > 0) secrets.Add(k);
        }
        if (dests.Count == 0 && !p.RecordEnabled) { Warn("Enable at least one output (or turn on recording)."); Tabs.SelectedIndex = 2; return; }
        if (p.Overlays.ImageEnabled && !File.Exists(p.Overlays.ImagePath)) { Warn("Overlay image not found."); Tabs.SelectedIndex = 1; return; }
        var ff = await RequireFfmpeg(); if (ff == null) return;
        Persist();

        // v2.4.1: EpikChat only accepts the ingest once a live session exists (camera icon → Go live). Create it first.
        var enabledOuts = p.Outputs.Where(x => x.Enabled).ToList();
        var epikOuts = enabledOuts.Where(IsEpikOutput).ToList();
        LiveSession? goLive = null; LiveSession.Result? goLiveResult = null;
        if (_store.Chat.AutoGoLive && epikOuts.Count > 0)
        {
            StartBtn.IsEnabled = false;
            try { (goLive, goLiveResult) = await EnsureEpikLiveSession(p, epikOuts, reconnect: false); }
            finally { StartBtn.IsEnabled = true; }
            if (_engine?.IsRunning == true) return;
            for (int i = 0; i < enabledOuts.Count; i++)
            {
                if (!IsEpikOutput(enabledOuts[i])) continue;
                var k = _keys.GetValueOrDefault(enabledOuts[i], "").Trim();
                if (k.Length == 0) { Warn($"No stream key for \"{enabledOuts[i].Name}\": Go live on EpikChat didn't provide one. {LiveSession.Fallback}"); Tabs.SelectedIndex = 2; return; }
                dests[i] = dests[i] with { Target = CommandBuilder.JoinUrl(enabledOuts[i].ServerUrl, k) };
                if (!secrets.Contains(k)) secrets.Add(k);
            }
            if (goLiveResult?.Outcome == LiveSession.Outcome.Failed) Log("⚠ Starting the encoder anyway; it keeps retrying until EpikChat accepts the stream.");
            GoLiveStatus(goLiveResult?.Outcome == LiveSession.Outcome.Failed ? "Starting encoder (Go live not confirmed: " + LiveSession.Fallback + ")" : "Live session ready, starting encoder…");
        }

        var run = p.Clone(p.Name); // snapshot, so UI edits don't affect the running stream
        var chatCfg = _store.Chat;
        try
        {
            _engine = new StreamEngine(run, ff, dests, secrets);
            HookEngine23(_engine);
            _engine.BallPit = run.BallPit.Active ? _pit?.Scene : null; _bpSeq = -1;
            _engine.TextOverlays = (chatCfg.CommandsEnabled && chatCfg.ShowOnStream) || chatCfg.AlertsEnabled;
            _engine.SetMusicMuted(_musicMuted); _engine.SetMicMuted(_micMuted); _engine.SetOverlaysHidden(_overlaysHidden);
            BeginSession(run, destNames);
            _destStatus.Clear(); for (int i = 0; i < destNames.Count; i++) _destStatus[i] = (destNames[i], DestState.Connecting, "");
            if (run.RecordEnabled) _destStatus[-1] = ("Recording", DestState.Live, "");
            RenderDests();
            _engine.DestinationStatus += (i, st, msg) => Dispatcher.UIThread.BeginInvoke(() => { if (_destStatus.TryGetValue(i, out var d)) { _destStatus[i] = (d.Name, st, msg); RenderDests(); } });
            _engine.Log += Log;
            _engine.State += st => Dispatcher.UIThread.BeginInvoke(() => { StatusText.Text = st; OnEngineState(st); });
            _engine.Stats += st => Dispatcher.UIThread.BeginInvoke(() => ShowStats(st));
            if (_store.Chat.AutoGoLive && epikOuts.Count > 0) _engine.BeforeReconnect = MakeReconnectHook(run, enabledOuts);
            _engine.Finished += () => Dispatcher.UIThread.BeginInvoke(OnFinished);
            _bitrates.Clear(); DrawGraph(); PreviewImg.Source = null; _lastPreviewWrite = default;
            _engine.Start();
        }
        catch (Exception ex) { Warn("Could not start: " + ex.Message); _engine = null; _session = null; return; }
        StartBtn.IsEnabled = false; StopBtn.IsEnabled = true; SkipBtn.IsEnabled = true;
        StatusText.Text = "Connecting…"; Tabs.SelectedItem = StatsTab;
        if (goLive != null && goLiveResult?.Outcome == LiveSession.Outcome.Created) _ = FinalizeGoLive(goLive);
        _previewTimer.Start();
        Notify("Stream starting", $"{run.Name}: {string.Join(", ", destNames.DefaultIfEmpty("recording only"))}");
        UpdateControls();
    }

    string _lastState = "";
    void OnEngineState(string st)
    {
        if (st.StartsWith("Reconnecting in") && !_lastState.StartsWith("Reconnecting")) Notify("Connection lost", st, true);
        else if (st.StartsWith("Live") && _lastState.StartsWith("Reconnecting")) Notify("Reconnected", st);
        else if (st.StartsWith("Error") || st.StartsWith("Failed")) Notify("Stream error", st, true);
        _lastState = st; UpdateControls();
    }

    void OnFinished()
    {
        if (_engine?.Recordings.Count > 0) Log("Recordings: " + string.Join(", ", _engine.Recordings));
        if (_destStatus.ContainsKey(-1)) { _destStatus[-1] = ("Recording", DestState.Stopped, ""); RenderDests(); }
        _previewTimer.Stop();
        StartBtn.IsEnabled = true; StopBtn.IsEnabled = false; SkipBtn.IsEnabled = false;
        var reason = string.IsNullOrEmpty(_lastState) ? "Stopped" : _lastState;
        EndSession(reason); _runningSchedule = null;
        Notify("Stream ended", reason, reason.StartsWith("Error") || reason.StartsWith("Failed"));
        _lastState = ""; UpdateControls();
    }

    void Skip_Click(object? s, RoutedEventArgs e) => _engine?.Skip();
    async void Stop_Click(object? s, RoutedEventArgs e) => await StopEngine();
    async Task StopEngine()
    {
        var eng = _engine; if (eng == null || !eng.IsRunning) return;
        StopBtn.IsEnabled = false; SkipBtn.IsEnabled = false; StatusText.Text = "Stopping…";
        eng.Stop();
        await Task.Run(() => eng.Wait(10000));
    }

    void ShowStats(StreamStats s)
    {
        _lastStats = s; _session?.OnStats(s);
        SFps.Text = s.Fps.ToString("0.0", Inv);
        SBitrate.Text = s.BitrateKbps > 0 ? $"{s.BitrateKbps:0} kbps" : "–";
        SSpeed.Text = s.Speed > 0 ? s.Speed.ToString("0.00x", Inv) : "–";
        SUptime.Text = s.Uptime.ToString(@"hh\:mm\:ss");
        SDrop.Text = $"{s.Dropped} / {s.Duplicated}";
        SReconn.Text = s.Reconnects.ToString();
        SFrames.Text = s.Frames.ToString();
        SItem.Text = $"{s.ItemIndex + 1} / {s.ItemCount}";
        SItemName.Text = $"{s.ItemName}  @ {TimeSpan.FromSeconds(s.ItemPosition):hh\\:mm\\:ss}";
        if (s.BitrateKbps > 0) { _bitrates.Add(s.BitrateKbps); if (_bitrates.Count > 120) _bitrates.RemoveAt(0); DrawGraph(); }
        UpdateControls();
    }

    void Graph_SizeChanged(object? s, SizeChangedEventArgs e) => DrawGraph();
    void DrawGraph()
    {
        var pts = new List<Point>();
        double w = Graph.Bounds.Width, h = Graph.Bounds.Height;
        if (_bitrates.Count > 1 && w > 0)
        {
            double max = Math.Max(_cur.VideoKbps * 1.3, _bitrates.Max()) ;
            for (int i = 0; i < _bitrates.Count; i++)
                pts.Add(new Point(w - (_bitrates.Count - 1 - i) * (w / 119.0), h - _bitrates[i] / max * (h - 4)));
            GraphMax.Text = $"{max:0} kbps";
        }
        GraphLine.Points = pts;
    }

    void RefreshPreview()
    {
        var path = _engine?.PreviewPath; if (path == null) return;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.LastWriteTimeUtc == _lastPreviewWrite) return;
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { bytes = new byte[fs.Length]; fs.ReadExactly(bytes); }
            var bmp = new Bitmap(new MemoryStream(bytes));
            (PreviewImg.Source as IDisposable)?.Dispose();
            PreviewImg.Source = bmp; _lastPreviewWrite = fi.LastWriteTimeUtc;
        }
        catch { /* partially written frame; try again next tick */ }
    }

    readonly Queue<string> _pendingLog = new();
    bool _logScheduled;
    void Log(string line)
    {
        lock (_pendingLog) { _pendingLog.Enqueue(line); if (_logScheduled) return; _logScheduled = true; }
        Dispatcher.UIThread.BeginInvoke(DispatcherPriority.Background, () =>
        {
            string chunk;
            lock (_pendingLog) { chunk = string.Join(Environment.NewLine, _pendingLog) + Environment.NewLine; _pendingLog.Clear(); _logScheduled = false; }
            LogBox.AppendText(chunk);
            if (LogBox.Text.Length > 200_000) LogBox.Text = LogBox.Text[^100_000..];
            LogBox.ScrollToEnd();
        });
    }

    bool _closing;
    async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) return;
        if (HideInsteadOfClose()) { e.Cancel = true; return; }
        Persist();
        if (_engine?.IsRunning == true) { e.Cancel = true; _closing = true; await StopEngine(); Close(); }
    }

    // ---------- outputs ----------
    void RefreshOutputs(OutputTarget? select)
    {
        _loading = true;
        OutputsList.Items.Clear();
        foreach (var o in _cur.Outputs) OutputsList.Items.Add(o);
        _loading = false;
        OutputsList.SelectedItem = select ?? _cur.Outputs.FirstOrDefault();
        if (OutputsList.SelectedItem == null) LoadOutputEditor(null);
    }

    void LoadOutputEditor(OutputTarget? o)
    {
        _loading = true;
        _editOut = o;
        bool on = o != null;
        OutNameBox.IsEnabled = ServerBox.IsEnabled = KeyBox.IsEnabled = OutPresetCombo.IsEnabled = OutEnabledCheck.IsEnabled = on;
        OutNameBox.Text = o?.Name ?? ""; ServerBox.Text = o?.ServerUrl ?? ""; KeyBox.Text = o != null ? _keys.GetValueOrDefault(o, "") : "";
        OutPresetCombo.SelectedItem = o?.Preset ?? "Custom"; OutEnabledCheck.IsChecked = o?.Enabled ?? false;
        OutHint.Text = o == null ? "Add an output." : OutputTarget.Presets.FirstOrDefault(x => x.Name == o.Preset).Hint ?? "";
        _loading = false;
    }

    void CommitOutputEditor()
    {
        var o = _editOut; if (o == null) return;
        o.Name = string.IsNullOrWhiteSpace(OutNameBox.Text) ? o.Preset : OutNameBox.Text.Trim();
        o.ServerUrl = ServerBox.Text.Trim(); o.Enabled = OutEnabledCheck.IsChecked == true;
        o.Preset = OutPresetCombo.SelectedItem as string ?? "Custom";
        _keys[o] = KeyBox.Text.Trim();
        RefreshOutputsList(o);
    }

    void OutputsList_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        CommitOutputEditor();
        LoadOutputEditor(OutputsList.SelectedItem as OutputTarget);
    }

    void OutPreset_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_loading || _editOut == null || OutPresetCombo.SelectedItem is not string name) return;
        var pr = OutputTarget.Presets.First(x => x.Name == name);
        if (pr.Url.Length > 0) ServerBox.Text = pr.Url;
        if (string.IsNullOrWhiteSpace(OutNameBox.Text) || OutputTarget.Presets.Any(x => x.Name == OutNameBox.Text)) OutNameBox.Text = name;
        OutHint.Text = pr.Hint;
    }

    void AddOutput_Click(object? s, RoutedEventArgs e)
    {
        CommitOutputEditor();
        var used = _cur.Outputs.Select(o => o.Preset).ToHashSet();
        var pr = OutputTarget.Presets.FirstOrDefault(x => !used.Contains(x.Name)); if (pr.Name == null) pr = OutputTarget.Presets[^1];
        var n = new OutputTarget { Name = pr.Name, Preset = pr.Name, ServerUrl = pr.Url, Enabled = true };
        _cur.Outputs.Add(n); _editOut = null; RefreshOutputs(n);
    }

    void RemoveOutput_Click(object? s, RoutedEventArgs e)
    {
        if (OutputsList.SelectedItem is not OutputTarget o) return;
        _cur.Outputs.Remove(o); _keys.Remove(o); _editOut = null; RefreshOutputs(null);
    }

    void BrowseRecFolder_Click(object? s, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Recording folder" };
        if (d.ShowDialog(this) == true) RecFolderBox.Text = d.FolderName;
    }

    void RenderDests()
    {
        DestList.Items.Clear();
        foreach (var (i, d) in _destStatus.OrderBy(k => k.Key < 0 ? int.MaxValue : k.Key))
        {
            var (dot, color) = d.State switch
            {
                DestState.Live => ("●", Brushes.LimeGreen), DestState.Failed => ("✖", Brushes.OrangeRed),
                DestState.Connecting => ("◌", Brushes.Gold), _ => ("○", Brushes.Gray)
            };
            DestList.Items.Add(new TextBlock { Text = $"{dot} {d.Name}: {d.State}{(d.Msg.Length > 0 ? " – " + d.Msg : "")}", Foreground = color });
        }
    }

    // ---------- chat panel ----------
    async void ChatToggle_Click(object? s, RoutedEventArgs e) => await ShowChat(ChatToggle.IsChecked == true);
    async Task ShowChat(bool on)
    {
        await _chat.SetActive(on);
    }

    void ShowChatStatus(int? viewers, bool live)
    {
        SViewers.Text = viewers?.ToString() ?? "–";
        OnViewerCount(viewers);
        SLive.Text = ChatToggle.IsChecked != true ? "–" : live ? "🔴 LIVE" : "offline";
        Title = "EpikStreamer 2.5.0" + (live ? " — 🔴 LIVE" : "") + (viewers is int v ? $" · {v} viewer{(v == 1 ? "" : "s")}" : "");
    }

    // ---------- updates ----------
    async void CheckUpdates_Click(object? s, RoutedEventArgs e) { Persist(); await CheckForUpdates(interactive: true); }
    async Task CheckForUpdates(bool interactive)
    {
        var url = string.IsNullOrWhiteSpace(UpdateUrlBox.Text) ? Updater.DefaultManifestUrl : UpdateUrlBox.Text.Trim();
        Updater.GitHubToken = GhTokenBox.Text.Length > 0 ? GhTokenBox.Text : null;
        try
        {
            StatusText.Text = "Checking for updates…";
            var info = await Updater.CheckAsync(url);
            if (info == null) { StatusText.Text = $"EpikStreamer {Updater.Current.ToString(3)} is up to date."; if (interactive) MessageBox.Show(this, StatusText.Text, "Updates"); return; }
            StatusText.Text = $"Update available: {info.Version.ToString(3)}";
            var notes = info.Notes.Length > 800 ? info.Notes[..800] + "…" : info.Notes;
            if (MessageBox.Show(this, $"EpikStreamer {info.Version.ToString(3)} is available (you have {Updater.Current.ToString(3)}).\n\n{notes}\n\nDownload and install it now?",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
            var path = await Updater.DownloadAsync(info, new Progress<double>(f => StatusText.Text = $"Downloading update… {f:P0}"));
            StatusText.Text = "Update downloaded and SHA-256 verified: " + path;
            Persist();
            await LinuxUpdate.Offer(this, path, _tray);
        }
        catch (Exception ex)
        {
            if (!interactive && ex.Message.StartsWith("This release has no Linux", StringComparison.Ordinal)) { StatusText.Text = ""; return; }   // Windows-only release: nothing to offer yet
            StatusText.Text = "Update check failed: " + ex.Message;
            if (interactive) MessageBox.Show(this, "Update check failed:\n" + ex.Message, "Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

/// <summary>Minimal dark text-input dialog.</summary>
static class Prompt
{
    public static string? Show(Window owner, string title, string initial) => Dialogs.AskText(owner, title, title, initial, allowEmpty: true);
}
