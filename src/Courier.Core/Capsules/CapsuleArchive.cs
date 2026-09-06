using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Courier.Core.Http;
using Courier.Core.Variables;

namespace Courier.Core.Capsules;

/// <summary>
/// Reads and writes the capsule container. CAP-01, CAP-05, CAP-09.
/// </summary>
/// <remarks>
/// <b>A capsule is inert data.</b> CAP-09 is a security property, not a convenience: importing one
/// never executes a script, never sends a request, and never writes outside Courier's own storage.
/// Nothing in this class evaluates anything it reads, entry names are checked before any path is
/// derived from them, and there is no field in the format that could carry executable content.
/// </remarks>
public static class CapsuleArchive
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(
        Stream destination,
        CapsuleManifest manifest,
        IReadOnlyList<CapsuleStep> steps,
        RedactionReport report,
        CancellationToken ct = default)
    {
        manifest.StepCount = steps.Count;

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        await WriteEntryAsync(archive, CapsuleFormat.ManifestEntry, manifest, ct).ConfigureAwait(false);
        await WriteEntryAsync(archive, CapsuleFormat.RedactionsEntry, report, ct).ConfigureAwait(false);

        if (steps.Count == 1)
        {
            await WriteEntryAsync(archive, CapsuleFormat.RequestEntry, steps[0].Request, ct).ConfigureAwait(false);

            if (steps[0].Response is { } response)
            {
                await WriteEntryAsync(archive, CapsuleFormat.ResponseEntry, response, ct).ConfigureAwait(false);
            }
        }
        else
        {
            // A sequence. CAP-07: variables thread between steps, so ordinals matter and the
            // per-step folder keeps them obvious to a human reading the zip.
            foreach (var step in steps)
            {
                var folder = $"steps/{step.Ordinal:D2}";
                await WriteEntryAsync(archive, $"{folder}/request.json", step.Request, ct).ConfigureAwait(false);

                if (step.Response is { } response)
                {
                    await WriteEntryAsync(archive, $"{folder}/response.json", response, ct).ConfigureAwait(false);
                }

                await WriteEntryAsync(archive, $"{folder}/step.json", new
                {
                    step.Ordinal,
                    step.Name,
                    step.Exports,
                }, ct).ConfigureAwait(false);
            }
        }

        var readme = archive.CreateEntry(CapsuleFormat.ReadmeEntry, CompressionLevel.Optimal);
        await using var readmeStream = readme.Open();
        await readmeStream.WriteAsync(Encoding.UTF8.GetBytes(CapsuleFormat.Readme), ct).ConfigureAwait(false);
    }

    public static async Task<Capsule> ReadAsync(Stream source, CancellationToken ct = default)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);

        AssertNoPathTraversal(archive);

        var manifest = await ReadEntryAsync<CapsuleManifest>(archive, CapsuleFormat.ManifestEntry, ct)
            .ConfigureAwait(false)
            ?? throw new CapsuleFormatException("This file has no manifest, so it is not a capsule.");

        if (manifest.Version > CapsuleFormat.Version)
        {
            throw new CapsuleFormatException(
                $"This capsule is format version {manifest.Version} and this build reads version "
                + $"{CapsuleFormat.Version}. Update Courier, or unzip it and read the JSON directly.");
        }

        var report = await ReadEntryAsync<RedactionReport>(archive, CapsuleFormat.RedactionsEntry, ct)
            .ConfigureAwait(false)
            ?? new RedactionReport([]);

        var steps = new List<CapsuleStep>();

        var single = await ReadEntryAsync<CapsuleRequest>(archive, CapsuleFormat.RequestEntry, ct)
            .ConfigureAwait(false);

        if (single is not null)
        {
            steps.Add(new CapsuleStep
            {
                Ordinal = 1,
                Name = manifest.Title,
                Request = single,
                Response = await ReadEntryAsync<CapsuleResponse>(archive, CapsuleFormat.ResponseEntry, ct)
                    .ConfigureAwait(false),
            });
        }
        else
        {
            foreach (var entry in archive.Entries
                .Where(e => e.FullName.StartsWith("steps/", StringComparison.Ordinal)
                            && e.FullName.EndsWith("/request.json", StringComparison.Ordinal))
                .OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                var folder = entry.FullName[..entry.FullName.LastIndexOf('/')];
                var request = await ReadEntryAsync<CapsuleRequest>(archive, entry.FullName, ct).ConfigureAwait(false);

                if (request is null)
                {
                    continue;
                }

                var metadata = await ReadEntryAsync<StepMetadata>(archive, $"{folder}/step.json", ct)
                    .ConfigureAwait(false);

                steps.Add(new CapsuleStep
                {
                    Ordinal = metadata?.Ordinal ?? steps.Count + 1,
                    Name = metadata?.Name ?? $"Step {steps.Count + 1}",
                    Request = request,
                    Response = await ReadEntryAsync<CapsuleResponse>(archive, $"{folder}/response.json", ct)
                        .ConfigureAwait(false),
                    Exports = metadata?.Exports ?? [],
                });
            }
        }

        if (steps.Count == 0)
        {
            throw new CapsuleFormatException("This capsule contains no request.");
        }

        return new Capsule(manifest, steps, report);
    }

    /// <summary>
    /// Refuses an archive whose entry names could escape a destination directory. Nothing in
    /// Courier extracts a capsule to disk, but CAP-09 is a promise about what importing can do, and
    /// a promise checked at the boundary survives a future refactor that adds extraction.
    /// </summary>
    private static void AssertNoPathTraversal(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Contains("..", StringComparison.Ordinal)
                || Path.IsPathRooted(entry.FullName)
                || entry.FullName.Contains(':', StringComparison.Ordinal))
            {
                throw new CapsuleFormatException(
                    $"This capsule contains an unsafe entry name ('{entry.FullName}') and was not opened.");
            }
        }
    }

    private static async Task WriteEntryAsync<T>(ZipArchive archive, string name, T value, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, Json, ct).ConfigureAwait(false);
    }

    private static async Task<T?> ReadEntryAsync<T>(ZipArchive archive, string name, CancellationToken ct)
        where T : class
    {
        var entry = archive.GetEntry(name);
        if (entry is null)
        {
            return null;
        }

        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false);
    }

    private sealed record StepMetadata(int Ordinal, string Name, Dictionary<string, string> Exports);
}

