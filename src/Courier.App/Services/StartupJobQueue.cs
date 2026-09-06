using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Courier.App.ViewModels;
using Courier.Core.Storage;

namespace Courier.App.Services;

/// <summary>
/// Everything that is not needed to draw the first frame.
/// </summary>
/// <remarks>
/// <para>
/// PERF-01 is a release gate: 1.5s cold to interactive, 800ms warm, measured on a 4-core, 16GB,
/// HDD-backed corporate image. TECH_SPEC 4 is specific about how that is met — "defer all
/// non-essential initialization past first paint; no scanner, no telemetry client, no git status on
/// startup".
/// </para>
/// <para>
/// This queue is the mechanism. Jobs run at background priority after the window has actually
/// rendered, one at a time, so a slow one cannot starve input. Adding work to the constructor
/// graph instead of here is how the startup budget gets lost.
/// </para>
/// </remarks>
public sealed class StartupJobQueue
{
    private readonly AppServices _services;
    private readonly List<(string Name, Func<Task> Job)> _jobs = [];

    public StartupJobQueue(AppServices services) => _services = services;

    /// <summary>Timings for each deferred job, surfaced in diagnostics and the perf tests.</summary>
    public List<(string Name, TimeSpan Elapsed)> Timings { get; } = [];

    /// <summary>Time from process start to the first rendered frame. The PERF-01 number.</summary>
    public TimeSpan? TimeToFirstFrame { get; private set; }

    public void Enqueue(string name, Func<Task> job) => _jobs.Add((name, job));

    /// <summary>
    /// Hooks the window's first render and runs the queue after it. Uses
    /// <see cref="DispatcherPriority.Background"/> so a deferred job yields to anything the user does.
    /// </summary>
    public void RunAfterFirstPaint(Window window, MainWindowViewModel shell)
    {
        EnqueueDefaults(shell);

        var started = Stopwatch.GetTimestamp();
        var fired = false;

        window.Opened += (_, _) =>
        {
            if (fired)
            {
                return;
            }

            fired = true;

            // Opened fires before the first frame is composed; one background-priority hop puts
            // this genuinely after it, which is what the budget is measured against.
            Dispatcher.UIThread.Post(
                async () =>
                {
                    // From process start, not from this method: PERF-01 measures what the user
                    // waits through, which includes runtime startup and assembly loading.
                    TimeToFirstFrame = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
                    ReportStartup();

                    await RunAsync().ConfigureAwait(true);
                },
                DispatcherPriority.Background);
        };
    }

    public async Task RunAsync()
    {
        foreach (var (name, job) in _jobs)
        {
            var started = Stopwatch.GetTimestamp();

            try
            {
                await job().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // A deferred job failing must never take the window with it. The tree simply shows
                // no git status, or history stays empty, and the log says why.
                CrashLog.Write(ex, $"startup job '{name}'");
            }

            Timings.Add((name, Stopwatch.GetElapsedTime(started)));

            // Yield between jobs so a burst of them cannot make the window feel stuck.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Writes the time to first frame where the budget runner can read it, and exits if asked.
    /// </summary>
    /// <remarks>
    /// PERF-01 is a release gate and cannot be measured from outside the process: a stopwatch
    /// around the launch measures the shell, the runtime and the window manager. This reports what
    /// the user actually waits through, and only when the runner asks — the environment variable
    /// keeps a diagnostic out of an ordinary session.
    /// </remarks>
    private void ReportStartup()
    {
        var reportPath = Environment.GetEnvironmentVariable("COURIER_STARTUP_REPORT");
        if (reportPath is null || TimeToFirstFrame is not { } elapsed)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            File.WriteAllText(reportPath, elapsed.TotalMilliseconds.ToString("0.##"));
        }
        catch (IOException ex)
        {
            CrashLog.Write(ex, "startup report");
        }

        if (Environment.GetEnvironmentVariable("COURIER_EXIT_AFTER_STARTUP") == "1")
        {
            Environment.Exit(0);
        }
    }

    private void EnqueueDefaults(MainWindowViewModel shell)
    {
        Enqueue("storage", () =>
        {
            StorageLocations.EnsureCreated();
            return Task.CompletedTask;
        });

        Enqueue("database", async () => await _services.DatabaseAsync().ConfigureAwait(false));

        Enqueue("history", shell.LoadHistoryAsync);

        Enqueue("git-status", shell.RefreshGitStatusAsync);

        Enqueue("session-restore", shell.RestoreSessionAsync);

        // Nothing contacts a network here. SEC-01: no update check, no analytics, no crash
        // reporting, and no "phone home to see if a newer version exists" hiding in a startup job.
    }
}
