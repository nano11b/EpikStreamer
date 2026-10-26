using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
namespace EpikStreamer;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            d.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;   // close-to-tray keeps running; Exit calls Shutdown
            d.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
    public static void Quit() { if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown(); }
}
