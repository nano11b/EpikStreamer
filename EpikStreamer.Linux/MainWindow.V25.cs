using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EpikStreamer.Core;
namespace EpikStreamer;

/// <summary>v2.5 "Ball Pit": room members → bouncing avatar balls in a CPU-rendered 3D scene, streamed as the video source
/// or as a transparent overlay. Settings live on the profile (Profile.BallPit).</summary>
public partial class MainWindow
{
    BallPitHost? _pit;
    readonly DispatcherTimer _bpPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    readonly DispatcherTimer _bpTick = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _bpLastFull = DateTime.MinValue, _bpLastQuick = DateTime.MinValue; long _bpSeq = -1;
    WriteableBitmap? _bpBmp;

    void Init25()
    {
        _pit = new BallPitHost(_cur.BallPit, Path.Combine(ProfileStore.Dir, "avatars"),
            url => Dispatcher.UIThread.InvokeAsync(() => _chat.FetchAvatarViaPage(url)));
        _pit.MyName = _store.Chat.MyUsername;
        _pit.Log += t => { if (_store.Chat.DebugLog) UI(() => Log(t)); };
        _chat.MembersReceived += (list, complete, total, found) => UI(() =>
        {
            if (!found) { BpStatusExtra = "member list not visible in the chat panel"; return; }
            _pit.OnMembers(list, complete, total);
            BpStatusExtra = $"last read {list.Count} members" + (complete ? " (full list)" : " (partial)") + (total > 0 ? $", page says {total}" : "");
        });
        _chat.MessageReceived += m => UI(() => { if (_cur.BallPit.Active || BpTabVisible) _pit.OnChat(m); });
        _chat.ViewersReceived += (src, names) => UI(() =>
        {
            if (src == "LiveViewers") _pit.OnViewers(names);
            else _pit.OnMembers(names.Select(n => new BallPitRoster.Member(n)).ToList(), false, 0);   // rendered rows only
        });
        HookBallPitCommands();
        _bpPreviewTimer.Tick += (_, _) => BpRenderPreview();
        _bpTick.Tick += (_, _) => BpTickNow();
        _bpTick.Start(); _bpPreviewTimer.Start();
        LoadBallPit(_cur);
    }
    string BpStatusExtra = "";
    bool BpTabVisible => IsVisible && Tabs.SelectedItem == BallPitTab;

    void HookBallPitCommands()
    {
        if (_commands == null) return;
        _commands.BallPit = (u, cmd, arg, host) => _pit != null && _cur.BallPit.Active && _cur.BallPit.Commands && _pit.Command(u, cmd, arg, host);
        _commands.BallPitListed = _cur.BallPit.Active && _cur.BallPit.Commands;
    }

    void BpTickNow()
    {
        if (_pit == null) return;
        _pit.MyName = _store.Chat.MyUsername;
        _pit.Tick();
        bool want = _cur.BallPit.Active || BpTabVisible;
        if (want && _chat.IsLoaded2)
        {
            var now = DateTime.Now;
            if ((now - _bpLastFull).TotalSeconds >= Math.Max(15, _cur.BallPit.ScanSeconds)) { _bpLastFull = now; _bpLastQuick = now; _ = _chat.ScanMembers(true); }
            else if ((now - _bpLastQuick).TotalSeconds >= 5) { _bpLastQuick = now; _ = _chat.ScanMembers(false); }
        }
        if (BpTabVisible) BpUpdateStatus();
    }
    void BpUpdateStatus()
    {
        if (_pit == null) return;
        var sc = _pit.Scene;
        var s = $"{sc.Count} in the ball pit" + (sc.Waiting > 0 ? $" (+{sc.Waiting} waiting)" : "") + $" · {_pit.Roster.Count} people seen";
        if (_engine?.IsRunning == true && _engine.PitFeed is { } f) s += $" · streaming, render {f.RenderFps:0} fps" + (f.Repeats > 0 ? $", {f.Repeats} repeated frames" : "");
        else if (!_chat.IsLoaded2) s += " · open the 💬 Chat panel to read the member list";
        if (BpStatusExtra.Length > 0) s += " · " + BpStatusExtra;
        BpStatus.Text = s;
    }

    void BpRenderPreview()
    {
        if (_pit == null || !BpTabVisible) return;
        try
        {
            byte[]? px = null; int w = 0, h = 0;
            if (_engine?.IsRunning == true && _engine.PitActive)
            {
                lock (BallPitFeed.PreviewLock)
                {
                    if (BallPitFeed.PreviewFrame == null || BallPitFeed.PreviewSeq == _bpSeq) return;
                    _bpSeq = BallPitFeed.PreviewSeq; w = BallPitFeed.PreviewW; h = BallPitFeed.PreviewH; px = (byte[])BallPitFeed.PreviewFrame.Clone();
                }
            }
            else { w = 640; h = 360; px = _pit.RenderPreview(w, h, transparent: false); }
            BpBlit(px, w, h);
        }
        catch (Exception ex) { BpStatus.Text = "Preview error: " + ex.Message; }
    }

