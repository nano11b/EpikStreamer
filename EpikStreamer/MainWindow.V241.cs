using System.Windows;
using System.Windows.Controls;
using EpikStreamer.Core;
namespace EpikStreamer;

/// <summary>v2.4.1: make sure an EpikChat live session exists (the camera / "Go live" flow in the chat panel) before ffmpeg
/// connects: on Start, scheduled starts and auto-reconnect. EpikChat refuses the RTMP ingest until a session was created.</summary>
public partial class MainWindow
{
    bool _goLiveBusy; DateTime _goLiveCreatedUtc = DateTime.MinValue;

    static bool IsEpikOutput(OutputTarget o) => o.Preset == "EpikChat" || (o.ServerUrl ?? "").Contains("epikchat.com", StringComparison.OrdinalIgnoreCase);

    void GoLiveStatus(string msg, bool error = false) { StatusText.Text = msg; Log((error ? "⚠ " : "▶ ") + "EpikChat: " + msg); }

    void AutoGoLive_Click(object s, RoutedEventArgs e) { _store.Chat.AutoGoLive = AutoGoLiveCheck.IsChecked == true; try { _store.Save(); } catch { } }

    /// <summary>Detect / create the live session. Updates the stored key of the EpikChat outputs when EpikChat shows a different one.</summary>
    async Task<(LiveSession? Session, LiveSession.Result? Result)> EnsureEpikLiveSession(Profile p, List<OutputTarget> epikOuts, bool reconnect, bool freshKey = false)
    {
        if (!_store.Chat.AutoGoLive || epikOuts.Count == 0 || _goLiveBusy) return (null, null);
        _goLiveBusy = true;
        try
        {
            if (!_chat.IsLoaded2)
            {
                GoLiveStatus("Opening the EpikChat chat panel…");
                ChatToggle.IsChecked = true; await ShowChat(true);
            }
            if (!await _chat.WaitPageReady(30))
            {
                var m = "The EpikChat chat panel didn't load, so Go live couldn't be checked. " + LiveSession.Fallback;
                GoLiveStatus(m, true); return (null, new LiveSession.Result(LiveSession.Outcome.Failed, m));
            }
            var ls = _chat.NewLiveSession(); ls.Status = (m, err) => GoLiveStatus(m, err);
            var r = await ls.EnsureAsync(p.Audience, _keys.GetValueOrDefault(epikOuts[0], ""), _store.Chat.GoLiveTimeoutSeconds, default, freshKey);
            if (r.Outcome == LiveSession.Outcome.Failed)
            {
                GoLiveStatus(r.Message, true);
                if (reconnect || _quiet || !IsVisible) Notify("EpikChat Go live", r.Message, true);
                return (ls, r);
            }
            if (r.Outcome == LiveSession.Outcome.Created) _goLiveCreatedUtc = DateTime.UtcNow;
            if (r.Key is { Length: > 0 } k && r.KeyChanged)
            {
                CommitOutputEditor();
                foreach (var o in epikOuts) _keys[o] = k;
                _editOut = null; RefreshOutputs(epikOuts[0]);
                Persist();
                Log("EpikChat issued a different stream key for this live session; the EpikChat output now uses it (••••" + (k.Length > 4 ? k[^4..] : "") + ").");
            }
            return (ls, r);
        }
        finally { _goLiveBusy = false; }
    }

    /// <summary>After ffmpeg started: confirm on EpikChat if it asks (final Go live button), close the encoder dialog via "Done".</summary>
    async Task FinalizeGoLive(LiveSession ls)
    {
        await Task.Delay(4000);   // give ffmpeg time to connect so EpikChat can detect the ingest
        if (_engine?.IsRunning != true) return;
        var r = await ls.FinalizeAsync(90);
        Log("▶ EpikChat: " + r);
        if (r.StartsWith("timed out")) StatusText.Text = "Streaming. If EpikChat still shows the encoder dialog, finish it in the chat panel (" + LiveSession.Fallback + ")";
    }

    /// <summary>Engine hook (engine thread → UI thread) before each reconnect attempt.</summary>
    Func<Task<IReadOnlyDictionary<int, (string Url, string Secret)>?>> MakeReconnectHook(Profile run, List<OutputTarget> enabledOuts) => () =>
        Dispatcher.InvokeAsync(async () =>
        {
            var epik = enabledOuts.Where(IsEpikOutput).ToList();
            if (!_store.Chat.AutoGoLive || epik.Count == 0) return null;
            // 2.8.2: "Publish failed / Operation not permitted" = the key is dead; get a fresh one even right after a create
            bool rejected = _engine?.PublishRejected == true;
            if (!rejected && (DateTime.UtcNow - _goLiveCreatedUtc).TotalSeconds < 90) return null;   // we just created one: don't stack sessions on a fast retry loop
            if (rejected) Log("⚠ EpikChat refused the stream key (Publish failed). Running Go live for a fresh key before retrying.");
            var (ls, r) = await EnsureEpikLiveSession(run, epik, reconnect: true, freshKey: rejected);
            IReadOnlyDictionary<int, (string Url, string Secret)>? map = null;
            if (r is { Outcome: LiveSession.Outcome.Created, KeyChanged: true })
            {
                var m = new Dictionary<int, (string Url, string Secret)>();
                for (int i = 0; i < enabledOuts.Count; i++)
                    if (IsEpikOutput(enabledOuts[i]) && _keys.GetValueOrDefault(enabledOuts[i], "") is { Length: > 0 } k)
                        m[i] = (CommandBuilder.JoinUrl(enabledOuts[i].ServerUrl, k), k);
                map = m;
            }
            if (r?.Outcome == LiveSession.Outcome.Created) { GoLiveStatus("Live session ready, reconnecting the encoder…"); if (ls != null) _ = FinalizeGoLive(ls); }
            return map;
        }).Task.Unwrap();
}
