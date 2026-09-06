using System.Diagnostics;

namespace Courier.Core.Http;

/// <summary>
/// Injects a W3C <c>traceparent</c> on outbound requests, configurable per collection. TEL-01.
/// </summary>
/// <remarks>
/// Built on <see cref="ActivitySource"/> so the header follows the W3C format correctly rather than
/// being hand-assembled — the trace id has to survive a round trip through Application Insights or
/// Datadog, and a malformed one fails silently at the far end, which is the worst way to fail.
/// </remarks>
public sealed class TraceInjector : IDisposable
{
    public const string SourceName = "Courier";
    private const string TraceParentHeader = "traceparent";
    private const string TraceStateHeader = "tracestate";

    private readonly ActivitySource _source = new(SourceName);
    private readonly ActivityListener _listener;

    public TraceInjector()
    {
        // Without a listener that samples, ActivitySource.StartActivity returns null and no ids are
        // generated at all. Courier is the root of these traces, so it always samples its own.
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>
    /// Adds the header when enabled and returns the trace id, or null when the collection has trace
    /// injection switched off. The returned id is what the inspector shows and what the telemetry
    /// bridge searches on.
    /// </summary>
    public string? Inject(HttpRequestMessage message, bool enabled)
    {
        if (!enabled)
        {
            return null;
        }

        using var activity = _source.StartActivity("courier.request", ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        var traceParent = $"00-{activity.TraceId}-{activity.SpanId}-01";
        message.Headers.Remove(TraceParentHeader);
        message.Headers.TryAddWithoutValidation(TraceParentHeader, traceParent);

        if (!string.IsNullOrEmpty(activity.TraceStateString))
        {
            message.Headers.TryAddWithoutValidation(TraceStateHeader, activity.TraceStateString);
        }

        return activity.TraceId.ToString();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }
}
