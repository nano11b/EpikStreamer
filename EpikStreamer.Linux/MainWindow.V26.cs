using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EpikStreamer.Core;
namespace EpikStreamer;

/// <summary>v2.6 "Radio": an internet radio station (e.g. AzuraCast) as background audio, with now-playing on the card,
/// a radio card + beat pulse in the Ball Pit, radio !song/!np/!lastsong and optional AzuraCast !request. Settings: Profile.Radio.</summary>
public partial class MainWindow
{
    RadioHost? _radio;
    bool _radioMuted;
    readonly DispatcherTimer _rdTick = new() { Interval = TimeSpan.FromSeconds(1) };
    string _rdArtShown = "";

    void Init26()
    {
        _radio = new RadioHost(_cur.Radio, Path.Combine(ProfileStore.Dir, "radio-art"));
        _radio.Log += t => UI(() => Log(t));
        _radio.SongChanged += np => UI(() => { _engine?.RadioTrackChanged(np.Key); RdShowNow(); });
        _radio.Updated += _ => UI(RdShowNow);
        _rdTick.Tick += (_, _) => RdTickNow(); _rdTick.Start();
        LoadRadio(_cur);
    }

    void RdTickNow()
    {
        if (_radio == null) return;
        var r = _cur.Radio;
        if (_pit != null)
        {
            _pit.Scene.RadioCard = _radio.PitCard();
            _pit.Scene.BeatStrength = r.Active && r.BeatPulse ? (float)Math.Clamp(r.BeatStrength, 0.05, 1) : 0;
        }
        _commands.RadioListed = r.Active && _radio.Current != null;
        _commands.RadioRequestsListed = r.Active && r.Requests && _radio.Target != null;
        if (IsVisible && Tabs.SelectedItem == RadioTab) RdShowNow();
    }

    void RdShowNow()
    {
        if (_radio == null) return;
        var np = _radio.Current; var r = _cur.Radio;
        if (!r.Active) { RdNowText.Text = "Radio is off."; RdNextText.Text = ""; }
        else if (np == null)
        {
            RdNowText.Text = _radio.Target == null ? "Playing the stream (no AzuraCast now-playing for this URL)." : r.NowPlaying ? "Waiting for now playing…" : "Now playing is off.";
            RdNextText.Text = _radio.Poller?.LastError is { Length: > 0 } e ? "⚠ " + e : "";
        }
        else
        {
            var pos = np.Duration > 0 ? $"  {LibraryItem.FormatTime(np.PositionAt(DateTime.UtcNow))} / {LibraryItem.FormatTime(np.Duration)}" : "  ● LIVE";
            RdNowText.Text = $"{(np.Online ? "ON AIR" : "OFF AIR")} · {np.Station}: {np.Display}{pos}";
            RdNextText.Text = (np.Next.Length > 0 ? "Up next: " + np.Next : "") + (np.Listeners is int n ? $"   · {n} listening" : "")
                + (np.History.Count > 0 ? $"   · last: {(np.History[0].Artist.Length > 0 ? np.History[0].Artist + " – " + np.History[0].Title : np.History[0].Text)}" : "");
        }
        var art = _radio.Poller?.ArtFile ?? "";
        if (art != _rdArtShown)
        {
            _rdArtShown = art;
            try { RdArt.Source = art.Length > 0 && File.Exists(art) ? new Bitmap(art) : null; } catch { RdArt.Source = null; }
        }
        string eng = "";
        if (_engine?.IsRunning == true && _engine.Radio is { } rl)
            eng = $"Stream: {rl.State}, buffer {rl.Buffered:0.0} s" + (rl.Reconnects > 0 ? $", {rl.Reconnects} reconnect(s)" : "") + (_engine.RadioFeedNow is { } f ? $", level {f.Level * 100:0}%" : "");
        RdEngState.Text = eng;
    }

