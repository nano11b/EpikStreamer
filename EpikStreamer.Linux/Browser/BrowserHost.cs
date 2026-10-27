using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace EpikStreamer.Browser;

/// <summary>
/// Child process ("epikstreamer --browser-host"): runs GTK 3 + WebKitGTK 4.1 and speaks JSON lines on stdin/stdout.
/// Kept out of the Avalonia process so the two toolkits never share a main loop, and so a WebKit crash can't take the stream down.
/// All views share the default WebKitWebContext, i.e. one cookie jar / login session (like WebView2's shared user-data folder).
///   in : {"op":"open","view":"chat","title":"…","w":1300,"h":860,"minW":984,"minH":560}   out also: {"ev":"size","view","w","h"}  navigate{view,url}  eval{view,id,js}  addScript{view,js}
///        clearScripts{view}  show{view}  hide{view}  close{view}  clearData{id}  quit
///   out: {"ev":"ready","version":"2.44.0"}  loaded{view,url}  result{view,id,ok,value|error}  message{view,url,data}  closed{view}  log{text}
/// </summary>
public static unsafe class BrowserHost
{
    const string Gtk = "libgtk-3.so.0", Wk = "libwebkit2gtk-4.1.so.0", Jsc = "libjavascriptcoregtk-4.1.so.0", GObj = "libgobject-2.0.so.0", GLib = "libglib-2.0.so.0";

    [DllImport(Gtk)] static extern bool gtk_init_check(IntPtr argc, IntPtr argv);
    [DllImport(Gtk)] static extern IntPtr gtk_window_new(int type);
    [DllImport(Gtk)] static extern void gtk_window_set_title(IntPtr w, string t);
    [DllImport(Gtk)] static extern void gtk_window_set_default_size(IntPtr w, int x, int y);
    [DllImport(Gtk)] static extern void gtk_window_get_size(IntPtr w, out int x, out int y);
    [DllImport(Gtk)] static extern void gtk_widget_set_size_request(IntPtr w, int x, int y);
    [DllImport(Gtk)] static extern void gtk_window_set_icon_from_file(IntPtr w, string path, IntPtr err);
    [DllImport(Gtk)] static extern void gtk_container_add(IntPtr c, IntPtr w);
    [DllImport(Gtk)] static extern void gtk_widget_show_all(IntPtr w);
    [DllImport(Gtk)] static extern void gtk_widget_hide(IntPtr w);
    [DllImport(Gtk)] static extern void gtk_widget_destroy(IntPtr w);
    [DllImport(Gtk)] static extern void gtk_window_present(IntPtr w);
    [DllImport(Gtk)] static extern void gtk_main();
    [DllImport(Gtk)] static extern void gtk_main_quit();
    [DllImport(GObj)] static extern ulong g_signal_connect_data(IntPtr inst, string sig, IntPtr handler, IntPtr data, IntPtr destroy, int flags);
    [DllImport(GObj)] static extern void g_object_unref(IntPtr o);
    [DllImport(GLib)] static extern uint g_idle_add(IntPtr fn, IntPtr data);
    [DllImport(GLib)] static extern void g_free(IntPtr p);
    [DllImport(GLib)] static extern void g_set_prgname(string n);
    [DllImport(GLib)] static extern void g_error_free(IntPtr e);
    [DllImport(Wk)] static extern IntPtr webkit_user_content_manager_new();
    [DllImport(Wk)] static extern bool webkit_user_content_manager_register_script_message_handler(IntPtr ucm, string name);
    [DllImport(Wk)] static extern void webkit_user_content_manager_add_script(IntPtr ucm, IntPtr script);
    [DllImport(Wk)] static extern void webkit_user_content_manager_remove_all_scripts(IntPtr ucm);
    [DllImport(Wk)] static extern IntPtr webkit_user_script_new(string src, int frames, int time, IntPtr allow, IntPtr block);
    [DllImport(Wk)] static extern void webkit_user_script_unref(IntPtr s);
    [DllImport(Wk)] static extern IntPtr webkit_web_view_new_with_user_content_manager(IntPtr ucm);
    [DllImport(Wk)] static extern void webkit_web_view_load_uri(IntPtr v, string uri);
    [DllImport(Wk)] static extern IntPtr webkit_web_view_get_uri(IntPtr v);
    [DllImport(Wk)] static extern void webkit_web_view_evaluate_javascript(IntPtr v, string js, nint len, IntPtr world, IntPtr srcUri, IntPtr cancel, IntPtr cb, IntPtr ud);
    [DllImport(Wk)] static extern IntPtr webkit_web_view_evaluate_javascript_finish(IntPtr v, IntPtr res, out IntPtr err);
    [DllImport(Wk)] static extern IntPtr webkit_javascript_result_get_js_value(IntPtr r);
    [DllImport(Wk)] static extern IntPtr webkit_web_context_get_default();
    [DllImport(Wk)] static extern IntPtr webkit_web_context_get_cookie_manager(IntPtr ctx);
    [DllImport(Wk)] static extern IntPtr webkit_web_context_get_website_data_manager(IntPtr ctx);
    [DllImport(Wk)] static extern void webkit_cookie_manager_set_persistent_storage(IntPtr cm, string path, int kind);
    [DllImport(Wk)] static extern void webkit_website_data_manager_clear(IntPtr m, int types, long timespan, IntPtr cancel, IntPtr cb, IntPtr ud);
    [DllImport(Wk)] static extern IntPtr webkit_navigation_policy_decision_get_navigation_action(IntPtr d);
    [DllImport(Wk)] static extern IntPtr webkit_navigation_action_get_request(IntPtr a);
    [DllImport(Wk)] static extern IntPtr webkit_uri_request_get_uri(IntPtr r);
    [DllImport(Wk)] static extern void webkit_policy_decision_ignore(IntPtr d);
    [DllImport(Wk)] static extern uint webkit_get_major_version();
    [DllImport(Wk)] static extern uint webkit_get_minor_version();
    [DllImport(Wk)] static extern uint webkit_get_micro_version();
    [DllImport(Jsc)] static extern IntPtr jsc_value_to_json(IntPtr v, uint indent);
    [DllImport(Jsc)] static extern IntPtr jsc_value_to_string(IntPtr v);
    [DllImport(Jsc)] static extern bool jsc_value_is_undefined(IntPtr v);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IdleFn(IntPtr d);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void MsgFn(IntPtr ucm, IntPtr jsResult, IntPtr d);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void LoadFn(IntPtr view, int ev, IntPtr d);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PolicyFn(IntPtr view, IntPtr decision, int type, IntPtr d);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeleteFn(IntPtr w, IntPtr ev, IntPtr d);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AsyncFn(IntPtr src, IntPtr res, IntPtr d);

