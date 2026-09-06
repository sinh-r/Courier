using System.Security.Cryptography.X509Certificates;

namespace Courier.Core.Abstractions;

/// <summary>
/// Server certificate validation against the OS trust store (ENT-09) and the recorded per-host
/// exception list (ENT-11).
/// </summary>
/// <remarks>
/// There is deliberately no method to disable verification globally. ENT-11 permits a per-host
/// exception, recorded with a timestamp and visible in settings, and nothing wider than that.
/// </remarks>
public interface ITrustStore
{
    /// <summary>
    /// Validates a chain. On failure the result carries per-element status so the error message can
    /// say which certificate failed and why, which is the whole point of ENT-11.
    /// </summary>
    TrustDecision Validate(string host, X509Certificate2? certificate, X509Chain? chain, bool platformSaysValid);

    ValueTask<IReadOnlyList<HostTrustException>> ListExceptionsAsync(CancellationToken ct = default);

    /// <summary>Records an explicit, user-made exception for one host. Never wildcards.</summary>
    ValueTask AllowHostAsync(string host, string thumbprint, string reason, CancellationToken ct = default);

    ValueTask RevokeHostAsync(string host, CancellationToken ct = default);
}

public sealed record TrustDecision(bool IsTrusted, string Source, IReadOnlyList<ChainElementProblem> Problems)
{
    public static TrustDecision Trusted(string source) => new(true, source, []);
}

/// <param name="Subject">Which certificate in the chain.</param>
/// <param name="Status">The raw platform status flag name.</param>
/// <param name="Explanation">Plain, active, specific. UI_SPEC 3.7.</param>
public sealed record ChainElementProblem(string Subject, string Thumbprint, string Status, string Explanation);

public sealed record HostTrustException(string Host, string Thumbprint, string Reason, DateTimeOffset RecordedUtc);
