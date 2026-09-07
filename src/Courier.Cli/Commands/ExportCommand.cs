using System.CommandLine;
using Courier.Core.Collections;
using Courier.Core.Export;

namespace Courier.Cli.Commands;

/// <summary>
/// <c>courier export</c>. CORE-10, and the practical half of STOR-02: the collection stays useful
/// without this tool, so getting a request out of it must not require the app.
/// </summary>
internal static class ExportCommand
{
    public static Command Build()
    {
        var folderArgument = new Argument<string>("collection")
        {
            Description = "Collection folder to export from.",
        };

        var formatOption = new Option<string>("--format", "-f")
        {
            Description = "curl, http, csharp or python.",
            DefaultValueFactory = _ => "http",
        };

        var requestOption = new Option<string?>("--request", "-r")
        {
            Description = "Export only requests whose name or URL contains this text.",
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Write to this file instead of standard output.",
        };

        var command = new Command("export", "Export requests as curl, .http, C# or Python.")
        {
            folderArgument,
            formatOption,
            requestOption,
            outputOption,
        };

        command.SetAction(parse => Execute(
            parse.GetValue(folderArgument)!,
            parse.GetValue(formatOption)!,
            parse.GetValue(requestOption),
            parse.GetValue(outputOption)));

        return command;
    }

    private static int Execute(string folder, string format, string? filter, string? output)
    {
        var collection = CollectionLoader.Load(folder);
        var requests = collection.Requests;

        if (filter is not null)
        {
            requests = [.. requests.Where(r =>
                r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || r.Url.Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        if (requests.Count == 0)
        {
            Console.Error.WriteLine("No requests matched.");
            return 2;
        }

        var text = new System.Text.StringBuilder();

        foreach (var request in requests)
        {
            text.AppendLine(format.ToLowerInvariant() switch
            {
                "curl" => RequestExporters.ToCurl(request),
                "http" => RequestExporters.ToHttpFile(request, collection.Definition),
                "csharp" or "cs" => RequestExporters.ToCSharp(request),
                "python" or "py" => RequestExporters.ToPython(request),
                _ => throw new ArgumentException($"'{format}' is not a format Courier exports. Use curl, http, csharp or python."),
            });

            text.AppendLine();
        }

        if (output is not null)
        {
            File.WriteAllText(output, text.ToString());
            Console.Error.WriteLine($"Wrote {requests.Count} request(s) to {output}");
        }
        else
        {
            Console.Write(text.ToString());
        }

        return 0;
    }
}
