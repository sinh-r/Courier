using System.Security.Cryptography.X509Certificates;

namespace Courier.Core.Privacy;

/// <summary>
/// The resolved transport configuration for a request, and the key the handler cache is built on.
/// TECH_SPEC 3.1: handler selection is per-request, driven by the auth profile.
/// </summary>
/// <remarks>
/// This is a value type by design. Two requests with the same transport requirements share one
/// pooled handler; a request needing a different client certificate or integrated auth gets its
/// own. Adding a field here changes the cache key, which is exactly what should happen.
/// </remarks>
public sealed record HandlerProfile
{
    public static readonly HandlerProfile Default = new();

    /// <summary>Host this profile was resolved for, used to explain a TLS failure. ENT-11.</summary>
    public string? Host { get; init; }

    /// <summary>NTLM or Kerberos with the current Windows identity. ENT-07.</summary>
    public bool UseIntegratedAuth { get; init; }

    /// <summary>Client certificates for mutual TLS. ENT-08.</summary>
    public IReadOnlyList<X509Certificate2> ClientCertificates { get; init; } = [];

    /// <summary>Thumbprints, so the record stays value-comparable without comparing certificates.</summary>
    private string CertificateKey => ClientCertificates.Count == 0
        ? string.Empty
        : string.Join(',', ClientCertificates.Select(c => c.Thumbprint));

    public bool Equals(HandlerProfile? other) =>
        other is not null
        && Host == other.Host
        && UseIntegratedAuth == other.UseIntegratedAuth
        && CertificateKey == other.CertificateKey;

    public override int GetHashCode() => HashCode.Combine(Host, UseIntegratedAuth, CertificateKey);
}
