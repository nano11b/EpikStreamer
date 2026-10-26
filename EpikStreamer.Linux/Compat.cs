using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
namespace EpikStreamer;

/// <summary>WPF-style GroupBox (header + bordered content); themed in App.axaml.</summary>
public class GroupBox : HeaderedContentControl { }

public enum MessageBoxButton { OK, OKCancel, YesNo, YesNoCancel }
public enum MessageBoxResult { None, OK, Cancel, Yes, No }
public enum MessageBoxImage { None, Information, Warning, Error, Question }

/// <summary>Helpers that keep the WPF code-behind's synchronous call sites working on Avalonia (nested dispatcher loop).</summary>
public static class Sync
{
    /// <summary>Runs the UI loop until <paramref name="t"/> completes, then returns its result (like WPF's modal ShowDialog).</summary>
    public static T Wait<T>(Task<T> t)
    {
        if (!Dispatcher.UIThread.CheckAccess()) return t.GetAwaiter().GetResult();
        if (!t.IsCompleted)
        {
            using var cts = new CancellationTokenSource();
            t.ContinueWith(_ => Dispatcher.UIThread.Post(() => cts.Cancel()), TaskScheduler.Default);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }
        return t.GetAwaiter().GetResult();
    }
    public static void Wait(Task t) => Wait(t.ContinueWith(x => { x.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default));

    public static Window? Owner(Window? w) => w ?? (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>Shows a dialog modally; falls back to a plain window if the owner isn't visible (e.g. hidden to tray).</summary>
    public static Task<T?> ShowModal<T>(Window dlg, Window? owner)
    {
        owner = Owner(owner);
        if (owner is { IsVisible: true }) return dlg.ShowDialog<T?>(owner);
        var tcs = new TaskCompletionSource<T?>();
        dlg.Closed += (_, _) => tcs.TrySetResult(dlg.Tag is T v ? v : default);
        dlg.Show(); return tcs.Task;
    }
}

public static class DispatcherCompat
{
    public static void BeginInvoke(this Dispatcher d, Action a) => d.Post(a);
    public static void BeginInvoke(this Dispatcher d, DispatcherPriority p, Action a) => d.Post(a, p);
    public static void BeginInvoke(this Dispatcher d, Delegate a) => d.Post(() => a.DynamicInvoke());
    public static void Invoke(this Dispatcher d, Action a, DispatcherPriority p) => d.Invoke(a, p);
}

public static class MessageBox
{
    public static MessageBoxResult Show(string text, string title = "EpikStreamer", MessageBoxButton b = MessageBoxButton.OK, MessageBoxImage img = MessageBoxImage.None) =>
        Show(null, text, title, b, img);
    public static MessageBoxResult Show(Window? owner, string text, string title = "EpikStreamer", MessageBoxButton b = MessageBoxButton.OK, MessageBoxImage img = MessageBoxImage.None) =>
        Sync.Wait(ShowAsync(owner, text, title, b, img));

    public static async Task<MessageBoxResult> ShowAsync(Window? owner, string text, string title, MessageBoxButton b = MessageBoxButton.OK, MessageBoxImage img = MessageBoxImage.None)
    {
        var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, MaxWidth = 640, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var icon = img switch { MessageBoxImage.Warning => "⚠", MessageBoxImage.Error => "⛔", MessageBoxImage.Question => "❓", MessageBoxImage.Information => "ℹ", _ => "" };
        var body = new DockPanel { Margin = new Thickness(16) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(btns, Dock.Bottom);
        void Add(string label, MessageBoxResult r, bool primary)
        {
            var bt = new Button { Content = label, MinWidth = 80, IsDefault = primary && r != MessageBoxResult.Cancel, IsCancel = r is MessageBoxResult.Cancel or MessageBoxResult.No };
            if (!primary) bt.Classes.Add("secondary");
            bt.Click += (_, _) => { w.Tag = r; w.Close(r); };
            btns.Children.Add(bt);
        }
        switch (b)
        {
            case MessageBoxButton.OK: Add("OK", MessageBoxResult.OK, true); break;
            case MessageBoxButton.OKCancel: Add("OK", MessageBoxResult.OK, true); Add("Cancel", MessageBoxResult.Cancel, false); break;
            case MessageBoxButton.YesNo: Add("Yes", MessageBoxResult.Yes, true); Add("No", MessageBoxResult.No, false); break;
            case MessageBoxButton.YesNoCancel: Add("Yes", MessageBoxResult.Yes, true); Add("No", MessageBoxResult.No, false); Add("Cancel", MessageBoxResult.Cancel, false); break;
        }
        body.Children.Add(btns);
        if (icon.Length > 0) { var ic = new TextBlock { Text = icon, FontSize = 26, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top }; DockPanel.SetDock(ic, Dock.Left); body.Children.Add(ic); }
        body.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
        w.Content = body;
        var res = await Sync.ShowModal<MessageBoxResult>(w, owner);
        return res == MessageBoxResult.None ? (b is MessageBoxButton.OK ? MessageBoxResult.OK : b is MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel) : res;
    }
}

/// <summary>"Desc|*.a;*.b|Desc2|*.c" (WPF filter syntax) → Avalonia file types.</summary>
static class FilterParse
{
    public static List<FilePickerFileType>? Types(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;
        var p = filter.Split('|'); var l = new List<FilePickerFileType>();
        for (int i = 0; i + 1 < p.Length; i += 2)
        {
            var pats = p[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(x => x == "*.*" ? new[] { "*" } : new[] { x, x.ToUpperInvariant() }).Distinct().ToList();
            l.Add(new FilePickerFileType(p[i]) { Patterns = pats });
        }
        return l;
    }
    public static async Task<IStorageFolder?> Folder(IStorageProvider sp, string? dir) =>
        string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir) ? null : await sp.TryGetFolderFromPathAsync(dir);
}

public class OpenFileDialog
{
    public string Filter { get; set; } = ""; public bool Multiselect { get; set; } public string Title { get; set; } = "Open";
    public string? InitialDirectory { get; set; } public string FileName { get; set; } = ""; public string[] FileNames { get; private set; } = Array.Empty<string>();
    public bool? ShowDialog(Window? owner = null)
    {
        var top = Sync.Owner(owner); if (top == null) return false;
        var r = Sync.Wait(Pick(top));
        FileNames = r.Select(f => f.TryGetLocalPath()).Where(x => x != null).Cast<string>().ToArray();
        FileName = FileNames.FirstOrDefault() ?? "";
        return FileNames.Length > 0;
    }
    async Task<IReadOnlyList<IStorageFile>> Pick(Window top) => await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
    {
        Title = Title, AllowMultiple = Multiselect, FileTypeFilter = FilterParse.Types(Filter),
        SuggestedStartLocation = await FilterParse.Folder(top.StorageProvider, InitialDirectory ?? (FileName.Length > 0 ? Path.GetDirectoryName(FileName) : null)),
    });
}

public class SaveFileDialog
{
    public string Filter { get; set; } = ""; public string Title { get; set; } = "Save"; public string FileName { get; set; } = ""; public string DefaultExt { get; set; } = "";
    public string? InitialDirectory { get; set; }
    public bool? ShowDialog(Window? owner = null)
    {
        var top = Sync.Owner(owner); if (top == null) return false;
        var f = Sync.Wait(Pick(top));
        var p = f?.TryGetLocalPath(); if (p == null) return false;
        FileName = p; return true;
    }
    async Task<IStorageFile?> Pick(Window top) => await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
    {
        Title = Title, FileTypeChoices = FilterParse.Types(Filter), SuggestedFileName = Path.GetFileName(FileName), DefaultExtension = DefaultExt.TrimStart('.'),
        SuggestedStartLocation = await FilterParse.Folder(top.StorageProvider, InitialDirectory ?? (FileName.Length > 0 ? Path.GetDirectoryName(FileName) : null)), ShowOverwritePrompt = true,
    });
}

public class OpenFolderDialog
{
    public string Title { get; set; } = "Choose folder"; public bool Multiselect { get; set; }
    public string FolderName { get; set; } = ""; public string[] FolderNames { get; private set; } = Array.Empty<string>(); public string? InitialDirectory { get; set; }
    public bool? ShowDialog(Window? owner = null)
    {
        var top = Sync.Owner(owner); if (top == null) return false;
        var r = Sync.Wait(Pick(top));
        FolderNames = r.Select(f => f.TryGetLocalPath()).Where(x => x != null).Cast<string>().ToArray();
        FolderName = FolderNames.FirstOrDefault() ?? ""; return FolderNames.Length > 0;
    }
    async Task<IReadOnlyList<IStorageFolder>> Pick(Window top) => await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
    { Title = Title, AllowMultiple = Multiselect, SuggestedStartLocation = await FilterParse.Folder(top.StorageProvider, InitialDirectory ?? (FolderName.Length > 0 ? FolderName : null)) });
}

public static class TextBoxCompat
{
    public static void AppendText(this TextBox tb, string s) => tb.Text = (tb.Text ?? "") + s;
    public static void ScrollToEnd(this TextBox tb) => tb.CaretIndex = tb.Text?.Length ?? 0;
}
