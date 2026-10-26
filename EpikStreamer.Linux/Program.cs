using Avalonia;
using EpikStreamer.Browser;
namespace EpikStreamer;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // WebKitGTK helper process: must not touch Avalonia or write anything else to stdout
        if (args.Contains("--browser-host")) return BrowserHost.Run(args);
        if (args.Length > 0 && args[0].StartsWith("--selftest", StringComparison.Ordinal)) return SelfTest.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--version")) { Console.WriteLine("EpikStreamer " + Core.Updater.Current.ToString(3)); return 0; }
        LinuxSecretStore.Install();
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { BrowserClient.Shutdown(); }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
