namespace Courier.Telemetry;

/// <summary>
/// A monitoring backend. TEL-02 and TEL-03: App Insights and Datadog behind the same interface.
/// </summary>
/// <remarks>
/// The interface exists because TEL-03 says "the same capability against Datadog, behind the same
/// interface". Getting that right means the UI never learns which backend it is talking to, and
/// adding a third is a class rather than a branch through the telemetry pane.
/// </remarks>
public interface ITelemetrySource
{
    /// <summary>Shown in the reconstruct dialog: "App Insights · orders-prod".</summary>
    string DisplayName { get; }

    /// <summary>The host this source will contact, so the egress policy can authorise it. P4.</summary>
    Uri Endpoint { get; }

    /// <summary>
    /// The server-side timeline for one trace: dependencies, SQL, exceptions. TEL-02.
    /// </summary>
    Task<ServerTimeline?> GetTimelineAsync(string traceId, CancellationToken ct = default);

    /// <summary>
    /// Finds requests to reconstruct. TEL-04: by trace id, or by URL, time window and status code.
    /// </summary>
    Task<IReadOnlyList<TelemetryRequest>> SearchAsync(TelemetryQuery query, CancellationToken ct = default);
}

/// <param name="TraceId">Exact match. When set, the other filters are ignored.</param>
/// <param name="UrlContains">Substring match on the request URL.</param>
public sealed record TelemetryQuery(
    string? TraceId = null,
    string? UrlContains = null,
    int? StatusCode = null,
    TimeSpan? Window = null,
    int Limit = 50)
{
    public TimeSpan EffectiveWindow => Window ?? TimeSpan.FromHours(24);
}

/// <param name="Dependencies">Outbound calls the server made: SQL, HTTP, queues.</param>
/// <param name="Exceptions">Server-side exceptions correlated to the same trace.</param>
public sealed record ServerTimeline(
    string TraceId,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    IReadOnlyList<TimelineSpan> Dependencies,
    IReadOnlyList<TimelineException> Exceptions)
{
    /// <summary>What the inspector's Trace section shows without expanding anything.</summary>
    public string Summary => $"{Dependencies.Count} deps · {Exceptions.Count} exception"
        + (Exceptions.Count == 1 ? string.Empty : "s");
}

/// <param name="Kind">"SQL", "HTTP", "Azure blob" — whatever the backend calls it.</param>
public sealed record TimelineSpan(
    string Name,
    string Kind,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    bool Succeeded,
    string? Detail);

public sealed record TimelineException(
    string Type,
    string Message,
    DateTimeOffset AtUtc,
    string? Method,
    string? StackTrace);

/// <summary>
/// A request found in telemetry, and how completely it can be rebuilt. TEL-04, TEL-05.
/// </summary>
/// <param name="Fidelity">
/// The honest part. A backend only holds what the application chose to log, and a reconstructed
/// request that silently omits its body is worse than one that says the body is not available.
/// </param>
public sealed record TelemetryRequest(
    string TraceId,
    DateTimeOffset AtUtc,
    string Method,
    string Url,
    int StatusCode,
    TimeSpan Duration,
    ReconstructionFidelity Fidelity,
    string Source)
{
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    public string? Body { get; init; }

    public string Path => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : Url;

    /// <summary>Glyph and label together, never colour alone. UI_SPEC 3.3.</summary>
    public string FidelityGlyph => Fidelity == ReconstructionFidelity.FullBody ? "●" : "◐";

    public string FidelityLabel => Fidelity == ReconstructionFidelity.FullBody
        ? "full request body"
        : "headers and route only";
}

public enum ReconstructionFidelity
{
    /// <summary>The body was logged and the request can be replayed exactly.</summary>
    FullBody,

    /// <summary>Route, method and headers only. Body fields arrive as placeholders to fill in.</summary>
    RouteAndHeaders,
}
