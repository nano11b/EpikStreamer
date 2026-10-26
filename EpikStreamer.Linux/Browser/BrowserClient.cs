using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
namespace EpikStreamer.Browser;

/// <summary>Parent side of the WebKitGTK helper process. One process for the whole app (shared EpikChat session); views by name.</summary>
public sealed class BrowserClient : IDisposable
{
    static BrowserClient? _shared;
    static readonly object Gate = new();
    /// <summary>Last start failure (null = fine). When set the UI falls back to the system browser + manual key paste.</summary>
    public static string? Unavailable { get; private set; }

    readonly Process _p;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _waits = new();
    readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _seq;
    public string EngineVersion { get; private set; } = "";
    public bool HasExited => _p.HasExited;

    public event Action<string, string>? Loaded;             // view, url
    public event Action<string, string, string>? Message;    // view, url, json string posted by the page
    public event Action<string>? Closed;                     // view (window hidden by the user)
    public event Action<string>? Log;
    public event Action? Exited;

    BrowserClient(string exe, IEnumerable<string> prefix)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in prefix) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--browser-host");
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _p.Exited += (_, _) => { _ready.TrySetException(new InvalidOperationException("browser host exited")); foreach (var w in _waits.Values) w.TrySetException(new InvalidOperationException("browser host exited")); Exited?.Invoke(); };
        _p.Start();
        _p.StandardInput.AutoFlush = true;
        new Thread(ReadLoop) { IsBackground = true, Name = "browser-host-out" }.Start();
        _ = Task.Run(async () => { string? l; while ((l = await _p.StandardError.ReadLineAsync()) != null) if (!l.Contains("Gtk-Message") && l.Length > 0) Log?.Invoke("webkit: " + l); });
    }

    /// <summary>The shared host, started on first use. Returns null (and sets <see cref="Unavailable"/>) if WebKitGTK can't start.</summary>
    public static async Task<BrowserClient?> GetAsync()
    {
        BrowserClient? c;
        lock (Gate)
        {
            if (_shared is { HasExited: false }) c = _shared;
            else
            {
                var exe = Environment.ProcessPath!;
                // `dotnet epikstreamer.dll` during development: re-run through the muxer
                var prefix = Path.GetFileNameWithoutExtension(exe) == "dotnet" ? new[] { typeof(BrowserClient).Assembly.Location } : Array.Empty<string>();
                try { c = _shared = new BrowserClient(exe, prefix); }
                catch (Exception e) { Unavailable = e.Message; return null; }
            }
        }
        try
        {
            var t = await Task.WhenAny(c._ready.Task, Task.Delay(20000));
            if (t != c._ready.Task) throw new TimeoutException("browser host did not start in 20 s");
            c.EngineVersion = await c._ready.Task; Unavailable = null; return c;
        }
        catch (Exception e) { Unavailable = e.Message; try { c.Dispose(); } catch { } lock (Gate) if (_shared == c) _shared = null; return null; }
    }

    void ReadLoop()
    {
        try
        {
            string? line;
            while ((line = _p.StandardOutput.ReadLine()) != null)
            {
                JsonObject? o; try { o = JsonNode.Parse(line) as JsonObject; } catch { continue; }
                if (o == null) continue;
                string ev = (string?)o["ev"] ?? "", view = (string?)o["view"] ?? "";
                switch (ev)
                {
                    case "ready": _ready.TrySetResult((string?)o["version"] ?? ""); break;
                    case "fatal": _ready.TrySetException(new InvalidOperationException((string?)o["error"] ?? "browser host failed")); break;
                    case "result": if (o["id"] is JsonValue iv && _waits.TryRemove((int)iv, out var w)) w.TrySetResult(o); break;
                    case "loaded": Loaded?.Invoke(view, (string?)o["url"] ?? ""); break;
                    case "message": Message?.Invoke(view, (string?)o["url"] ?? "", (string?)o["data"] ?? "null"); break;
                    case "closed": Closed?.Invoke(view); break;
                    case "log": Log?.Invoke((string?)o["text"] ?? ""); break;
                }
            }
        }
        catch { }
    }

    void Send(JsonObject o) { lock (_p) { if (!_p.HasExited) _p.StandardInput.WriteLine(o.ToJsonString()); } }

    public void Open(string view, string title, int w = 1100, int h = 800, bool show = true) =>
        Send(new JsonObject { ["op"] = "open", ["view"] = view, ["title"] = title, ["w"] = w, ["h"] = h, ["show"] = show });
    public void Navigate(string view, string url) => Send(new JsonObject { ["op"] = "navigate", ["view"] = view, ["url"] = url });
    public void AddScript(string view, string js) => Send(new JsonObject { ["op"] = "addScript", ["view"] = view, ["js"] = js });
    public void ClearScripts(string view) => Send(new JsonObject { ["op"] = "clearScripts", ["view"] = view });
    public void Show(string view) => Send(new JsonObject { ["op"] = "show", ["view"] = view });
    public void Hide(string view) => Send(new JsonObject { ["op"] = "hide", ["view"] = view });
    public void Close(string view) => Send(new JsonObject { ["op"] = "close", ["view"] = view });

    /// <summary>Evaluates JS in the view; returns the JSON of the result (like WebView2's ExecuteScriptAsync), "null" for undefined.</summary>
    public async Task<string> EvalAsync(string view, string js, int timeoutMs = 15000)
    {
        var id = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waits[id] = tcs;
        Send(new JsonObject { ["op"] = "eval", ["view"] = view, ["id"] = id, ["js"] = js });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (done != tcs.Task) { _waits.TryRemove(id, out _); throw new TimeoutException("script timed out"); }
        var r = await tcs.Task;
        if (r["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException((string?)r["error"] ?? "script error");
        return (string?)r["value"] ?? "null";
    }

    public async Task ClearDataAsync()
    {
        var id = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously); _waits[id] = tcs;
        Send(new JsonObject { ["op"] = "clearData", ["id"] = id });
        await Task.WhenAny(tcs.Task, Task.Delay(15000));
    }

    public void Dispose()
    {
        try { Send(new JsonObject { ["op"] = "quit" }); } catch { }
        try { if (!_p.WaitForExit(2000)) _p.Kill(true); } catch { }
    }

    public static void Shutdown() { lock (Gate) { _shared?.Dispose(); _shared = null; } }
}
