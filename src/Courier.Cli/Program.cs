using System.CommandLine;
using Courier.Cli.Commands;

namespace Courier.Cli;

/// <summary>
/// The CLI. STOR-06 and STOR-07: run a collection with assertions and a machine-readable report,
/// and expose the source scanner, both without the desktop app.
/// </summary>
/// <remarks>
/// SEC-01 applies here exactly as it does in the app: this binary contacts the hosts in the
/// collection and nothing else. There is no update check and no usage reporting, which for a tool
/// that runs on a build agent matters more than it does on a laptop.
/// </remarks>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var root = new RootCommand(
            "Courier — a local-first API client. Runs collections and derives them from source, "
            + "with no account and no network access beyond the hosts you name.");

        root.Subcommands.Add(ScanCommand.Build());
        root.Subcommands.Add(RunCommand.Build());
        root.Subcommands.Add(ExportCommand.Build());
        root.Subcommands.Add(CapsuleCommand.Build());

        return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
    }
}
