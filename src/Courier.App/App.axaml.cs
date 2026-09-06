using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Courier.App.Services;
using Courier.App.ViewModels;
using Courier.App.Views;

namespace Courier.App;

/// <summary>
/// Application entry point and composition root.
/// </summary>
/// <remarks>
/// PERF-01 gives 1.5s cold to interactive on an HDD-backed corporate image, so nothing expensive
/// happens here. The scanner, the telemetry client, git status and the database are all deferred
/// past first paint by <see cref="StartupJobQueue"/>; the constructor graph reachable from this
/// method is deliberately shallow.
/// </remarks>
public sealed class CourierApplication : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Build(desktop.Args ?? []);
            var shell = new MainWindowViewModel(services);

            desktop.MainWindow = new MainWindow { DataContext = shell };
            desktop.ShutdownRequested += (_, _) => services.Dispose();

            // Everything that is not needed to draw the first frame runs after it.
            services.StartupJobs.RunAfterFirstPaint(desktop.MainWindow, shell);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
