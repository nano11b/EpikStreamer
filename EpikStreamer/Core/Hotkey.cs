namespace EpikStreamer.Core;

/// <summary>Parses "Ctrl+Alt+S" into Win32 RegisterHotKey modifiers + virtual-key code (pure; unit-tested).</summary>
public static class Hotkey
{
    public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    public static bool TryParse(string? text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts[..^1])
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": mods |= MOD_WIN; break;
                default: return false;
            }
        var k = parts[^1].ToUpperInvariant();
        if (k.Length == 1 && (char.IsAsciiLetterUpper(k[0]) || char.IsAsciiDigit(k[0]))) vk = k[0];
        else if (k.StartsWith('F') && int.TryParse(k[1..], out var f) && f is >= 1 and <= 24) vk = (uint)(0x70 + f - 1);
        else if (k.StartsWith("NUM") && int.TryParse(k[3..], out var n) && n is >= 0 and <= 9) vk = (uint)(0x60 + n);
        else vk = k switch
        {
            "SPACE" => 0x20, "PAUSE" => 0x13, "HOME" => 0x24, "END" => 0x23, "PGUP" or "PAGEUP" => 0x21, "PGDN" or "PAGEDOWN" => 0x22,
            "INSERT" or "INS" => 0x2D, "DELETE" or "DEL" => 0x2E, "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            "MEDIAPLAYPAUSE" => 0xB3, "MEDIANEXT" => 0xB0, "MEDIASTOP" => 0xB2, "VOLUMEMUTE" => 0xAD,
            _ => 0,
        };
        if (vk == 0) return false;
        // a bare letter/digit as a global hotkey would steal typing: require a modifier unless it's an F-key/media key
        if (mods == 0 && (vk < 0x70 || vk > 0x87) && vk < 0xAD) return false;
        return true;
    }
}

/// <summary>Parses `ffmpeg -list_devices true -f dshow -i dummy` stderr (both old and new ffmpeg formats).</summary>
public static class DshowDevices
{
    public static List<string> ParseAudio(string stderr)
    {
        var res = new List<string>(); bool audioSection = false;
        foreach (var raw in stderr.Split('\n'))
        {
            var l = raw.TrimEnd('\r');
            if (l.Contains("DirectShow audio devices")) { audioSection = true; continue; }
            if (l.Contains("DirectShow video devices")) { audioSection = false; continue; }
            if (l.Contains("Alternative name")) continue;
            int q1 = l.IndexOf('"'), q2 = l.LastIndexOf('"');
            if (q1 < 0 || q2 <= q1) continue;
            var name = l[(q1 + 1)..q2];
            if (l.Contains("(audio)") || (audioSection && !l.Contains("(video)"))) if (!res.Contains(name)) res.Add(name);
        }
        return res;
    }
}
