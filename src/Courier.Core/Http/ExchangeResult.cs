using System.Net;

namespace Courier.Core.Http;

/// <summary>
/// What came back, or why nothing did.
/// </summary>
/// <remarks>
/// The two failure modes are kept apart deliberately. UI_SPEC 3.3: "the request never left" is a
/// different kind of problem from "the server said no", and it gets a distinct glyph rather than
/// being folded into an error status. Collapsing them is the thing that makes a client feel like it
/// is lying to you at 2am.
/// </remarks>
public sealed record ExchangeResult
{
    public required ExchangeOutcome Outcome { get; init; }

    public required SentRequest Request { get; init; }

    public ReceivedResponse? Response { get; init; }

    /// <summary>Populated only when <see cref="Outcome"/> is <see cref="ExchangeOutcome.TransportFailure"/>.</summary>
    public TransportFailure? Failure { get; init; }

    /// <summary>Wall-clock time from send to last byte, or to failure.</summary>
    public required TimeSpan Elapsed { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>W3C trace id injected on this request, when the collection asked for one. TEL-01.</summary>
    public string? TraceId { get; init; }

    /// <summary>Intermediate responses when redirects were followed. CORE-11.</summary>
    public IReadOnlyList<RedirectHop> Redirects { get; init; } = [];

    /// <summary>Attempts made before this result, when retries were configured.</summary>
    public int Attempts { get; init; } = 1;

    public bool Succeeded => Outcome == ExchangeOutcome.Completed;
}

public enum ExchangeOutcome
{
    /// <summary>A response arrived. It may still be a 500; that is the server's answer, not a failure.</summary>
    Completed,

    /// <summary>The request never left, or no response came back. DNS, TLS, proxy, connection reset.</summary>
    TransportFailure,

    /// <summary>The user pressed Cancel, or the per-request timeout elapsed.</summary>
    Cancelled,

    /// <summary>Courier refused to open the connection. SEC-01. Should never be user-caused.</summary>
    Refused,
}

/// <param name="Body">Exactly what was sent, after variable substitution, for the capsule.</param>
public sealed record SentRequest(
    string Method,
    Uri Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[]? Body,
    string? ContentType,
    Version HttpVersion);

/// <param name="BodyPath">
/// Set instead of <paramref name="Body"/> when the payload was too large to hold in memory and was
/// spilled to the response cache. PERF-05.
/// </param>
public sealed record ReceivedResponse(
    HttpStatusCode StatusCode,
    string ReasonPhrase,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[]? Body,
    string? BodyPath,
    long ContentLength,
    string? ContentType,
    Version HttpVersion)
{
    public int Status => (int)StatusCode;

    /// <summary>2xx, 3xx, 4xx, 5xx. Drives the status colour, which is the only colour in that row.</summary>
    public int StatusClass => Status / 100;
}

/// <param name="Kind">Coarse category, so the message can be specific without parsing exception text.</param>
/// <param name="Explanation">Plain, active, specific. Says what happened and what to do. UI_SPEC 3.7.</param>
public sealed record TransportFailure(
    TransportFailureKind Kind,
    string Explanation,
    string? Detail = null,
    IReadOnlyList<string>? ChainProblems = null);

public enum TransportFailureKind
{
    NameResolution,
    ConnectionRefused,
    ConnectionReset,
    Timeout,
    TlsHandshake,
    /// <summary>A certificate in the chain was not trusted. ENT-11 requires naming which and why.</summary>
    CertificateNotTrusted,
    ProxyAuthenticationRequired,
    ProxyUnreachable,
    ClientCertificateRejected,
    Unknown,
}

public sealed record RedirectHop(int Status, Uri From, Uri To);