    // ---- start / engine hooks ----
    async Task<List<string>?> RadioPrepare(Profile run)
    {
        if (_radio == null || !run.Radio.Active) return null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var urls = await _radio.ResolveAsync(cts.Token);
            return urls;
        }
        catch (Exception ex) { Log("⚠ Radio: couldn't read the playlist (" + ex.Message + "); streaming without the radio."); return null; }
    }
    void HookEngine26(StreamEngine e, Profile run, List<string>? urls)
    {
        if (_radio == null || urls == null) return;
        e.RadioUrls = urls; e.RadioInfo = _radio.CardInfo;
        var sc = _pit?.Scene; if (sc != null) e.RadioLevel = sc.AudioLevel;
        e.SetRadioMuted(_radioMuted);
    }

    /// <summary>!request → AzuraCast while the radio is on and radio requests are enabled. True = handled.</summary>
    bool HandleRadioRequest(ChatMessage m)
    {
        var r = _cur.Radio;
        if (_radio == null || !r.Active || !r.Requests || _radio.Target == null || !AzuraRequests.IsRequest(m.Text)) return false;
        if (m.IsBot || m.Self || (_store.Chat.MyUsername.Length > 0 && string.Equals(m.User, _store.Chat.MyUsername, StringComparison.OrdinalIgnoreCase))) return true;
        var user = m.User.Trim(); var text = m.Text; var room = m.Room;
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var reply = await _radio.Requests.Handle(user, text, _store.Requests.UserCooldownSeconds, 5, DateTime.UtcNow, cts.Token);
            if (reply == null) return;
            UI(() =>
            {
                Log($"📻 {user}: {text} → {reply}");
                var c = _store.Chat;
                if (_engine?.IsRunning == true) _engine.ShowChatResponse(reply, c.OverlaySeconds);
                if (c.ReplyInChat) _ = ReplyAsync(reply, room);
            });
        });
        return true;
    }
    void RadioContext(ChatCommands.Context ctx) => _radio?.FillContext(ctx);

    // ---- settings <-> UI ----
    void LoadRadio(Profile p)
    {
        if (_radio == null) return;
        var r = p.Radio; var was = _loading; _loading = true;
        RdEnable.IsChecked = r.Enabled; RdUrl.Text = r.Url; RdVolume.Value = r.Volume; RdVolText.Text = $"{r.Volume * 100:0}%";
        RdMute.IsChecked = _radioMuted; RdDuck.IsChecked = r.Duck; RdNorm.IsChecked = r.Normalize; RdHls.IsChecked = r.PreferHls;
        RdNp.IsChecked = r.NowPlaying; RdCard.IsChecked = r.FeedCard; RdPitCard.IsChecked = r.PitCard; RdListeners.IsChecked = r.ShowListeners;
        RdPoll.Text = r.PollSeconds.ToString(); RdBase.Text = r.ApiBase; RdCode.Text = r.Shortcode; RdKey.Text = "";
        RdKeyState.Text = string.IsNullOrEmpty(r.ProtectedApiKey) ? "no key (not needed)" : "key saved (encrypted)";
        RdSongTpl.Text = r.SongTemplate; RdLastTpl.Text = r.LastTemplate; RdReq.IsChecked = r.Requests;
        RdBeat.IsChecked = r.BeatPulse; RdBeatStr.Value = r.BeatStrength;
        _radio.Apply(r); RdShowDetect();
        _loading = was;
    }
    void SaveRadio(Profile p)
    {
        var r = p.Radio;
        r.Enabled = RdEnable.IsChecked == true; r.Url = (RdUrl.Text ?? "").Trim(); r.Volume = Math.Round(RdVolume.Value, 2);
        r.Duck = RdDuck.IsChecked == true; r.Normalize = RdNorm.IsChecked == true; r.PreferHls = RdHls.IsChecked == true;
        r.NowPlaying = RdNp.IsChecked == true; r.FeedCard = RdCard.IsChecked == true; r.PitCard = RdPitCard.IsChecked == true; r.ShowListeners = RdListeners.IsChecked == true;
        r.PollSeconds = Int(RdPoll.Text, 10, 5, 300); r.ApiBase = (RdBase.Text ?? "").Trim(); r.Shortcode = (RdCode.Text ?? "").Trim();
        var key = (RdKey.Text ?? "").Trim();
        if (key.Length > 0) { r.ProtectedApiKey = Secret.Protect(key); RdKey.Text = ""; RdKeyState.Text = r.ProtectedApiKey != null ? "key saved (encrypted)" : "⚠ no secure store: key not saved"; }
        r.SongTemplate = RdSongTpl.Text is { Length: > 0 } st ? st : new RadioSettings().SongTemplate;
        r.LastTemplate = RdLastTpl.Text is { Length: > 0 } lt ? lt : new RadioSettings().LastTemplate;
        r.Requests = RdReq.IsChecked == true; r.BeatPulse = RdBeat.IsChecked == true; r.BeatStrength = Math.Round(RdBeatStr.Value, 2);
        if (_radio != null && p == _cur) { _radio.Apply(r); RdShowDetect(); RdTickNow(); }
    }
    void RdShowDetect()
    {
        var t = RadioHost.TargetFor(_cur.Radio);
        RdDetect.Text = t == null ? "AzuraCast: not detected (now playing / requests off)" : $"AzuraCast: {t.Base} · station \"{t.Shortcode}\"";
    }
    void RdLive() { if (!_loading && _radio != null) SaveRadio(_cur); }
    void RdCheck(object? s, RoutedEventArgs e) => RdLive();
    void RdSlider(object? s, RangeBaseValueChangedEventArgs e)
    {
        if (_loading || _radio == null) return;
        RdVolText.Text = $"{RdVolume.Value * 100:0}%";
        if (_engine?.IsRunning == true && _engine.RadioActive) _engine.SetRadioVolume(Math.Round(RdVolume.Value, 2));
        RdLive();
    }
    void RdMute_Click(object? s, RoutedEventArgs e)
    {
        _radioMuted = RdMute.IsChecked == true;
        if (_engine?.IsRunning == true) _engine.SetRadioMuted(_radioMuted);
        StatusText.Text = _radioMuted ? "Radio muted." : "Radio unmuted.";
    }
    void RdReq_Click(object? s, RoutedEventArgs e)
    {
        RdLive();
        if (RdReq.IsChecked == true) StatusText.Text = "Radio requests on: !request <song> searches the station's requestable songs and submits the best match. AzuraCast rate-limits requests per IP.";
    }
    void RdClearKey_Click(object? s, RoutedEventArgs e) { _cur.Radio.ProtectedApiKey = null; RdKey.Text = ""; RdKeyState.Text = "no key (not needed)"; RdLive(); Persist(); }
    void RdSave_Click(object? s, RoutedEventArgs e)
    {
        SaveRadio(_cur); Persist();
        StatusText.Text = "Radio settings saved." + (_engine?.IsRunning == true ? " Turning the radio on/off or changing the URL applies on the next start; volume, now playing and chat apply now." : "");
    }
    async void RdTest_Click(object? s, RoutedEventArgs e)
    {
        if (_radio == null) return;
        SaveRadio(_cur);
        if ((_cur.Radio.Url ?? "").Trim().Length == 0) { RdStatus.Text = "Enter the station's .pls / .m3u / stream URL first."; return; }
        var was = _cur.Radio.Enabled; _cur.Radio.Enabled = true;    // test even while the radio is switched off
        RdStatus.Text = "Testing… (reading the playlist and decoding a few seconds of each stream)";
        try
        {
            var ff = await RequireFfmpeg(); if (ff == null) { RdStatus.Text = "ffmpeg not found (Settings tab)."; return; }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            RdStatus.Text = await _radio.TestAsync(ff, cts.Token);
        }
        catch (Exception ex) { RdStatus.Text = "Test failed: " + ex.Message; }
        finally { _cur.Radio.Enabled = was; }
    }
}
