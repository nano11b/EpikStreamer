using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace EpikStreamer;

/// <summary>Compact always-on-top player: preview, now playing, transport and quick mutes.</summary>
public sealed class MiniPlayer : Window
{
    readonly Image _img = new() { Stretch = Stretch.Uniform };
    readonly TextBlock _line1 = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0) };
    readonly TextBlock _line2 = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0), FontSize = 11 };
    readonly Button _startStop = new() { Content = "▶", ToolTip = "Start / stop", Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(10, 4, 10, 4) };
    readonly Button _next = new() { Content = "⏭", ToolTip = "Next item", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 0) };
    readonly ToggleButton _music = new() { Content = "🎵", ToolTip = "Mute background music", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 0) };
    readonly ToggleButton _mic = new() { Content = "🎤", ToolTip = "Mute microphone", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 0) };
    readonly ToggleButton _ovl = new() { Content = "🖼", ToolTip = "Hide overlays (logo/text/clock)", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 0) };
    readonly Button _main = new() { Content = "⤢", ToolTip = "Open the main window", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0) };
    bool _updating;

    public MiniPlayer(Action startStop, Action next, Action<bool> music, Action<bool> mic, Action<bool> overlays, Action openMain)
    {
        Title = "EpikStreamer mini"; Width = 360; Height = 300; Topmost = true; ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStyle = WindowStyle.ToolWindow; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bg");
        _next.SetResourceReference(StyleProperty, "Secondary"); _main.SetResourceReference(StyleProperty, "Secondary");
        foreach (var t in new[] { _music, _mic, _ovl }) { t.SetResourceReference(BackgroundProperty, "SecondaryBg"); t.SetResourceReference(ForegroundProperty, "Fg"); t.BorderThickness = new Thickness(0); }
        _line2.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        _startStop.Click += (_, _) => startStop(); _next.Click += (_, _) => next(); _main.Click += (_, _) => openMain();
        _music.Click += (_, _) => { if (!_updating) music(_music.IsChecked == true); };
        _mic.Click += (_, _) => { if (!_updating) mic(_mic.IsChecked == true); };
        _ovl.Click += (_, _) => { if (!_updating) overlays(_ovl.IsChecked == true); };
        var root = new DockPanel { Margin = new Thickness(8) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var b in new UIElement[] { _startStop, _next, _music, _mic, _ovl, _main }) bar.Children.Add(b);
        DockPanel.SetDock(bar, Dock.Bottom); DockPanel.SetDock(_line2, Dock.Bottom); DockPanel.SetDock(_line1, Dock.Bottom);
        root.Children.Add(bar); root.Children.Add(_line2); root.Children.Add(_line1);
        root.Children.Add(new Border { Background = Brushes.Black, Child = _img });
        Content = root;
    }

    public void Update(ImageSource? preview, string line1, string line2, bool running, bool musicMuted, bool micMuted, bool overlaysHidden, bool hasMusic, bool hasMic)
    {
        _updating = true;
        if (preview != null) _img.Source = preview; else if (!running) _img.Source = null;
        _line1.Text = line1; _line2.Text = line2;
        _startStop.Content = running ? "■" : "▶";
        _startStop.Background = new SolidColorBrush(running ? Color.FromRgb(0xC0, 0x39, 0x2B) : Color.FromRgb(0x2E, 0x9E, 0x5B));
        _next.IsEnabled = running;
        _music.IsChecked = musicMuted; _mic.IsChecked = micMuted; _ovl.IsChecked = overlaysHidden;
        _music.Opacity = hasMusic ? 1 : 0.4; _mic.Opacity = hasMic ? 1 : 0.4;
        _updating = false;
    }
}
