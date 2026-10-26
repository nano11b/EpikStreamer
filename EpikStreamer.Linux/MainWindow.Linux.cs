using EpikStreamer.Core;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
namespace EpikStreamer;

/// <summary>Linux-only glue for the shared MainWindow code (resources, drag/drop, Pulse, sounds, wiring).</summary>
public partial class MainWindow
{
    internal IBrush Res(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    static string[]? FileDrops(DragEventArgs e)
    {
#pragma warning disable CS0618
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        var r = files?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToArray();
        return r is { Length: > 0 } ? r : null;
    }

    /// <summary>Parses <c>pactl list short sources</c>; monitors of outputs are listed last.</summary>
    internal static List<string> PulseSources(string text)
    {
        var all = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).Where(c => c.Length > 1).Select(c => c[1].Trim()).Where(n => n.Length > 0).ToList();
        return all.Where(n => !n.EndsWith(".monitor")).Concat(all.Where(n => n.EndsWith(".monitor"))).ToList();
    }

    static void PlaySound(string file)
    {
        foreach (var player in new[] { "paplay", "pw-play", "aplay" })
            try { var psi = new ProcessStartInfo(player) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true }; psi.ArgumentList.Add(file); Process.Start(psi); return; }
            catch { }
    }

    void RefreshOutputsList(OutputTarget? keep)
    {
        var src = OutputsList.ItemsSource; bool was = _loading; _loading = true;
        if (src != null) { OutputsList.ItemsSource = null; OutputsList.ItemsSource = src; }
        else { var items = OutputsList.Items.Cast<object>().ToList(); OutputsList.Items.Clear(); foreach (var i in items) OutputsList.Items.Add(i); }
        OutputsList.SelectedItem = keep; _loading = was;
    }

    /// <summary>Hooks events that WPF wired in XAML but Avalonia needs in code.</summary>
    void WireLinux()
    {
        void Drop(Control c, Action<object, DragEventArgs> h)
        {
            DragDrop.SetAllowDrop(c, true);
            c.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
            c.AddHandler(DragDrop.DropEvent, (s, e) => h(s!, e));
        }
        Drop(PlaylistBox, Playlist_Drop); Drop(MusicList, MusicList_Drop); Drop(PlItems, PlItems_Drop);
        LibBox.AddHandler(PointerPressedEvent, LibBox_MouseDown, RoutingStrategies.Tunnel);
        LibBox.AddHandler(PointerMovedEvent, LibBox_MouseMove, RoutingStrategies.Tunnel);
        Closing += Window_Closing;
        Closed += (_, _) => App.Quit();
        DownloadBtn.IsVisible = false;
        Graph.SizeChanged += (_, _) => DrawGraph();
        HistoryChart.SizeChanged += (_, _) => DrawHistoryChart();
        ChatNote.Text = "On Linux the EpikChat panel opens in its own window.";
        _chat.UserClosed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { ChatToggle.IsChecked = false; Persist(); });
        _chat.Note += t => Avalonia.Threading.Dispatcher.UIThread.Post(() => Log("💬 " + t));
    }
}

static class Dialogs
{
    public static string? AskText(Window owner, string title, string label, string value, bool allowEmpty = false)
    {
        var box = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 10), MinWidth = 300 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Classes.Add("secondary");
        var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        w.Bind(TemplatedControl.BackgroundProperty, w.GetResourceObservable("Bg"));
        string? result = null;
        ok.Click += (_, _) => { result = box.Text ?? ""; w.Tag = result; w.Close(result); };
        cancel.Click += (_, _) => w.Close(null);
        w.Content = new StackPanel { Margin = new Thickness(14), Children = { new TextBlock { Text = label }, box,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } } };
        w.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        var r = Sync.Wait(Sync.ShowModal<string>(w, owner));
        if (r == null) return null;
        r = r.Trim();
        return r.Length > 0 || allowEmpty ? r : null;
    }
}

/// <summary>Linux update hand-off: never installs as root silently; the user installs the verified .deb.</summary>
static class LinuxUpdate
{
    public static string Command(string deb) => $"sudo apt install {QuoteSh(deb)}";
    static string QuoteSh(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public static async Task Offer(Window owner, string deb, Tray? tray)
    {
        var cmd = Command(deb);
        tray?.Notify("EpikStreamer update ready", "Downloaded and verified: " + Path.GetFileName(deb));
        try { if (owner.Clipboard is { } cb) await cb.SetTextAsync(cmd); } catch { }
        var r = MessageBox.Show(owner,
            $"The update package was downloaded and its SHA-256 checksum verified:\n\n{deb}\n\n" +
            $"Install it with (copied to the clipboard):\n    {cmd}\n\nthen restart EpikStreamer.\n\nOpen the package in your software installer now?",
            "Install update", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (r == MessageBoxResult.Yes) WebViewHost.OpenExternal(deb);
    }
}
