namespace EpikStreamer.Core;
/// <summary>2.8.2: the app version shown in the title bar, read from the assembly at runtime (no hardcoded version strings).</summary>
public static class AppInfo
{
    public static string Version => Updater.Current.ToString(3);
    public static string Title => "EpikStreamer " + Version;
}
