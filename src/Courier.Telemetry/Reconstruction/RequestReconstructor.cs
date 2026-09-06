using Courier.Core.Collections;

namespace Courier.Telemetry.Reconstruction;

/// <summary>
/// Turns a telemetry record into a runnable request. TEL-04, TEL-05.
/// </summary>
/// <remarks>
/// The definition of done for Phase 5 is "paste an operation ID from an App Insights alert and get
/// a runnable request". The word doing the work is <i>runnable</i> — and the honest part of that is
/// what happens when the backend does not have the body. A reconstruction that quietly sends an
/// empty payload produces a different failure from the one being investigated, which is the worst
/// possible outcome for a tool someone reached for at 2am.
/// </remarks>
public static class RequestReconstructor
{
    /// <summary>
    /// Builds a request definition. Fields the backend could not supply become named placeholders
    /// rather than empty values, so the gap is visible before the send.
    /// </summary>
    public static ReconstructedRequest Rebuild(TelemetryRequest source)
    {
        var request = new RequestDefinition
        {
            Name = $"{source.Method} {source.Path}",
            Method = source.Method,
            Url = source.Url,
            Provenance = Provenance.Authored,
            Description =
                $"Reconstructed from {source.Source} at {source.AtUtc:yyyy-MM-dd HH:mm:ss} UTC. "
                + $"Trace {source.TraceId}.",
        };

        var missing = new List<string>();

        foreach (var header in source.Headers)
        {
            // A credential in telemetry is either redacted already or should be. Either way it is
            // not carried into the request: the user's own auth profile supplies it.
            if (Core.Privacy.SecretPatterns.Classify(header.Value, header.Key).IsSecret)
            {
                request.Headers.Add(new HeaderValue(header.Key, $"{{{{auth.{Slug(header.Key)}}}}}"));
                missing.Add(header.Key);
                continue;
            }

            request.Headers.Add(new HeaderValue(header.Key, header.Value));
        }

        if (source.Fidelity == ReconstructionFidelity.FullBody && source.Body is { Length: > 0 } body)
        {
            request.Body = new RequestBody
            {
                Kind = LooksLikeJson(body) ? BodyKind.Json : BodyKind.Text,
                Text = body,
            };
        }
        else if (RequiresBody(source.Method))
        {
            // Named, not blank. The user has to see that this is a hole they must fill.
            request.Body = new RequestBody
            {
                Kind = BodyKind.Json,
                Text = "{\n  \"_note\": \"{{body.notLogged}}\"\n}",
            };

            missing.Add("request body");

            request.UnresolvedNotes.Add(
                "The monitoring backend did not hold this request's body, so it could not be rebuilt. "
                + "Fill it in before sending, or the server will see a different request from the one "
                + "you are investigating.");
        }

        // The status the server actually returned becomes the assertion, so a successful
        // reconstruction reproduces the failure rather than quietly passing.
        request.Assertions.Add(new AssertionDefinition(
            AssertionKind.Status,
            Expected: source.StatusCode.ToString(),
            Source: AssertionSource.Authored,
            Note: $"the status this request produced at {source.AtUtc:HH:mm:ss}"));

        return new ReconstructedRequest(request, source.Fidelity, missing, source.TraceId);
    }

    private static bool RequiresBody(string method) =>
        method.ToUpperInvariant() is "POST" or "PUT" or "PATCH";

    private static bool LooksLikeJson(string body)
    {
        var trimmed = body.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static string Slug(string value) =>
        new([.. value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}

/// <param name="Missing">
/// Named fields the reconstruction could not supply. Shown before the send, not discovered after.
/// </param>
public sealed record ReconstructedRequest(
    RequestDefinition Request,
    ReconstructionFidelity Fidelity,
    IReadOnlyList<string> Missing,
    string TraceId)
{
    public bool IsComplete => Missing.Count == 0;

    /// <summary>The sentence the landing tab shows above a reconstructed request.</summary>
    public string Describe() => IsComplete
        ? $"Rebuilt in full from trace {TraceId}."
        : $"Rebuilt from trace {TraceId}. Not available in telemetry: {string.Join(", ", Missing)}.";
}
