using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Courier.Core.Privacy;

namespace Courier.Telemetry.AppInsights;

/// <summary>
/// Application Insights, queried with KQL. TEL-02, TEL-04.
/// </summary>
/// <remarks>
/// <para>
/// Authenticated through the same Azure.Identity chain as request auth (TEL-06), and its transport
/// comes from <see cref="EgressGate"/> so the workspace appears in the network statement like any
/// other destination. A monitoring SDK opening its own sockets is exactly the kind of quiet
/// outbound traffic SEC-06 exists to make visible.
/// </para>
/// <para>
/// <b>Needs live validation.</b> The KQL below is written against the standard Application Insights
/// schema but has not been run against a real workspace from this machine.
/// </para>
/// </remarks>
public sealed class AppInsightsSource : ITelemetrySource
{
    private const string Initiator = nameof(AppInsightsSource);

    private readonly LogsQueryClient _client;
    private readonly string _workspaceId;

    /// <param name="workspaceId">The Log Analytics workspace backing the App Insights resource.</param>
    public AppInsightsSource(
        string workspaceId,
        string resourceName,
        TokenCredential credential,
        EgressGate gate,
        UserIntentEgressPolicy policy,
        Uri? endpoint = null)
    {
        _workspaceId = workspaceId;
        DisplayName = $"App Insights · {resourceName}";
        Endpoint = endpoint ?? new Uri("https://api.loganalytics.io");

        // P4: the user configured this backend, and that action is what makes it legitimate.
        policy.Register(Endpoint, EgressPurpose.TelemetryBackend);

        _client = new LogsQueryClient(
            Endpoint,
            credential,
            new LogsQueryClientOptions
            {
                Transport = new HttpClientTransport(
                    gate.CreateClient(EgressPurpose.TelemetryBackend, Initiator)),
            });
    }

    public string DisplayName { get; }

    public Uri Endpoint { get; }

    public async Task<ServerTimeline?> GetTimelineAsync(string traceId, CancellationToken ct = default)
    {
        // operation_Id is the App Insights name for the W3C trace id Courier injects (TEL-01),
        // which is what ties a request the user sent to the server's own view of it.
        const string Query = """
            let id = '{0}';
            let req = requests | where operation_Id == id | project timestamp, duration;
            let deps = dependencies
                | where operation_Id == id
                | project timestamp, name, type, duration, success, data;
            let exc = exceptions
                | where operation_Id == id
                | project timestamp, type, outerMessage, method, details;
            req | extend kind = 'request' | extend name = '', type = '', success = true, data = '', outerMessage = '', method = '', details = ''
            | union (deps | extend kind = 'dependency' | extend outerMessage = '', method = '', details = '')
            | union (exc | extend kind = 'exception' | extend name = '', duration = 0.0, success = false, data = '')
            | order by timestamp asc
            """;

        var response = await _client
            .QueryWorkspaceAsync(_workspaceId, string.Format(Query, Escape(traceId)), QueryTimeRange.All, cancellationToken: ct)
            .ConfigureAwait(false);

        var table = response.Value.Table;
        if (table.Rows.Count == 0)
        {
            return null;
        }

        var dependencies = new List<TimelineSpan>();
        var exceptions = new List<TimelineException>();
        DateTimeOffset started = default;
        var duration = TimeSpan.Zero;

        foreach (var row in table.Rows)
        {
            var kind = Text(row, "kind");
            var timestamp = row.GetDateTimeOffset("timestamp") ?? default;

            switch (kind)
            {
                case "request":
                    started = timestamp;
                    duration = TimeSpan.FromMilliseconds(Number(row, "duration"));
                    break;

                case "dependency":
                    dependencies.Add(new TimelineSpan(
                        Text(row, "name") ?? "(unnamed)",
                        Text(row, "type") ?? "dependency",
                        timestamp,
                        TimeSpan.FromMilliseconds(Number(row, "duration")),
                        row.GetBoolean("success") ?? true,
                        Text(row, "data")));
                    break;

                case "exception":
                    exceptions.Add(new TimelineException(
                        Text(row, "type") ?? "Exception",
                        Text(row, "outerMessage") ?? string.Empty,
                        timestamp,
                        Text(row, "method"),
                        Text(row, "details")));
                    break;
            }
        }

        return new ServerTimeline(traceId, started, duration, dependencies, exceptions);
    }

    public async Task<IReadOnlyList<TelemetryRequest>> SearchAsync(
        TelemetryQuery query,
        CancellationToken ct = default)
    {
        var filters = new List<string>();

        if (query.TraceId is { Length: > 0 } traceId)
        {
            filters.Add($"operation_Id == '{Escape(traceId)}'");
        }

        if (query.UrlContains is { Length: > 0 } url)
        {
            filters.Add($"url contains '{Escape(url)}'");
        }

        if (query.StatusCode is { } status)
        {
            filters.Add($"toint(resultCode) == {status}");
        }

        var where = filters.Count > 0 ? $"| where {string.Join(" and ", filters)}" : string.Empty;

        // The body is only present if the application logged it as a custom dimension. TEL-05
        // depends on knowing which, so it is selected explicitly rather than inferred later.
        var kql = $"""
            requests
            {where}
            | extend requestBody = tostring(customDimensions['RequestBody'])
            | extend requestHeaders = tostring(customDimensions['RequestHeaders'])
            | project timestamp, name, url, resultCode, duration, operation_Id, requestBody, requestHeaders
            | order by timestamp desc
            | take {query.Limit}
            """;

        var response = await _client
            .QueryWorkspaceAsync(_workspaceId, kql, new QueryTimeRange(query.EffectiveWindow), cancellationToken: ct)
            .ConfigureAwait(false);

        var results = new List<TelemetryRequest>();

        foreach (var row in response.Value.Table.Rows)
        {
            var body = Text(row, "requestBody");
            var name = Text(row, "name") ?? string.Empty;

            results.Add(new TelemetryRequest(
                Text(row, "operation_Id") ?? string.Empty,
                row.GetDateTimeOffset("timestamp") ?? default,
                MethodFrom(name),
                Text(row, "url") ?? string.Empty,
                int.TryParse(Text(row, "resultCode"), out var code) ? code : 0,
                TimeSpan.FromMilliseconds(Number(row, "duration")),
                string.IsNullOrEmpty(body) ? ReconstructionFidelity.RouteAndHeaders : ReconstructionFidelity.FullBody,
                string.IsNullOrEmpty(body) ? "request" : "request + custom event")
            {
                Body = body,
                Headers = ParseHeaders(Text(row, "requestHeaders")),
            });
        }

        return results;
    }

    /// <summary>App Insights names a request "POST /api/v2/orders", so the verb is the first word.</summary>
    private static string MethodFrom(string name)
    {
        var space = name.IndexOf(' ');
        return space > 0 ? name[..space].ToUpperInvariant() : "GET";
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ParseHeaders(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var headers = new List<KeyValuePair<string, string>>();

        foreach (var line in raw.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers.Add(new KeyValuePair<string, string>(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
        }

        return headers;
    }

    private static string? Text(LogsTableRow row, string column)
    {
        try
        {
            return row.GetString(column);
        }
        catch (ArgumentException)
        {
            // A column the union did not produce for this row shape. Absent, not an error.
            return null;
        }
    }

    private static double Number(LogsTableRow row, string column)
    {
        try
        {
            return row.GetDouble(column) ?? 0;
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Escapes a value for a KQL string literal. The inputs here are a trace id and a URL fragment
    /// the user typed, and neither should be able to change the shape of the query.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("'", "\\'", StringComparison.Ordinal);
}