    // kept alive for the process lifetime
    static readonly IdleFn Pump = PumpImpl;
    static readonly MsgFn OnMsg = OnMsgImpl;
    static readonly LoadFn OnLoad = OnLoadImpl;
    static readonly PolicyFn OnPolicy = OnPolicyImpl;
    static readonly DeleteFn OnDelete = OnDeleteImpl;
    static readonly DeleteFn OnConfigure = OnConfigureImpl;
    static readonly AsyncFn OnEval = OnEvalImpl;
    static readonly AsyncFn OnCleared = (_, _, d) => Send(new JsonObject { ["ev"] = "result", ["id"] = (int)d, ["ok"] = true, ["value"] = "true" });

    sealed class View { public string Id = ""; public IntPtr Win, Web, Ucm; public int LastW, LastH; }
    static readonly Dictionary<string, View> Views = new();
    static readonly Dictionary<IntPtr, View> ByPtr = new();
    static readonly ConcurrentQueue<JsonObject> Inbox = new();
    static readonly object OutLock = new();
    static int _pending, _evalSeq;
    static readonly Dictionary<int, (string view, int id)> Evals = new();
    static string? _icon;

    public const string Shim = "(function(){try{window.chrome=window.chrome||{};if(!window.chrome.webview||!window.chrome.webview.__epikShim)" +
        "window.chrome.webview={__epikShim:true,postMessage:function(m){window.webkit.messageHandlers.epik.postMessage(JSON.stringify(m));}," +
        "addEventListener:function(){},removeEventListener:function(){}};}catch(e){}})();";

