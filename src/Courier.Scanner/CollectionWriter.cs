using System.Text.Json;
using Courier.Core.Abstractions;
using Courier.Core.Collections;

namespace Courier.Scanner;

/// <summary>
/// Writes a scan result to a collection folder. SCAN-09, P3.
/// </summary>
/// <remarks>
/// Shared between <c>courier scan</c> and the desktop app's "sync from code" so both write the same
/// three files the same way and cannot drift apart. This was originally private to the CLI command;
/// promoted here so the GUI has something to call instead of reimplementing it.
/// </remarks>
public static class CollectionWriter
{
    /// <summary>
    /// Writes only the machine-owned file. SCAN-09 and P3: the overlay holding the user's payloads
    /// is never touched, which is what makes running this on every build — or every GUI rescan —
    /// safe.
    /// </summary>
    public static void Write(string folder, ScanResult result, string baseUrlVariable)
    {
        Directory.CreateDirectory(folder);

        var serializer = new CollectionSerializer();

        var set = new GeneratedEndpointSet
        {
            Source = result.Tier.ToString(),
            GeneratedUtc = DateTimeOffset.UtcNow,
            Endpoints = [.. result.Endpoints.Select(e => e.ToRequest(baseUrlVariable))],
        };

        File.WriteAllText(
            Path.Combine(folder, CollectionFormat.GeneratedFileName),
            serializer.SerializeGenerated(set));

        // The overlay is created empty if it does not exist, and left alone if it does.
        var overlayPath = Path.Combine(folder, CollectionFormat.OverlayFileName);
        if (!File.Exists(overlayPath))
        {
            File.WriteAllText(overlayPath, serializer.SerializeOverlay(new OverlaySet()));
        }

        WriteCache(folder, result);
    }

    /// <summary>
    /// Writes each environment the scan derived from <c>launchSettings.json</c>/
    /// <c>appsettings.*.json</c>/a Postman environment export, so <c>{{baseUrl}}</c> has something to
    /// resolve against. SCAN-07.
    /// </summary>
    /// <remarks>
    /// Never overwrites a file that already exists — same P3 reasoning as the overlay. A user who
    /// has started editing <c>environments/Local.env.yaml</c> must not have it silently replaced by
    /// the next rescan. That guard applies whole: an environment whose file already exists is
    /// skipped entirely, so a secret found for it is never even attempted.
    /// </remarks>
    /// <param name="secrets">
    /// Where a Postman-derived secret variable's value is stored. SEC-03: the value never reaches
    /// <paramref name="folder"/> — only the variable's name does, as an
    /// <see cref="EnvironmentDefinition.LocalNames"/> entry. A store that refuses writes (the CLI's,
    /// deliberately, off a build agent) is reported in the result rather than left to throw through
    /// a build.
    /// </param>
    public static async Task<EnvironmentWriteReport> WriteEnvironments(
        string folder, ScanResult result, ISecretStore secrets, CancellationToken ct = default)
    {
        if (result.Environments.Count == 0)
        {
            return new EnvironmentWriteReport(0, 0, []);
        }

        var environmentsFolder = Path.Combine(folder, CollectionFormat.EnvironmentsFolder);
        Directory.CreateDirectory(environmentsFolder);

        var serializer = new CollectionSerializer();

        var environmentsWritten = 0;
        var secretsStored = 0;
        var secretFailures = new List<SecretWriteFailure>();

        foreach (var environment in result.Environments)
        {
            var path = Path.Combine(
                environmentsFolder,
                $"{environment.Name}{CollectionFormat.EnvironmentFileExtension}");

            if (File.Exists(path))
            {
                continue;
            }

            var definition = new EnvironmentDefinition { Name = environment.Name };
            definition.Shared["baseUrl"] = environment.BaseUrl;

            foreach (var (key, value) in environment.Variables ?? ImmutableEmpty)
            {
                definition.Shared.TryAdd(key, value);
            }

            foreach (var (key, value) in environment.SecretVariables ?? ImmutableEmpty)
            {
                definition.LocalNames.Add(key);

                try
                {
                    await secrets.SetAsync(definition.SecretKeyFor(key), value, ct).ConfigureAwait(false);
                    secretsStored++;
                }
                catch (NotSupportedException ex)
                {
                    // The CLI's store refuses on principle — a build agent cannot unlock a user's
                    // credential store. The variable's name still round-trips; only its value is
                    // missing, same as any other unfilled local variable.
                    secretFailures.Add(new SecretWriteFailure(environment.Name, key, ex.Message));
                }
            }

            File.WriteAllText(path, serializer.SerializeEnvironment(definition));
            environmentsWritten++;
        }

        return new EnvironmentWriteReport(environmentsWritten, secretsStored, secretFailures);
    }

    private static readonly IReadOnlyDictionary<string, string> ImmutableEmpty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Writes <c>collection.yaml</c> if it does not exist yet. Never overwritten afterward: it is
    /// where the user's own variables, headers and settings live once they start editing it.
    /// </summary>
    /// <param name="sourcePath">
    /// The folder or <c>.sln</c> this collection was scanned from, recorded as <c>scannedFrom</c> so
    /// the app can re-derive environments from the same <c>launchSettings.json</c>/<c>appsettings.*.json</c>
    /// long after the import dialog closed — e.g. when creating another environment later. Null for
    /// a collection that did not come from a scan.
    /// </param>
    public static void WriteCollectionDefinition(string folder, string name, string? sourcePath = null)
    {
        var path = Path.Combine(folder, CollectionFormat.CollectionFileName);
        if (File.Exists(path))
        {
            return;
        }

        var definition = new CollectionDefinition { Name = name };

        if (sourcePath is not null)
        {
            definition.ScannedFrom = new ScanSource(sourcePath, DateTimeOffset.UtcNow);
        }

        var serializer = new CollectionSerializer();
        File.WriteAllText(path, serializer.SerializeCollection(definition));
    }

    private static void WriteCache(string folder, ScanResult result) =>
        File.WriteAllText(
            Path.Combine(folder, CollectionFormat.ScanCacheFileName),
            JsonSerializer.Serialize(
                new ScanCache(result.FileHashes, result.Endpoints),
                new JsonSerializerOptions { WriteIndented = false }));

    /// <summary>
    /// Reads the incremental-rescan cache, or null on a first scan, a missing folder, or a cache
    /// that failed to parse. A stale or hand-edited cache costs a full rescan, which is correct
    /// rather than fatal.
    /// </summary>
    public static ScanCache? ReadCache(string folder)
    {
        var path = Path.Combine(folder, CollectionFormat.ScanCacheFileName);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ScanCache>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record ScanCache(
    IReadOnlyDictionary<string, string> Hashes,
    IReadOnlyList<ScannedEndpoint> Endpoints);

/// <summary>What <see cref="CollectionWriter.WriteEnvironments"/> actually did — counts and failures
/// only, never a secret value, so it's safe to print to a console or fold into a status line.</summary>
public sealed record EnvironmentWriteReport(
    int EnvironmentsWritten,
    int SecretsStored,
    IReadOnlyList<SecretWriteFailure> SecretFailures);

/// <param name="Reason">The store's own message — already actionable, e.g. which environment
/// variable to set on the pipeline instead.</param>
public sealed record SecretWriteFailure(string Environment, string VariableName, string Reason);
