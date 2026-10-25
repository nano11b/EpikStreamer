using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using EpikStreamer.Core;
namespace EpikStreamer;

/// <summary>System-wide hotkeys via RegisterHotKey on the main window's HWND.</summary>
public sealed class GlobalHotkeys : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    const int WM_HOTKEY = 0x0312;
    readonly HwndSource _src; readonly Dictionary<int, Action> _actions = new(); int _next = 0xB000;

    public GlobalHotkeys(Window w)
    {
        _src = HwndSource.FromHwnd(new WindowInteropHelper(w).EnsureHandle());
        _src.AddHook(Hook);
    }
    /// <summary>Registers <paramref name="combo"/>; returns an error message or null on success.</summary>
    public string? Register(string combo, Action action)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        if (!Hotkey.TryParse(combo, out var mods, out var vk)) return $"\"{combo}\" is not a valid hotkey";
        int id = _next++;
        if (!RegisterHotKey(_src.Handle, id, mods | Hotkey.MOD_NOREPEAT, vk)) return $"\"{combo}\" is already used by another app";
        _actions[id] = action; return null;
    }
    public void Clear() { foreach (var id in _actions.Keys) UnregisterHotKey(_src.Handle, id); _actions.Clear(); }
    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a)) { handled = true; a(); }
        return IntPtr.Zero;
    }
    public void Dispose() { Clear(); _src.RemoveHook(Hook); }
}

/// <summary>"Launch at Windows startup": HKCU\...\Run value (per-user, no admin).</summary>
public static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "EpikStreamer";
    public static void Set(bool on, bool minimized)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on && Environment.ProcessPath is string exe) k.SetValue(Name, $"\"{exe}\"" + (minimized ? " --minimized" : ""));
            else k.DeleteValue(Name, false);
        }
        catch { }
    }
    public static bool IsSet() { try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) != null; } catch { return false; } }
}

/// <summary>Notification-area icon with a context menu and balloon tips (WinForms NotifyIcon).</summary>
public sealed class Tray : IDisposable
{
    readonly System.Windows.Forms.NotifyIcon _icon = new();
    readonly System.Windows.Forms.ToolStripMenuItem _startStop, _next, _music, _mic;
    public bool Notifications { get; set; } = true;

    public Tray(Action show, Action startStop, Action next, Action muteMusic, Action muteMic, Action mini, Action exit)
    {
        try { _icon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { _icon.Icon = System.Drawing.SystemIcons.Application; }
        _icon.Text = "EpikStreamer";
        var m = new System.Windows.Forms.ContextMenuStrip();
        m.Items.Add("Show EpikStreamer", null, (_, _) => show());
        m.Items.Add("Mini player", null, (_, _) => mini());
        m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _startStop = new("▶ Start", null, (_, _) => startStop()); m.Items.Add(_startStop);
        _next = new("⏭ Next", null, (_, _) => next()) { Enabled = false }; m.Items.Add(_next);
        _music = new("Mute music", null, (_, _) => muteMusic()); m.Items.Add(_music);
        _mic = new("Mute mic", null, (_, _) => muteMic()); m.Items.Add(_mic);
        m.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        m.Items.Add("Exit", null, (_, _) => exit());
        _icon.ContextMenuStrip = m;
        _icon.DoubleClick += (_, _) => show();
        _icon.Visible = true;
    }
    public void SetState(bool running, bool musicMuted, bool micMuted, string tooltip)
    {
        _startStop.Text = running ? "■ Stop" : "▶ Start"; _next.Enabled = running;
        _music.Checked = musicMuted; _mic.Checked = micMuted;
        var t = "EpikStreamer — " + tooltip; _icon.Text = t.Length > 63 ? t[..63] : t;   // NotifyIcon limit
    }
    public void Notify(string title, string text, bool error = false)
    {
        if (!Notifications) return;
        try { _icon.ShowBalloonTip(4000, title, string.IsNullOrEmpty(text) ? " " : text, error ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Info); } catch { }
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
