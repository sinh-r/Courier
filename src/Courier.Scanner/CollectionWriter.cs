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
