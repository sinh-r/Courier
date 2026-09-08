using System.Text.Json;
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
    /// <c>appsettings.*.json</c>, so <c>{{baseUrl}}</c> has something to resolve against. SCAN-07.
    /// </summary>
    /// <remarks>
    /// Never overwrites a file that already exists — same P3 reasoning as the overlay. A user who
    /// has started editing <c>environments/Local.env.yaml</c> must not have it silently replaced by
    /// the next rescan.
    /// </remarks>
    public static void WriteEnvironments(string folder, ScanResult result)
    {
        if (result.Environments.Count == 0)
        {
            return;
        }

        var environmentsFolder = Path.Combine(folder, CollectionFormat.EnvironmentsFolder);
        Directory.CreateDirectory(environmentsFolder);

        var serializer = new CollectionSerializer();

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

            File.WriteAllText(path, serializer.SerializeEnvironment(definition));
        }
    }

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