    // ---- settings <-> UI ----
    void LoadBallPit(Profile p)
    {
        if (_pit == null) return;
        var b = p.BallPit; var was = _loading; _loading = true;
        foreach (ComboBoxItem i in BpMode.Items) if ((string?)i.Tag == b.Mode) BpMode.SelectedItem = i;
        if (BpMode.SelectedItem == null) BpMode.SelectedIndex = 0;
        BpScale.SelectedIndex = b.RenderScale <= 0.55 ? 2 : b.RenderScale <= 0.8 ? 1 : 0;
        BpMax.Text = b.MaxBalls.ToString(); BpSize.Value = b.BallSize; BpGravity.Value = b.Gravity; BpBounce.Value = b.Bounciness; BpFriction.Value = b.Friction;
        BpPitch.Value = b.CameraPitch; BpZoom.Value = b.CameraZoom; BpYaw.Value = b.CameraYaw; BpOrbit.IsChecked = b.Orbit; BpOrbitSpeed.Text = b.OrbitSpeed.ToString(CultureInfo.InvariantCulture);
        BpRoom.IsChecked = b.ShowRoom; BpLabels.IsChecked = b.Labels; BpCount.IsChecked = b.ShowCount;
        BpBg.Text = b.BackgroundColor; BpFloor.Text = b.FloorColor; BpWall.Text = b.WallColor;
        BpJoin.IsChecked = b.JoinEvents; BpLeave.IsChecked = b.LeaveEvents; BpBubbles.IsChecked = b.ChatBubbles; BpCmds.IsChecked = b.Commands;
        BpShakeHost.IsChecked = b.ShakeHostOnly; BpShakeCd.Text = b.ShakeCooldownSeconds.ToString();
        BpViewers.IsChecked = b.IncludeViewers; BpChatters.IsChecked = b.IncludeChatters; BpChatMin.Text = b.ChatterMinutes.ToString(); BpScan.Text = b.ScanSeconds.ToString();
        _pit.Apply(b); HookBallPitCommands();
        _loading = was;
    }
    void SaveBallPit(Profile p)
    {
        var b = p.BallPit;
        b.Mode = (BpMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "Off";
        b.RenderScale = BpScale.SelectedIndex switch { 2 => 0.5, 1 => 0.75, _ => 1.0 };
        b.MaxBalls = Int(BpMax.Text, 200, 1, BallPitScene.HardCap);
        b.BallSize = BpSize.Value; b.Gravity = BpGravity.Value; b.Bounciness = BpBounce.Value; b.Friction = BpFriction.Value;
        b.CameraPitch = BpPitch.Value; b.CameraZoom = BpZoom.Value; b.CameraYaw = BpYaw.Value; b.Orbit = BpOrbit.IsChecked == true;
        b.OrbitSpeed = double.TryParse(BpOrbitSpeed.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var os) ? Math.Clamp(os, -90, 90) : 4;
        b.ShowRoom = BpRoom.IsChecked == true; b.Labels = BpLabels.IsChecked == true; b.ShowCount = BpCount.IsChecked == true;
        string Col(string t, string def) => BallPitScene.ParseColor(t.Trim()) != null ? t.Trim() : def;
        b.BackgroundColor = Col(BpBg.Text ?? "", "#0F172A"); b.FloorColor = Col(BpFloor.Text ?? "", "#1E293B"); b.WallColor = Col(BpWall.Text ?? "", "#26344A");
        b.JoinEvents = BpJoin.IsChecked == true; b.LeaveEvents = BpLeave.IsChecked == true; b.ChatBubbles = BpBubbles.IsChecked == true; b.Commands = BpCmds.IsChecked == true;
        b.ShakeHostOnly = BpShakeHost.IsChecked == true; b.ShakeCooldownSeconds = Int(BpShakeCd.Text, 30, 0, 3600);
        b.IncludeViewers = BpViewers.IsChecked == true; b.IncludeChatters = BpChatters.IsChecked == true;
        b.ChatterMinutes = Int(BpChatMin.Text, 20, 1, 1440); b.ScanSeconds = Int(BpScan.Text, 60, 15, 3600);
        if (_pit != null && p == _cur) { _pit.Apply(b); HookBallPitCommands(); }
    }
    void BpLive() { if (!_loading && _pit != null) SaveBallPit(_cur); }
    void BpChanged(object? s, SelectionChangedEventArgs e) => BpLive();
    void BpSlider(object? s, RangeBaseValueChangedEventArgs e) => BpLive();
    void BpCheck(object? s, RoutedEventArgs e) => BpLive();
    void BpSave_Click(object? s, RoutedEventArgs e)
    {
        SaveBallPit(_cur); Persist();
        StatusText.Text = "Ball Pit settings saved." + (_engine?.IsRunning == true ? " Mode / render-size changes apply on the next start; physics, camera and events apply now." : "");
    }
    void BpTest_Click(object? s, RoutedEventArgs e) { _pit?.AddTestUsers(10); BpUpdateStatus(); }
    void BpClearTest_Click(object? s, RoutedEventArgs e) { _pit?.ClearTestUsers(); BpUpdateStatus(); }
    void BpShake_Click(object? s, RoutedEventArgs e) => _pit?.Scene.Command(_store.Chat.MyUsername.Length > 0 ? _store.Chat.MyUsername : "host", "!shake", "", true);
    async void BpScanNow_Click(object? s, RoutedEventArgs e)
    {
        if (!_chat.IsLoaded2) { StatusText.Text = "Open the 💬 Chat panel (signed in, in the room) first."; return; }
        _bpLastFull = DateTime.Now; BpStatusExtra = "scanning the member list…"; BpUpdateStatus();
        await _chat.ScanMembers(true);
    }
    static void UI(Action a) => Dispatcher.UIThread.Post(a);
    void BpBlit(byte[] px, int w, int h)
    {
        if (_bpBmp == null || _bpBmp.PixelSize.Width != w || _bpBmp.PixelSize.Height != h)
            _bpBmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = _bpBmp.Lock())
        {
            if (fb.RowBytes == w * 4) Marshal.Copy(px, 0, fb.Address, w * h * 4);
            else for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        }
        if (BpPreview.Source != _bpBmp) BpPreview.Source = _bpBmp;
        BpPreview.InvalidateVisual();
    }
}
