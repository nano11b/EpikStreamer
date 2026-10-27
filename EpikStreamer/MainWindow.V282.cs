using System.Windows.Controls;
using EpikStreamer.Core;
namespace EpikStreamer;

/// <summary>2.8.2: Source / Playlist tab source selector ("Playlist (video files)" vs "Ball Pit scene"), kept in sync with the
/// Ball Pit tab's mode (Ball Pit scene = mode Source) through <see cref="SourceMode"/>.</summary>
public partial class MainWindow
{
    bool _srcSync;
    void Init282()
    {
        // 2.8.2: remember the chat column width when the splitter is dragged
        ChatSplitter.DragCompleted += (_, _) => { if (ChatColumn.ActualWidth >= ChatLayout.MinColumn) { _store.ChatWidth = (int)ChatColumn.ActualWidth; try { _store.Save(); } catch { } } };
        SrcKind.SelectionChanged += (_, _) => SrcKindChanged();
        BpMode.SelectionChanged += (_, _) => SyncSourceFromPit();
        Tabs.SelectionChanged += (_, _) => { if (!_srcSync) RefreshSourceNote(); };
        SyncSourceFromPit();
    }
    static string? TagOf(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as string;
    static void SelectTag(ComboBox c, string tag) { foreach (var o in c.Items) if (o is ComboBoxItem i && (string?)i.Tag == tag) { c.SelectedItem = i; return; } }

    /// <summary>Source selector changed → set the Ball Pit mode (which saves through the Ball Pit tab's normal path).</summary>
    void SrcKindChanged()
    {
        if (_srcSync) return;
        var tmp = new BallPitSettings { Mode = TagOf(BpMode) ?? "Off" };
        SourceMode.Set(tmp, TagOf(SrcKind) ?? SourceMode.Playlist);
        _srcSync = true;
        try { SelectTag(BpMode, tmp.Mode); } finally { _srcSync = false; }
        if (!_loading && _pit != null) { SaveBallPit(_cur); Persist(); }
        RefreshSourceNote();
    }
    /// <summary>Ball Pit mode changed (or a profile was loaded) → reflect it in the source selector.</summary>
    void SyncSourceFromPit()
    {
        if (_srcSync) return;
        _srcSync = true;
        try { SelectTag(SrcKind, SourceMode.Of(new BallPitSettings { Mode = TagOf(BpMode) ?? "Off" })); } finally { _srcSync = false; }
        RefreshSourceNote();
    }
    void RefreshSourceNote()
    {
        var p = _cur.Clone(_cur.Name); p.BallPit.Mode = TagOf(BpMode) ?? p.BallPit.Mode;
        SrcNote.Text = SourceMode.Note(p);
    }
}
