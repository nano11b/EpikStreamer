using System.Windows;
using EpikStreamer.Core;

namespace EpikStreamer;

/// <summary>v2.7.1: chat-reply toggle on the chat panel's bar, first-run "reply in chat?" prompt, room-switch policy.</summary>
public partial class MainWindow
{
    bool _replyUiInit;
    void InitReplyUi()
    {
        if (_replyUiInit) return; _replyUiInit = true;
        // switching rooms to reply is only done while the chat panel isn't on screen (no visible disruption), else queued
        _chat.OnScreen = () => IsVisible && WindowState != WindowState.Minimized && ChatHost.Visibility == Visibility.Visible;
        _chat.ReplyToggled += on => SetReplyInChat(on);
    }
    void SyncReplyToggle() { _chat.ReplyInChat = _store.Chat.ReplyInChat; }
    void SetReplyInChat(bool on)
    {
        var c = _store.Chat; c.ReplyInChat = on; c.Feed.ReplyPromptShown = true;
        ReplyInChatCheck.IsChecked = on; SyncReplyToggle();
        try { _store.Save(); } catch { }
        Log(on ? "💬 replies to chat commands: ON (posted as you via EpikChat's chat box, rate-limited)" : "💬 replies to chat commands: OFF (on-stream overlay only)");
    }
    void MaybeAskReplyPrompt()
    {
        var c = _store.Chat;
        if (c.Feed.ReplyPromptShown || !IsVisible) return;   // asked next time the panel is opened with the window up
        if (c.ReplyInChat) { c.Feed.ReplyPromptShown = true; try { _store.Save(); } catch { } return; }
        var r = MessageBox.Show(this, "Reply to chat commands in chat? (posts as you)\n\nResponses to !help, !song etc. are typed into EpikChat's chat box in the room the command came from, rate-limited. You can change this any time with the ↩ Reply toggle on the chat panel.",
            "EpikStreamer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        SetReplyInChat(r == MessageBoxResult.Yes);
    }
}
