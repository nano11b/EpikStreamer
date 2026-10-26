using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EpikStreamer.Core;
namespace EpikStreamer;

/// <summary>System-wide hotkeys on X11 (XGrabKey on the root window, own Display connection + thread).
/// Wayland compositors don't allow global grabs; there this reports why and does nothing.</summary>
public sealed class GlobalHotkeys : IDisposable
{
    const string X = "libX11.so.6";
    [DllImport(X)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X)] static extern int XCloseDisplay(IntPtr d);
    [DllImport(X)] static extern IntPtr XDefaultRootWindow(IntPtr d);
    [DllImport(X)] static extern byte XKeysymToKeycode(IntPtr d, ulong keysym);
    [DllImport(X)] static extern int XGrabKey(IntPtr d, int keycode, uint mods, IntPtr win, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(X)] static extern int XUngrabKey(IntPtr d, int keycode, uint mods, IntPtr win);
    [DllImport(X)] static extern int XSync(IntPtr d, int discard);
    [DllImport(X)] static extern int XPending(IntPtr d);
    [DllImport(X)] static extern int XNextEvent(IntPtr d, IntPtr ev);
    [DllImport(X)] static extern IntPtr XSetErrorHandler(IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ErrFn(IntPtr d, IntPtr e);
    static readonly ErrFn OnErr = (_, _) => { _xerr = true; return 0; };
    static volatile bool _xerr;

    const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;
    static readonly uint[] LockVariants = { 0, LockMask, Mod2Mask, LockMask | Mod2Mask };

    IntPtr _d, _root; Thread? _t; volatile bool _stop;
    readonly BlockingCollection<Action> _work = new();
    readonly Dictionary<(int code, uint mods), Action> _actions = new();
    public string? Unavailable { get; }

    public GlobalHotkeys(Window _)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) { Unavailable = "Global hotkeys need an X11 session (not available on pure Wayland)."; return; }
        try { _d = XOpenDisplay(IntPtr.Zero); } catch (DllNotFoundException) { Unavailable = "libX11 not found."; return; }
        if (_d == IntPtr.Zero) { Unavailable = "Can't open the X display."; return; }
        _root = XDefaultRootWindow(_d);
        _t = new Thread(Loop) { IsBackground = true, Name = "x11-hotkeys" }; _t.Start();
    }

    void Loop()
    {
        var ev = Marshal.AllocHGlobal(256);
        while (!_stop)
        {
            while (_work.TryTake(out var a)) a();
            while (XPending(_d) > 0)
            {
                XNextEvent(_d, ev);
                if (Marshal.ReadInt32(ev) != 2) continue;   // KeyPress
                // XKeyEvent (LP64): type@0 serial@8 send_event@16 display@24 window@32 root@40 subwindow@48 time@56 x,y,x_root,y_root@64..79 state@80 keycode@84
                uint state = (uint)Marshal.ReadInt32(ev, 80) & ~(LockMask | Mod2Mask); int code = Marshal.ReadInt32(ev, 84);
                if (_actions.TryGetValue((code, state), out var act)) Dispatcher.UIThread.Post(act);
            }
            Thread.Sleep(30);
        }
        Marshal.FreeHGlobal(ev);
    }

    T OnX<T>(Func<T> f)
    {
        if (_t == null) return f();
        var done = new ManualResetEventSlim(); T r = default!;
        _work.Add(() => { try { r = f(); } finally { done.Set(); } });
        done.Wait(3000); return r;
    }

    public static ulong Keysym(uint vk) => vk switch
    {
        >= 'A' and <= 'Z' => vk + 0x20,
        >= '0' and <= '9' => vk,
        >= 0x70 and <= 0x87 => 0xffbe + (vk - 0x70),   // F1..F24
        >= 0x60 and <= 0x69 => 0xffb0 + (vk - 0x60),   // KP_0..KP_9
        0x20 => 0x20, 0x13 => 0xff13, 0x24 => 0xff50, 0x23 => 0xff57, 0x21 => 0xff55, 0x22 => 0xff56, 0x2D => 0xff63, 0x2E => 0xffff,
        0x25 => 0xff51, 0x26 => 0xff52, 0x27 => 0xff53, 0x28 => 0xff54,
        0xB3 => 0x1008ff14, 0xB0 => 0x1008ff17, 0xB2 => 0x1008ff15, 0xAD => 0x1008ff12,
        _ => 0,
    };
    public static uint XMods(uint m) => ((m & Hotkey.MOD_SHIFT) != 0 ? ShiftMask : 0) | ((m & Hotkey.MOD_CONTROL) != 0 ? ControlMask : 0) |
                                        ((m & Hotkey.MOD_ALT) != 0 ? Mod1Mask : 0) | ((m & Hotkey.MOD_WIN) != 0 ? Mod4Mask : 0);

    /// <summary>Registers <paramref name="combo"/> (same syntax as Windows); returns an error message or null on success.</summary>
    public string? Register(string combo, Action action)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        if (Unavailable != null) return Unavailable;
        if (!Hotkey.TryParse(combo, out var mods, out var vk)) return $"\"{combo}\" is not a valid hotkey";
        var sym = Keysym(vk); if (sym == 0) return $"\"{combo}\" has no X11 key";
        return OnX(() =>
        {
            int code = XKeysymToKeycode(_d, sym); if (code == 0) return $"\"{combo}\": key not on this keyboard";
            uint xm = XMods(mods);
            _xerr = false; var old = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(OnErr));
            foreach (var l in LockVariants) XGrabKey(_d, code, xm | l, _root, 0, 1, 1);
            XSync(_d, 0); XSetErrorHandler(old);
            if (_xerr) { foreach (var l in LockVariants) XUngrabKey(_d, code, xm | l, _root); XSync(_d, 0); return $"\"{combo}\" is already used by another app"; }
            _actions[(code, xm)] = action; return (string?)null;
        });
    }
    public void Clear()
    {
        if (Unavailable != null) return;
        OnX(() => { foreach (var (code, m) in _actions.Keys) foreach (var l in LockVariants) XUngrabKey(_d, code, m | l, _root); XSync(_d, 0); _actions.Clear(); return 0; });
    }
    public void Dispose() { if (_t == null) return; Clear(); _stop = true; _t.Join(500); XCloseDisplay(_d); _t = null; }
}

