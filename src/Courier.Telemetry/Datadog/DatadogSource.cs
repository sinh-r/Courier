using System.Net.Http.Json;
using System.Text.Json;
using Courier.Core.Abstractions;
using Courier.Core.Privacy;

namespace Courier.Telemetry.Datadog;

/// <summary>
/// Datadog, behind the same interface as App Insights. TEL-03.
/// </summary>
/// <remarks>
/// <para>
/// A plain REST client, as TECH_SPEC 3.8 specifies. The API keys come from the credential store
/// through <see cref="ISecretStore"/>, never from a collection file — TEL-06 is explicit that a
/// monitoring key must not end up somewhere that gets committed.
/// </para>
/// <para>
/// <b>Needs live validation.</b> The request shapes follow Datadog's documented v2 spans API but
/// have not been exercised against a live account from this machine.
/// </para>
/// </remarks>
public sealed class DatadogSource : ITelemetrySource
{
    private const string Initiator = nameof(DatadogSource);

    private readonly EgressGate _gate;
    private readonly ISecretStore _secrets;
    private readonly string _site;

    /// <param name="site">"datadoghq.com", "datadoghq.eu", and so on.</param>
    public DatadogSource(
        string site,
        string serviceName,
        EgressGate gate,
        UserIntentEgressPolicy policy,
        ISecretStore secrets)
    {
        _gate = gate;
        _secrets = secrets;
        _site = site;

        DisplayName = $"Datadog · {serviceName}";
        ServiceName = serviceName;
        Endpoint = new Uri($"https://api.{site}");

        policy.Register(Endpoint, EgressPurpose.TelemetryBackend);
    }

    public string DisplayName { get; }

    public string ServiceName { get; }

    public Uri Endpoint { get; }

    /// <summary>Credential store keys. The values never appear in a file Courier writes. TEL-06.</summary>
    public SecretKey ApiKeyKey => new(SecretKey.TelemetryScope, $"datadog/{_site}/api-key");

    public SecretKey ApplicationKeyKey => new(SecretKey.TelemetryScope, $"datadog/{_site}/app-key");

    public async Task<ServerTimeline?> GetTimelineAsync(string traceId, CancellationToken ct = default)
    {
        var spans = await QuerySpansAsync($"@trace_id:{traceId}", TimeSpan.FromDays(1), 200, ct)
            .ConfigureAwait(false);

        if (spans.Count == 0)
        {
            return null;
        }

        var root = spans.OrderBy(s => s.Start).First();
        var dependencies = new List<TimelineSpan>();
        var exceptions = new List<TimelineException>();

        foreach (var span in spans.Skip(1))
        {
            if (span.ErrorType is { Length: > 0 })
            {
                exceptions.Add(new TimelineException(
                    span.ErrorType,
                    span.ErrorMessage ?? string.Empty,
                    span.Start,
                    span.Resource,
                    span.ErrorStack));
            }

            dependencies.Add(new TimelineSpan(
                span.Resource ?? span.Name,
                span.Type ?? "span",
                span.Start,
                span.Duration,
                span.ErrorType is null,
                span.Resource));
        }

        return new ServerTimeline(traceId, root.Start, root.Duration, dependencies, exceptions);
    }

    public async Task<IReadOnlyList<TelemetryRequest>> SearchAsync(
        TelemetryQuery query,
        CancellationToken ct = default)
    {
        var filters = new List<string> { $"service:{ServiceName}" };

        if (query.TraceId is { Length: > 0 } traceId)
        {
            filters.Add($"@trace_id:{traceId}");
        }

        if (query.UrlContains is { Length: > 0 } url)
        {
            filters.Add($"@http.url:*{url}*");
        }

        if (query.StatusCode is { } status)
        {
            filters.Add($"@http.status_code:{status}");
        }

        var spans = await QuerySpansAsync(string.Join(' ', filters), query.EffectiveWindow, query.Limit, ct)
            .ConfigureAwait(false);

        return
        [
            .. spans.Select(span => new TelemetryRequest(
                span.TraceId ?? string.Empty,
                span.Start,
                span.Method ?? "GET",
                span.Url ?? string.Empty,
                span.StatusCode ?? 0,
                span.Duration,

                // TEL-05: Datadog holds a body only if the application tagged it, which most do not.
                span.Body is { Length: > 0 } ? ReconstructionFidelity.FullBody : ReconstructionFidelity.RouteAndHeaders,
                span.Body is { Length: > 0 } ? "span + http.request.body tag" : "span")
            {
                Body = span.Body,
            }),
        ];
    }

