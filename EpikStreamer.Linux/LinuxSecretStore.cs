using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace EpikStreamer;

/// <summary>
/// Stream keys / tokens on Linux. Preferred: the desktop keyring via libsecret (Secret Service: GNOME Keyring, KWallet ≥5.97, KeePassXC).
/// The profile then only stores "ls:&lt;id&gt;". Fallback (no keyring / no D-Bus session): AES-256-GCM with a random per-user key in
/// ~/.config/epikstreamer/secret.key (mode 0600); stored as "aes:&lt;base64 nonce|tag|cipher&gt;".
/// </summary>
public static unsafe class LinuxSecretStore
{
    const string Lib = "libsecret-1.so.0", GLib = "libglib-2.0.so.0";
    [DllImport(Lib)] static extern int secret_password_storev_sync(IntPtr schema, IntPtr attrs, IntPtr collection, string label, string password, IntPtr cancel, out IntPtr error);
    [DllImport(Lib)] static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attrs, IntPtr cancel, out IntPtr error);
    [DllImport(Lib)] static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attrs, IntPtr cancel, out IntPtr error);
    [DllImport(Lib)] static extern void secret_password_free(IntPtr p);
    [DllImport(GLib)] static extern IntPtr g_hash_table_new(IntPtr hash, IntPtr eq);
    [DllImport(GLib)] static extern int g_hash_table_insert(IntPtr t, IntPtr k, IntPtr v);
    [DllImport(GLib)] static extern void g_hash_table_unref(IntPtr t);
    [DllImport(GLib)] static extern void g_error_free(IntPtr e);

    public static string ConfigDir => Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "epikstreamer");
    public static string KeyFile => Path.Combine(ConfigDir, "secret.key");
    /// <summary>"libsecret", "file" or "file (keyring error: …)".</summary>
    public static string Backend { get; private set; } = "?";
    /// <summary>Set EPIK_SECRET_BACKEND=file to force the AES fallback (tests / headless servers).</summary>
    static bool ForceFile => Environment.GetEnvironmentVariable("EPIK_SECRET_BACKEND") == "file";

    static IntPtr _schema, _nameKey, _strHash, _strEq;
    static bool? _libOk;

    public static void Install() { Core.Secret.PlatformProtect = Protect; Core.Secret.PlatformUnprotect = Unprotect; }

    public static string? Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        if (!ForceFile && LibOk())
        {
            // deterministic id (HMAC with the per-user key) so re-saving the same key replaces the keyring item instead of piling up copies
            var id = Convert.ToHexString(HMACSHA256.HashData(Key(), Encoding.UTF8.GetBytes(plain)))[..24].ToLowerInvariant();
            var err = Store(id, plain);
            if (err == null) { Backend = "libsecret"; return "ls:" + id; }
            Backend = "file (keyring error: " + err + ")";
        }
        else Backend = "file";
        return "aes:" + AesEncrypt(plain);
    }

    public static string Unprotect(string stored)
    {
        try
        {
            if (stored.StartsWith("ls:", StringComparison.Ordinal)) return LibOk() ? Lookup(stored[3..]) ?? "" : "";
            if (stored.StartsWith("aes:", StringComparison.Ordinal)) return AesDecrypt(stored[4..]);
        }
        catch { }
        return "";   // e.g. a DPAPI blob copied from Windows: just ask for the key again
    }

    /// <summary>Removes a keyring entry (best effort).</summary>
    public static void Forget(string? stored)
    {
        if (stored == null || !stored.StartsWith("ls:") || !LibOk()) return;
        var attrs = Attrs(stored[3..]);
        try { secret_password_clearv_sync(Schema(), attrs, IntPtr.Zero, out var e); Free(e); } finally { g_hash_table_unref(attrs); }
    }

    // ── libsecret ───────────────────────────────────────────────────────────────────────────────────────────────
    static bool LibOk()
    {
        if (_libOk != null) return _libOk.Value;
        try
        {
            var glib = NativeLibrary.Load(GLib);
            _strHash = NativeLibrary.GetExport(glib, "g_str_hash"); _strEq = NativeLibrary.GetExport(glib, "g_str_equal");
            NativeLibrary.Load(Lib);
            if (Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is not { Length: > 0 } && !File.Exists($"/run/user/{getuid()}/bus")) return (_libOk = false).Value;
            _libOk = true;
        }
        catch { _libOk = false; }
        return _libOk.Value;
    }
    [DllImport("libc")] static extern uint getuid();

    static IntPtr Schema()
    {
        if (_schema != IntPtr.Zero) return _schema;
        // struct SecretSchema { const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32]; + reserved } (all zero-terminated)
        var s = Marshal.AllocHGlobal(1024); new Span<byte>((void*)s, 1024).Clear();
        Marshal.WriteIntPtr(s, 0, Marshal.StringToCoTaskMemUTF8("io.github.nano11b.EpikStreamer"));
        Marshal.WriteInt32(s, 8, 0);                                          // SECRET_SCHEMA_NONE
        _nameKey = Marshal.StringToCoTaskMemUTF8("id");
        Marshal.WriteIntPtr(s, 16, _nameKey); Marshal.WriteInt32(s, 24, 0);  // { "id", SECRET_SCHEMA_ATTRIBUTE_STRING }
        return _schema = s;
    }
    static IntPtr Attrs(string id)
    {
        Schema();
        var t = g_hash_table_new(_strHash, _strEq);
        g_hash_table_insert(t, _nameKey, Marshal.StringToCoTaskMemUTF8(id));   // tiny, intentionally not freed (lives as long as the call)
        return t;
    }
    static string? Free(IntPtr e)
    {
        if (e == IntPtr.Zero) return null;
        var m = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(e, 8)); g_error_free(e); return m ?? "error";
    }
    static string? Store(string id, string secret)
    {
        var a = Attrs(id);
        try { var ok = secret_password_storev_sync(Schema(), a, IntPtr.Zero, "EpikStreamer stream key", secret, IntPtr.Zero, out var e); var m = Free(e); return ok != 0 ? null : m ?? "store failed"; }
        finally { g_hash_table_unref(a); }
    }
    static string? Lookup(string id)
    {
        var a = Attrs(id);
        try
        {
            var p = secret_password_lookupv_sync(Schema(), a, IntPtr.Zero, out var e); Free(e);
            if (p == IntPtr.Zero) return null;
            var s = Marshal.PtrToStringUTF8(p); secret_password_free(p); return s;
        }
        finally { g_hash_table_unref(a); }
    }

    // ── AES file fallback ───────────────────────────────────────────────────────────────────────────────────────
    static byte[] Key()
    {
        Directory.CreateDirectory(ConfigDir);
        try { File.SetUnixFileMode(ConfigDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        if (File.Exists(KeyFile))
        {
            var k = File.ReadAllBytes(KeyFile);
            if (k.Length == 32) { File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); return k; }
        }
        var key = RandomNumberGenerator.GetBytes(32);
        using (var f = new FileStream(KeyFile, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            f.Write(key);
        File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }
    static string AesEncrypt(string plain)
    {
        var p = Encoding.UTF8.GetBytes(plain); var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var c = new byte[p.Length];
        using var g = new AesGcm(Key(), 16); g.Encrypt(nonce, p, c, tag, Encoding.ASCII.GetBytes("epikstreamer-v1"));
        return Convert.ToBase64String(nonce.Concat(tag).Concat(c).ToArray());
    }
    static string AesDecrypt(string b64)
    {
        var all = Convert.FromBase64String(b64); var c = all[28..]; var p = new byte[c.Length];
        using var g = new AesGcm(Key(), 16); g.Decrypt(all[..12], c, all[12..28], p, Encoding.ASCII.GetBytes("epikstreamer-v1"));
        return Encoding.UTF8.GetString(p);
    }
}