/// <summary>"Launch at login": an XDG autostart entry (~/.config/autostart/epikstreamer.desktop).</summary>
public static class StartupRegistration
{
    static string File_ => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart", "epikstreamer.desktop");
    public static string Exe => File.Exists("/usr/bin/epikstreamer") ? "/usr/bin/epikstreamer" : Environment.ProcessPath ?? "epikstreamer";
    public static void Set(bool on, bool minimized)
    {
        try
        {
            if (!on) { if (File.Exists(File_)) File.Delete(File_); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, "[Desktop Entry]\nType=Application\nName=EpikStreamer\nComment=Stream videos to EpikChat\n" +
                $"Exec={Exe}{(minimized ? " --minimized" : "")}\nIcon=epikstreamer\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
        }
        catch { }
    }
    public static bool IsSet() => File.Exists(File_);
}

/// <summary>Tray icon (StatusNotifierItem / XEmbed via Avalonia) + desktop notifications (notify-send / org.freedesktop.Notifications).</summary>
public sealed class Tray : IDisposable
{
    readonly TrayIcon _icon;
    readonly NativeMenuItem _startStop, _next, _music, _mic;
    public bool Notifications { get; set; } = true;
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "epikstreamer.png");

    public Tray(Action show, Action startStop, Action next, Action muteMusic, Action muteMic, Action mini, Action exit)
    {
        var m = new NativeMenu();
        NativeMenuItem Item(string t, Action a) { var i = new NativeMenuItem(t); i.Click += (_, _) => a(); m.Items.Add(i); return i; }
        Item("Show EpikStreamer", show); Item("Mini player", mini); m.Items.Add(new NativeMenuItemSeparator());
        _startStop = Item("▶ Start", startStop); _next = Item("⏭ Next", next); _next.IsEnabled = false;
        _music = Item("Mute music", muteMusic); _music.ToggleType = NativeMenuItemToggleType.CheckBox;
        _mic = Item("Mute mic", muteMic); _mic.ToggleType = NativeMenuItemToggleType.CheckBox;
        m.Items.Add(new NativeMenuItemSeparator()); Item("Exit", exit);
        _icon = new TrayIcon { ToolTipText = "EpikStreamer", Menu = m, IsVisible = true };
        try { _icon.Icon = File.Exists(IconPath) ? new WindowIcon(IconPath) : new WindowIcon(AssetLoader()); } catch { }
        _icon.Clicked += (_, _) => show();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });
    }
    static Bitmap AssetLoader() => new(Avalonia.Platform.AssetLoader.Open(new Uri("avares://epikstreamer/Assets/icon.png")));

    public void SetState(bool running, bool musicMuted, bool micMuted, string tooltip)
    {
        _startStop.Header = running ? "■ Stop" : "▶ Start"; _next.IsEnabled = running;
        _music.IsChecked = musicMuted; _mic.IsChecked = micMuted;
        _icon.ToolTipText = "EpikStreamer — " + tooltip;
    }
    public void Notify(string title, string text, bool error = false)
    {
        if (!Notifications) return;
        try
        {
            var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-a", "EpikStreamer", "-i", File.Exists(IconPath) ? IconPath : "epikstreamer", "-u", error ? "critical" : "normal", title, string.IsNullOrEmpty(text) ? " " : text }) psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch { /* libnotify-bin not installed: silently skip */ }
    }
    public void Dispose() { _icon.IsVisible = false; _icon.Dispose(); }
}