    private async Task<IReadOnlyList<DatadogSpan>> QuerySpansAsync(
        string filter,
        TimeSpan window,
        int limit,
        CancellationToken ct)
    {
        var apiKey = await _secrets.GetAsync(ApiKeyKey, ct).ConfigureAwait(false);
        var appKey = await _secrets.GetAsync(ApplicationKeyKey, ct).ConfigureAwait(false);

        if (apiKey is null || appKey is null)
        {
            throw new InvalidOperationException(
                "Datadog needs an API key and an application key. Add them in the telemetry settings; "
                + "they go to the credential store, never to a collection file.");
        }

        using var client = _gate.CreateClient(EgressPurpose.TelemetryBackend, Initiator);
        client.BaseAddress = Endpoint;
        client.DefaultRequestHeaders.Add("DD-API-KEY", apiKey);
        client.DefaultRequestHeaders.Add("DD-APPLICATION-KEY", appKey);

        var request = new
        {
            data = new
            {
                type = "search_request",
                attributes = new
                {
                    filter = new
                    {
                        query = filter,
                        from = $"now-{(int)window.TotalSeconds}s",
                        to = "now",
                    },
                    page = new { limit },
                    sort = "-timestamp",
                },
            },
        };

        using var response = await client
            .PostAsJsonAsync("/api/v2/spans/events/search", request, ct)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        using var document = await JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);

        var spans = new List<DatadogSpan>();

        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return spans;
        }

        foreach (var element in data.EnumerateArray())
        {
            if (element.TryGetProperty("attributes", out var attributes))
            {
                spans.Add(ReadSpan(attributes));
            }
        }

        return spans;
    }

    private static DatadogSpan ReadSpan(JsonElement attributes)
    {
        var custom = attributes.TryGetProperty("custom", out var c) ? c : default;

        return new DatadogSpan
        {
            TraceId = Text(attributes, "trace_id"),
            Name = Text(attributes, "name") ?? "span",
            Resource = Text(attributes, "resource_name"),
            Type = Text(attributes, "type"),
            Start = attributes.TryGetProperty("start_timestamp", out var start)
                && start.TryGetDateTimeOffset(out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue,
            Duration = TimeSpan.FromMilliseconds(
                attributes.TryGetProperty("duration", out var duration) ? duration.GetDouble() / 1_000_000 : 0),
            Method = Nested(custom, "http", "method"),
            Url = Nested(custom, "http", "url"),
            StatusCode = int.TryParse(Nested(custom, "http", "status_code"), out var status) ? status : null,
            Body = Nested(custom, "http", "request_body"),
            ErrorType = Nested(custom, "error", "type"),
            ErrorMessage = Nested(custom, "error", "message"),
            ErrorStack = Nested(custom, "error", "stack"),
        };
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? value.ToString()
            : null;

    private static string? Nested(JsonElement element, string group, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(group, out var nested)
            ? Text(nested, property)
            : null;

    private sealed record DatadogSpan
    {
        public string? TraceId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string? Resource { get; init; }

        public string? Type { get; init; }

        public DateTimeOffset Start { get; init; }

        public TimeSpan Duration { get; init; }

        public string? Method { get; init; }

        public string? Url { get; init; }

        public int? StatusCode { get; init; }

        public string? Body { get; init; }

        public string? ErrorType { get; init; }

        public string? ErrorMessage { get; init; }

        public string? ErrorStack { get; init; }
    }
}
