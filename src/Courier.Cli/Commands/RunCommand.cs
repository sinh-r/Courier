using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Courier.Cli.Services;
using Courier.Core.Collections;
using Courier.Scripting.Running;

namespace Courier.Cli.Commands;

/// <summary>
/// <c>courier run</c>. STOR-06: runs a collection with assertions and a machine-readable report,
/// suitable for CI.
/// </summary>
internal static class RunCommand
{
    public static Command Build()
    {
        var folderArgument = new Argument<string>("collection")
        {
            Description = "Collection folder to run.",
        };

        var environmentOption = new Option<string?>("--environment", "-e")
        {
            Description = "Environment name. Shared values come from the collection folder; local values from the credential store.",
        };

        var dataOption = new Option<string?>("--data", "-d")
        {
            Description = "CSV or JSON data file. One iteration per row.",
        };

        var jsonOption = new Option<string?>("--json")
        {
            Description = "Write the JSON report to this path.",
        };

        var junitOption = new Option<string?>("--junit")
        {
            Description = "Write a JUnit XML report to this path.",
        };

        var bailOption = new Option<bool>("--bail")
        {
            Description = "Stop at the first failing request.",
        };

        var filterOption = new Option<string?>("--filter")
        {
            Description = "Run only requests whose name or URL contains this text.",
        };

        var command = new Command("run", "Run a collection and report the results.")
        {
            folderArgument,
            environmentOption,
            dataOption,
            jsonOption,
            junitOption,
            bailOption,
            filterOption,
        };

        command.SetAction((parse, ct) => ExecuteAsync(
            parse.GetValue(folderArgument)!,
            parse.GetValue(environmentOption),
            parse.GetValue(dataOption),
            parse.GetValue(jsonOption),
            parse.GetValue(junitOption),
            parse.GetValue(bailOption),
            parse.GetValue(filterOption),
            ct));

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string folder,
        string? environmentName,
        string? dataPath,
        string? jsonPath,
        string? junitPath,
        bool bail,
        string? filter,
        CancellationToken ct)
    {
        using var services = CliServices.Build();

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
            Console.Error.WriteLine(
                filter is null
                    ? $"No requests found in {folder}."
                    : $"No requests in {folder} match '{filter}'.");

            return 2;
        }

        var environment = environmentName is null
            ? null
            : CollectionLoader.LoadEnvironment(folder, environmentName);

        var plan = new RunPlan
        {
            CollectionName = collection.Definition.Name,
            Requests = requests,
            EnvironmentName = environmentName,
            EnvironmentVariables = environment?.Shared ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CollectionVariables = collection.Definition.Variables,
            Data = dataPath is null ? [] : DataFileReader.Read(dataPath),
            DefaultSettings = collection.Definition.Settings,
            InjectTraceParent = collection.Definition.InjectTraceParent,
            StopOnFailure = bail,
        };

        // Every host the collection names becomes a legitimate destination. SEC-01 denies anything
        // that is not, and on a build agent that is the difference between a scripted run and an
        // unreviewed outbound connection.
        foreach (var request in requests)
        {
            if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            {
                services.EgressPolicy.Register(uri, Core.Privacy.EgressPurpose.TargetRequest);
            }
        }

        var runner = new CollectionRunner(services.Executor, services.Variables);
        runner.RequestCompleted += Print;

        var report = await runner.RunAsync(plan, ct).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(
            $"{report.Passed}/{report.Total} passed in {report.Elapsed.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s");

        if (jsonPath is not null)
        {
            await File.WriteAllTextAsync(jsonPath, report.ToJson(), ct).ConfigureAwait(false);
            Console.WriteLine($"Wrote {jsonPath}");
        }

        if (junitPath is not null)
        {
            await File.WriteAllTextAsync(junitPath, report.ToJUnitXml(), ct).ConfigureAwait(false);
            Console.WriteLine($"Wrote {junitPath}");
        }

        return report.ExitCode;
    }

    /// <summary>
    /// One line per request, with the failing assertions underneath. A build log is read by someone
    /// who already knows something is wrong, so the failures need to be the part that stands out.
    /// </summary>
    private static void Print(RequestRunResult result)
    {
        var mark = result.Passed ? "pass" : "FAIL";
        var status = result.Error is not null ? "---" : result.Status.ToString();

        Console.WriteLine(
            $"  {mark}  {result.Method,-6} {Truncate(result.Url, 68),-68} {status,4}  "
            + $"{result.Elapsed.TotalMilliseconds,6:0} ms");

        if (result.Error is not null)
        {
            Console.WriteLine($"        {result.Error}");
            return;
        }

        foreach (var (expression, passed, detail) in result.AllAssertions().Where(a => !a.Passed))
        {
            Console.WriteLine($"        {expression}");

            if (detail is not null)
            {
                Console.WriteLine($"          {detail}");
            }
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : $"{value[..(max - 1)]}…";
}

/// <summary>
/// Reads a data file for a data-driven run. TEST-07.
/// </summary>
internal static class DataFileReader
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> Read(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".json" => ReadJson(path),
            ".csv" or ".tsv" => ReadDelimited(path, extension == ".tsv" ? '\t' : ','),
            _ => throw new ArgumentException($"'{extension}' is not a data file Courier reads. Use .csv, .tsv or .json."),
        };
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("A JSON data file must be an array of objects, one per iteration.");
        }

        return
        [
            .. document.RootElement.EnumerateArray().Select(row =>
                (IReadOnlyDictionary<string, string>)row.EnumerateObject()
                    .ToDictionary(
                        p => p.Name,
                        p => p.Value.ValueKind == JsonValueKind.String
                            ? p.Value.GetString() ?? string.Empty
                            : p.Value.ToString(),
                        StringComparer.Ordinal)),
        ];
    }

    /// <summary>
    /// A minimal RFC 4180 reader: quoted fields, doubled quotes inside them, and embedded commas.
    /// Enough for a data file a person exported from a spreadsheet, which is where these come from.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadDelimited(string path, char delimiter)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2)
        {
            return [];
        }

        var headers = SplitLine(lines[0], delimiter);
        var rows = new List<IReadOnlyDictionary<string, string>>(lines.Length - 1);

        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
        {
            var values = SplitLine(line, delimiter);
            var row = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var i = 0; i < headers.Count && i < values.Count; i++)
            {
                row[headers[i]] = values[i];
            }

            rows.Add(row);
        }

        return rows;
    }

    private static List<string> SplitLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }
}
