using System.Security.Cryptography.X509Certificates;

namespace Courier.Core.Abstractions;

/// <summary>
/// Client certificates for mutual TLS. ENT-08: selected from the OS certificate store per host
/// or per collection, including smart-card-backed certificates.
/// </summary>
public interface ICertificateSource
{
    /// <summary>
    /// Certificates that could be presented to a server, with provenance so the trust settings
    /// page can state the source of every row (UI_SPEC 5.7).
    /// </summary>
    ValueTask<IReadOnlyList<CertificateCandidate>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves a certificate by thumbprint. Returns null rather than throwing when a certificate
    /// has been removed from the store since it was selected, so the caller can report it plainly.
    /// </summary>
    ValueTask<X509Certificate2?> ResolveAsync(string thumbprint, CancellationToken ct = default);

    /// <summary>Loads a certificate from a .pfx file. Remains available alongside the store (ENT-08).</summary>
    ValueTask<X509Certificate2> LoadFromFileAsync(string path, string? password, CancellationToken ct = default);
}

/// <param name="Subject">Distinguished name, shown as-is.</param>
/// <param name="Thumbprint">SHA-1 thumbprint, the stable selection key.</param>
/// <param name="Source">Human-readable provenance, e.g. "from Windows certificate store".</param>
/// <param name="IsHardwareBacked">Smart-card or TPM backed; the private key is never exportable.</param>
public sealed record CertificateCandidate(
    string Subject,
    string Thumbprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Source,
    bool IsHardwareBacked)
{
    public bool IsExpired => DateTimeOffset.UtcNow > NotAfter;

    public bool IsNotYetValid => DateTimeOffset.UtcNow < NotBefore;
}
