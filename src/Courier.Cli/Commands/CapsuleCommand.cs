using System.CommandLine;
using Courier.Core.Capsules;

namespace Courier.Cli.Commands;

/// <summary>
/// <c>courier capsule</c>. Inspects a capsule without opening the app.
/// </summary>
/// <remarks>
/// Reading only, on purpose. CAP-09 says a capsule is inert data, and the CLI is the most likely
/// place for that promise to be quietly broken — a "replay this capsule on the agent" command
/// would turn a file someone emailed into remote execution against whatever the agent can reach.
/// </remarks>
internal static class CapsuleCommand
{
    public static Command Build()
    {
        var fileArgument = new Argument<string>("file")
        {
            Description = "The .capsule file to inspect.",
        };

        var showRedactionsOption = new Option<bool>("--redactions")
        {
            Description = "List everything that was replaced before this capsule was written.",
        };

        var inspect = new Command("inspect", "Show what is inside a capsule.")
        {
            fileArgument,
            showRedactionsOption,
        };

        inspect.SetAction((parse, ct) => InspectAsync(
            parse.GetValue(fileArgument)!,
            parse.GetValue(showRedactionsOption),
            ct));

        return new Command("capsule", "Work with capsule files.") { inspect };
    }

    private static async Task<int> InspectAsync(string path, bool showRedactions, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"'{path}' does not exist.");
            return 2;
        }

        await using var stream = File.OpenRead(path);
        var capsule = await CapsuleArchive.ReadAsync(stream, ct).ConfigureAwait(false);

        Console.WriteLine($"{capsule.Manifest.Title}");
        Console.WriteLine($"  captured     {capsule.Manifest.CapturedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"  environment  {capsule.Manifest.EnvironmentName ?? "(none)"}  — name only, values are never included");
        Console.WriteLine($"  trace id     {capsule.Manifest.TraceId ?? "(none)"}");
        Console.WriteLine($"  client       {capsule.Manifest.ClientVersion}");
        Console.WriteLine($"  steps        {capsule.Steps.Count}");
        Console.WriteLine();

        foreach (var step in capsule.Steps)
        {
            Console.WriteLine($"  {step.Ordinal}. {step.Request.Method} {step.Request.Url}");

            if (step.Response is { } response)
            {
                Console.WriteLine(response.TransportFailure is { } failure
                    ? $"     the request never left: {failure}"
                    : $"     {response.Status} {response.ReasonPhrase}  {response.ContentLength} bytes  {response.ElapsedMilliseconds:0} ms");
            }
        }

        var placeholders = capsule.ReferencedPlaceholders();
        if (placeholders.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Placeholders to bind before sending:");

            foreach (var placeholder in placeholders)
            {
                Console.WriteLine($"    {{{{{placeholder}}}}}");
            }
        }

        if (showRedactions && capsule.Redactions.Entries.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Replaced before this capsule was written:");

            foreach (var entry in capsule.Redactions.Entries)
            {
                var how = entry.WasAutomatic ? "automatic" : "decided by the exporter";
                Console.WriteLine($"    {entry.Location,-40} {entry.Placeholder,-24} {how} — {entry.Reason}");
            }
        }

        return 0;
    }
}