/// <summary>An imported capsule, before anything has been resolved against the local environment.</summary>
public sealed record Capsule(CapsuleManifest Manifest, IReadOnlyList<CapsuleStep> Steps, RedactionReport Redactions)
{
    /// <summary>
    /// Which placeholders resolve against the current environment and which do not. CAP-06 requires
    /// showing this before allowing a send, so the recipient knows what will actually go on the wire.
    /// </summary>
    public PlaceholderBinding Bind(VariableScopes scopes)
    {
        var resolved = new List<string>();
        var unresolved = new List<string>();

        foreach (var name in ReferencedPlaceholders())
        {
            var isBound =
                scopes.Request.ContainsKey(name)
                || scopes.Collection.ContainsKey(name)
                || scopes.Global.ContainsKey(name)
                || (scopes.Environment?.Shared.ContainsKey(name) ?? false)
                || (scopes.Environment?.LocalNames.Contains(name, StringComparer.Ordinal) ?? false);

            (isBound ? resolved : unresolved).Add(name);
        }

        return new PlaceholderBinding(resolved, unresolved);
    }

    /// <summary>Every distinct placeholder in the capsule, in first-seen order.</summary>
    public IReadOnlyList<string> ReferencedPlaceholders()
    {
        var seen = new List<string>();

        void Collect(string? text)
        {
            foreach (var reference in VariableResolver.FindReferences(text))
            {
                if (!seen.Contains(reference.Name, StringComparer.Ordinal))
                {
                    seen.Add(reference.Name);
                }
            }
        }

        foreach (var step in Steps)
        {
            Collect(step.Request.Url);
            Collect(step.Request.Body);

            foreach (var header in step.Request.Headers)
            {
                Collect(header.Value);
            }
        }

        return seen;
    }
}

/// <param name="Unresolved">Shown as "1 unresolved: {{secret.apiKey}}" on the landed capsule banner.</param>
public sealed record PlaceholderBinding(IReadOnlyList<string> Resolved, IReadOnlyList<string> Unresolved)
{
    public bool IsFullyBound => Unresolved.Count == 0;
}

public sealed class CapsuleFormatException(string message) : Exception(message);

/// <summary>Converts a live exchange into the capsule shape, before redaction.</summary>
public static class CapsuleProjection
{
    public static CapsuleStep FromExchange(ExchangeResult result, int ordinal = 1, string? name = null)
    {
        var request = new CapsuleRequest
        {
            Method = result.Request.Method,
            Url = result.Request.Url.ToString(),
            ContentType = result.Request.ContentType,
            Headers = [.. result.Request.Headers.Select(h => new CapsuleHeader(h.Key, h.Value))],
        };

        (request.Body, request.BodyIsBase64) = Encode(result.Request.Body);

        CapsuleResponse? response = null;
        if (result.Response is { } received)
        {
            response = new CapsuleResponse
            {
                Status = received.Status,
                ReasonPhrase = received.ReasonPhrase,
                ContentType = received.ContentType,
                ContentLength = received.ContentLength,
                ElapsedMilliseconds = result.Elapsed.TotalMilliseconds,
                Headers = [.. received.Headers.Select(h => new CapsuleHeader(h.Key, h.Value))],
            };

            (response.Body, response.BodyIsBase64) = Encode(received.Body);
        }
        else if (result.Failure is { } failure)
        {
            // "The request never left" is the whole reason for many capsules, so it travels.
            response = new CapsuleResponse
            {
                Status = 0,
                ReasonPhrase = failure.Kind.ToString(),
                TransportFailure = failure.Explanation,
                ElapsedMilliseconds = result.Elapsed.TotalMilliseconds,
            };
        }

        return new CapsuleStep
        {
            Ordinal = ordinal,
            Name = name ?? $"{result.Request.Method} {result.Request.Url.AbsolutePath}",
            Request = request,
            Response = response,
        };
    }

    private static (string? Body, bool IsBase64) Encode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return (null, false);
        }

        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), false);
        }
        catch (DecoderFallbackException)
        {
            // Binary payload. Base64 so it survives, flagged so nothing tries to redact inside it.
            return (Convert.ToBase64String(bytes), true);
        }
    }
}
