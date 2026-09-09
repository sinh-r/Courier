using System.CommandLine;
using System.Text.Json;
using Courier.Cli.Services;
using Courier.Core.Collections;
using Courier.Scanner;

namespace Courier.Cli.Commands;

/// <summary>
/// <c>courier scan</c>. STOR-07: collection generation in a pipeline, without the desktop app.
/// </summary>
/// <remarks>
/// Also what the MSBuild task shells out to (SCAN-11). That is why the JSON report is a first-class
/// output rather than an afterthought: the task parses it, and a build-time integration that has to
/// scrape human-readable console text is one console tweak away from breaking.
/// </remarks>
internal static class ScanCommand
{
    public static Command Build()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "A folder or .sln to scan.",
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Collection folder to write endpoints.generated.yaml into. Omit to print a summary only.",
        };

        var jsonOption = new Option<string?>("--json")
        {
            Description = "Write a machine-readable scan report to this path.",
        };

        var baseUrlOption = new Option<string>("--base-url-variable")
        {
            Description = "Variable name to prefix generated URLs with.",
            DefaultValueFactory = _ => "baseUrl",
        };

        var failOnUnresolvedOption = new Option<bool>("--fail-on-unresolved")
        {
            Description = "Exit non-zero if any endpoint could not be fully derived.",
        };

        var command = new Command("scan", "Derive an API collection from .NET source.")
        {
            pathArgument,
            outputOption,
            jsonOption,
            baseUrlOption,
            failOnUnresolvedOption,
        };

        command.SetAction((parse, ct) => ExecuteAsync(
            parse.GetValue(pathArgument)!,
            parse.GetValue(outputOption),
            parse.GetValue(jsonOption),
            parse.GetValue(baseUrlOption)!,
            parse.GetValue(failOnUnresolvedOption),
            ct));

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string path,
        string? output,
        string? jsonPath,
        string baseUrlVariable,
        bool failOnUnresolved,
        CancellationToken ct)
    {
        using var services = CliServices.Build();

        var scanner = new SolutionScanner();

        var previous = output is not null ? CollectionWriter.ReadCache(output) : null;
        var result = scanner.Scan(path, previous?.Hashes, previous?.Endpoints, ct);

        Console.WriteLine(
            $"{result.Endpoints.Count} endpoints, {result.Unresolved.Count} unresolved, "
            + $"{result.Environments.Count} environments, in {result.Elapsed.TotalMilliseconds:0} ms");

        // REQUIREMENTS 9: unresolved endpoints are reported, never omitted. On a build agent that
        // means printing them, because nobody is going to open a UI to find out.
        foreach (var unresolved in result.Unresolved)
        {
            Console.WriteLine($"  unresolved  {unresolved.DeclaringType}.{unresolved.ActionName}");
            Console.WriteLine($"              {unresolved.Reason}");
        }

        EnvironmentWriteReport? writeReport = null;

        if (output is not null)
        {
            CollectionWriter.Write(output, result, baseUrlVariable);
            Console.WriteLine($"Wrote {Path.Combine(output, CollectionFormat.GeneratedFileName)}");

            writeReport = await CollectionWriter.WriteEnvironments(output, result, services.SecretStore, ct)
                .ConfigureAwait(false);

            // SEC-03, in console form: a value is never printed, only where it landed or why it
            // couldn't. On a build agent — CliServices' EnvironmentSecretStore refuses on principle —
            // every one of these becomes a failure line rather than a silent no-op.
            if (writeReport.SecretsStored > 0)
            {
                Console.WriteLine($"  {writeReport.SecretsStored} secret variable(s) stored in {services.SecretStore.LocationDescription}");
            }

            foreach (var failure in writeReport.SecretFailures)
            {
                Console.WriteLine($"  secret not stored  {failure.Environment}/{failure.VariableName}");
                Console.WriteLine($"                      {failure.Reason}");
            }
        }

        if (jsonPath is not null)
        {
            File.WriteAllText(jsonPath, ToJson(result, writeReport));
            Console.WriteLine($"Wrote {jsonPath}");
        }

        return failOnUnresolved && result.Unresolved.Count > 0 ? 1 : 0;
    }

    private static string ToJson(ScanResult result, EnvironmentWriteReport? writeReport) => JsonSerializer.Serialize(
        new
        {
            tier = result.Tier.ToString(),
            elapsedMs = (long)result.Elapsed.TotalMilliseconds,
            endpoints = result.Endpoints.Select(e => new
            {
                id = e.Id,
                method = e.Method,
                route = e.RouteTemplate,
                declaringType = e.DeclaringType,
                action = e.ActionName,
                file = e.SourceFile,
                line = e.Line,
                scopes = e.RequiredScopes,
                authorization = e.Authorization.Describe(),
                notes = e.PartialResolutionNotes,
            }),
            unresolved = result.Unresolved.Select(u => new
            {
                declaringType = u.DeclaringType,
                action = u.ActionName,
                file = u.SourceFile,
                line = u.Line,
                reason = u.Reason,
            }),
            environments = result.Environments.Select(e => new
            {
                name = e.Name,
                baseUrl = e.BaseUrl,
                source = e.Source,
                variableCount = e.Variables?.Count ?? 0,
                secretVariableCount = e.SecretVariables?.Count ?? 0,
            }),
            secretsStored = writeReport?.SecretsStored ?? 0,
            secretFailures = (writeReport?.SecretFailures ?? []).Select(f => new
            {
                environment = f.Environment,
                variable = f.VariableName,
                reason = f.Reason,
            }),
        },
        new JsonSerializerOptions { WriteIndented = true });
}
