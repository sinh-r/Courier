using Courier.Core.Collections;
using Courier.Core.Privacy;

namespace Courier.Core.Http;

/// <summary>
/// A request after variable substitution and auth, ready to go on the wire.
/// </summary>
/// <remarks>
/// Keeping this separate from <see cref="RequestDefinition"/> matters: the definition is what the
/// user edits and what is written to a file, and it still contains <c>{{variable}}</c> references
/// and no credentials. This is the resolved form, held only in memory, and it is what the capsule
/// exporter redacts before anything is written anywhere.
/// </remarks>
public sealed record PreparedRequest
{
    public required string Method { get; init; }

    public required Uri Url { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    public byte[]? BodyBytes { get; init; }

    public string? ContentType { get; init; }

    public RequestSettings Settings { get; init; } = new();

    public HandlerProfile HandlerProfile { get; init; } = HandlerProfile.Default;

    /// <summary>Scopes the cookie jar. Cookies are per-environment. CORE-05.</summary>
    public string? EnvironmentName { get; init; }

    /// <summary>Whether to add a W3C traceparent. Per-collection. TEL-01.</summary>
    public bool InjectTraceParent { get; init; }

    /// <summary>
    /// Variables that were referenced but had no value. The send is still allowed, because a URL
    /// with a visible unresolved token is more useful than a refusal, but the caller warns first.
    /// </summary>
    public IReadOnlyList<string> UnboundVariables { get; init; } = [];

    /// <summary>
    /// Values known to be secret, registered with the log redactor before the send so they cannot
    /// appear in any log line this request produces. SEC-07.
    /// </summary>
    public IReadOnlyList<string> SecretValues { get; init; } = [];
}