    public static int Run(string[] args)
    {
        try
        {
            g_set_prgname("epikstreamer");
            if (!gtk_init_check(IntPtr.Zero, IntPtr.Zero)) { Send(new JsonObject { ["ev"] = "fatal", ["error"] = "gtk_init failed (no display?)" }); return 2; }
            var ver = $"{webkit_get_major_version()}.{webkit_get_minor_version()}.{webkit_get_micro_version()}";
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "epikstreamer");
            Directory.CreateDirectory(dir);
            webkit_cookie_manager_set_persistent_storage(webkit_web_context_get_cookie_manager(webkit_web_context_get_default()), Path.Combine(dir, "cookies.sqlite"), 1);
            _icon = Path.Combine(AppContext.BaseDirectory, "epikstreamer.png"); if (!File.Exists(_icon)) _icon = null;
            new Thread(ReadStdin) { IsBackground = true, Name = "stdin" }.Start();
            Send(new JsonObject { ["ev"] = "ready", ["version"] = ver });
            gtk_main();
            return 0;
        }
        catch (DllNotFoundException e) { Send(new JsonObject { ["ev"] = "fatal", ["error"] = "WebKitGTK 4.1 not available: " + e.Message }); return 3; }
    }

    static void ReadStdin()
    {
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonNode.Parse(line) is JsonObject o) { Inbox.Enqueue(o); Kick(); } } catch { }
        }
        Inbox.Enqueue(new JsonObject { ["op"] = "quit" }); Kick();   // parent went away
    }
    static void Kick() { if (Interlocked.Exchange(ref _pending, 1) == 0) g_idle_add(Marshal.GetFunctionPointerForDelegate(Pump), IntPtr.Zero); }

    public static void Send(JsonObject o)
    {
        var s = o.ToJsonString();
        lock (OutLock) { var w = Console.Out; w.Write(s); w.Write('\n'); w.Flush(); }
    }

    static int PumpImpl(IntPtr _)
    {
        Volatile.Write(ref _pending, 0);
        while (Inbox.TryDequeue(out var o)) { try { Handle(o); } catch (Exception e) { Send(new JsonObject { ["ev"] = "log", ["text"] = "host: " + e.Message }); } }
        return 0;
    }

    static string S(JsonObject o, string k, string d = "") => o[k]?.GetValue<string>() ?? d;
    static int I(JsonObject o, string k, int d) => o[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : d;

    static void Handle(JsonObject o)
    {
        var op = S(o, "op"); var vid = S(o, "view", "main");
        Views.TryGetValue(vid, out var v);
        switch (op)
        {
            case "open":
                if (v == null)
                {
                    v = new View { Id = vid, Ucm = webkit_user_content_manager_new() };
                    webkit_user_content_manager_register_script_message_handler(v.Ucm, "epik");
                    g_signal_connect_data(v.Ucm, "script-message-received::epik", Marshal.GetFunctionPointerForDelegate(OnMsg), IntPtr.Zero, IntPtr.Zero, 0);
                    v.Web = webkit_web_view_new_with_user_content_manager(v.Ucm);
                    v.Win = gtk_window_new(0);
                    gtk_window_set_default_size(v.Win, I(o, "w", 1100), I(o, "h", 800));
                    if (_icon != null) gtk_window_set_icon_from_file(v.Win, _icon, IntPtr.Zero);
                    gtk_container_add(v.Win, v.Web);
                    g_signal_connect_data(v.Web, "load-changed", Marshal.GetFunctionPointerForDelegate(OnLoad), IntPtr.Zero, IntPtr.Zero, 0);
                    g_signal_connect_data(v.Web, "decide-policy", Marshal.GetFunctionPointerForDelegate(OnPolicy), IntPtr.Zero, IntPtr.Zero, 0);
                    g_signal_connect_data(v.Win, "delete-event", Marshal.GetFunctionPointerForDelegate(OnDelete), IntPtr.Zero, IntPtr.Zero, 0);
                    g_signal_connect_data(v.Win, "configure-event", Marshal.GetFunctionPointerForDelegate(OnConfigure), IntPtr.Zero, IntPtr.Zero, 0);
                    Views[vid] = v; ByPtr[v.Web] = v; ByPtr[v.Ucm] = v; ByPtr[v.Win] = v;
                    AddScript(v, Shim);
                }
                if (I(o, "minW", 0) > 0) gtk_widget_set_size_request(v.Web, I(o, "minW", 0), I(o, "minH", 400));   // 2.8.2: keep EpikChat's desktop layout
                gtk_window_set_title(v.Win, S(o, "title", "EpikChat"));
                if (o["show"]?.GetValue<bool>() != false) { gtk_widget_show_all(v.Win); gtk_window_present(v.Win); }
                break;
            case "navigate": if (v != null) webkit_web_view_load_uri(v.Web, S(o, "url")); break;
            case "addScript": if (v != null) AddScript(v, S(o, "js")); break;
            case "clearScripts": if (v != null) { webkit_user_content_manager_remove_all_scripts(v.Ucm); AddScript(v, Shim); } break;
            case "show": if (v != null) { gtk_widget_show_all(v.Win); gtk_window_present(v.Win); } break;
            case "hide": if (v != null) gtk_widget_hide(v.Win); break;
            case "close":
                if (v != null) { Views.Remove(vid); ByPtr.Remove(v.Web); ByPtr.Remove(v.Ucm); ByPtr.Remove(v.Win); gtk_widget_destroy(v.Win); Send(new JsonObject { ["ev"] = "closed", ["view"] = vid, ["destroyed"] = true }); }
                break;
            case "eval":
            {
                int id = I(o, "id", 0);
                if (v == null) { Send(new JsonObject { ["ev"] = "result", ["view"] = vid, ["id"] = id, ["ok"] = false, ["error"] = "no such view" }); break; }
                int seq = ++_evalSeq; Evals[seq] = (vid, id);
                webkit_web_view_evaluate_javascript(v.Web, S(o, "js"), -1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(OnEval), seq);
                break;
            }
            case "clearData":
                webkit_website_data_manager_clear(webkit_web_context_get_website_data_manager(webkit_web_context_get_default()), 0x3FFF, 0, IntPtr.Zero,
                    Marshal.GetFunctionPointerForDelegate(OnCleared), I(o, "id", 0));
                break;
            case "quit": gtk_main_quit(); break;
        }
    }

    static void AddScript(View v, string js)
    {
        var s = webkit_user_script_new(js, 1 /*top frame only*/, 0 /*document start*/, IntPtr.Zero, IntPtr.Zero);
        webkit_user_content_manager_add_script(v.Ucm, s); webkit_user_script_unref(s);
    }

    static string? Str(IntPtr p) { if (p == IntPtr.Zero) return null; var s = Marshal.PtrToStringUTF8(p); return s; }
    static string? TakeStr(IntPtr p) { if (p == IntPtr.Zero) return null; var s = Marshal.PtrToStringUTF8(p); g_free(p); return s; }

    static void OnMsgImpl(IntPtr ucm, IntPtr jsResult, IntPtr _)
    {
        ByPtr.TryGetValue(ucm, out var v);
        var val = webkit_javascript_result_get_js_value(jsResult);
        var data = TakeStr(jsc_value_to_string(val)) ?? "null";
        Send(new JsonObject { ["ev"] = "message", ["view"] = v?.Id ?? "", ["url"] = v != null ? Str(webkit_web_view_get_uri(v.Web)) : null, ["data"] = data });
    }

    static void OnLoadImpl(IntPtr view, int ev, IntPtr _)
    {
        if (ev != 3) return;   // WEBKIT_LOAD_FINISHED
        ByPtr.TryGetValue(view, out var v);
        Send(new JsonObject { ["ev"] = "loaded", ["view"] = v?.Id ?? "", ["url"] = Str(webkit_web_view_get_uri(view)) });
    }

    static int OnPolicyImpl(IntPtr view, IntPtr decision, int type, IntPtr _)
    {
        if (type != 1) return 0;   // only NEW_WINDOW_ACTION: pop-ups/target=_blank
        var uri = Str(webkit_uri_request_get_uri(webkit_navigation_action_get_request(webkit_navigation_policy_decision_get_navigation_action(decision)))) ?? "";
        webkit_policy_decision_ignore(decision);
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Host == "epikchat.com" || u.Host.EndsWith(".epikchat.com")))
            webkit_web_view_load_uri(view, uri);   // keep EpikChat inside the signed-in window
        else if (uri.StartsWith("https://") || uri.StartsWith("http://"))
            try { Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { uri }, UseShellExecute = false }); } catch { }
        return 1;
    }

    /// <summary>2.8.2: report the window size (the app remembers it); only when it changed.</summary>
    static int OnConfigureImpl(IntPtr win, IntPtr ev, IntPtr _)
    {
        if (ByPtr.TryGetValue(win, out var v))
        {
            gtk_window_get_size(win, out var w, out var h);
            if (w != v.LastW || h != v.LastH) { v.LastW = w; v.LastH = h; Send(new JsonObject { ["ev"] = "size", ["view"] = v.Id, ["w"] = w, ["h"] = h }); }
        }
        return 0;   // let GTK handle it
    }

    static int OnDeleteImpl(IntPtr win, IntPtr ev, IntPtr _)
    {
        if (ByPtr.TryGetValue(win, out var v)) { gtk_widget_hide(win); Send(new JsonObject { ["ev"] = "closed", ["view"] = v.Id }); }
        return 1;   // never destroy on the window-manager close button; the parent decides
    }

    static void OnEvalImpl(IntPtr src, IntPtr res, IntPtr d)
    {
        int seq = (int)d; Evals.Remove(seq, out var t);
        var val = webkit_web_view_evaluate_javascript_finish(src, res, out var err);
        var o = new JsonObject { ["ev"] = "result", ["view"] = t.view, ["id"] = t.id };
        if (val == IntPtr.Zero)
        {
            string msg = "eval failed";
            if (err != IntPtr.Zero) { msg = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(err, 8)) ?? msg; g_error_free(err); }
            o["ok"] = false; o["error"] = msg;
        }
        else
        {
            o["ok"] = true;
            o["value"] = jsc_value_is_undefined(val) ? "null" : TakeStr(jsc_value_to_json(val, 0)) ?? "null";
            g_object_unref(val);
        }
        Send(o);
    }
}
