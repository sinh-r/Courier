using System;
using Avalonia;
using Courier.App.Services;

namespace Courier.App;

internal static class Program
{
    /// <summary>
    /// Entry point.
    /// </summary>
    /// <remarks>
    /// SEC-01: nothing here contacts anything. There is no update check, no analytics and no crash
    /// reporter, so an unhandled exception is written to the local log and the session state is
    /// preserved for NFR-07's crash recovery — and that is all that happens.
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return 1;
        }
    }

    /// <summary>Used by the Avalonia designer and the headless UI tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<CourierApplication>()
            .UsePlatformDetect()
            .LogToTrace();
}
