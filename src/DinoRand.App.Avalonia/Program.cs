using System;
using Avalonia;

namespace DinoRand.App;

internal static class Program
{
    // Avalonia's entry point. Keep this minimal and side-effect free (init happens in
    // App.OnFrameworkInitializationCompleted) so design tooling can call BuildAvaloniaApp.
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()   // picks Win32 / X11 / macOS backend at runtime
            .LogToTrace();
}
