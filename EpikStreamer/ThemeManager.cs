using System.Windows;
using System.Windows.Media;
namespace EpikStreamer;

/// <summary>Live theme switching: replaces the brush resources that every style references via DynamicResource.</summary>
public static class ThemeManager
{
    public static readonly string[] Names = { "Dark", "Light", "Midnight Blue", "Epik Purple" };
    public static event Action? Changed;

    record Palette(string Bg, string Panel, string Border, string Fg, string Dim, string Accent, string AccentFg, string SecondaryBg, string Hover, string Input, string Thumb);

    static Palette Get(string name) => name switch
    {
        "Light" => new("#F4F4F7", "#FFFFFF", "#C9C9D3", "#1E1E24", "#5E5E6E", "#6A4CF0", "#FFFFFF", "#E2E2EA", "#EAE6FF", "#FFFFFF", "#B4B4C2"),
        "Midnight Blue" => new("#0F1626", "#17213A", "#2A3A5E", "#E3EAF7", "#8EA0C4", "#3D8BFF", "#FFFFFF", "#24345A", "#1E2C4C", "#131C31", "#3A4E7A"),
        "Epik Purple" => new("#1A1229", "#24193A", "#3E2C63", "#EEE6FF", "#A99BC8", "#B04CFF", "#FFFFFF", "#382659", "#2F2149", "#201633", "#5A4290"),
        _ => new("#1E1E24", "#2A2A33", "#3C3C48", "#E6E6EA", "#9A9AA8", "#7C5CFF", "#FFFFFF", "#3A3A46", "#34343F", "#2A2A33", "#55556A"),
    };

    public static string Current { get; private set; } = "Dark";

    public static void Apply(string name)
    {
        if (!Names.Contains(name)) name = "Dark";
        var p = Get(name); var r = Application.Current.Resources;
        void Set(string key, string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); r[key] = b; }
        Set("Bg", p.Bg); Set("Panel", p.Panel); Set("Border", p.Border); Set("Fg", p.Fg); Set("Dim", p.Dim); Set("Accent", p.Accent);
        Set("AccentFg", p.AccentFg); Set("SecondaryBg", p.SecondaryBg); Set("Hover", p.Hover); Set("Input", p.Input); Set("Thumb", p.Thumb);
        Current = name; Changed?.Invoke();
    }
    public static bool IsLight => Current == "Light";
}
