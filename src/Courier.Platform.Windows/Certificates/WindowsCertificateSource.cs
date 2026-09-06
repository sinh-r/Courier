using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Courier.Core.Abstractions;

namespace Courier.Platform.Windows.Certificates;

/// <summary>
/// Client certificates from the Windows certificate store. ENT-08.
/// </summary>
/// <remarks>
/// Smart-card and TPM-backed certificates work through CNG with no extra handling, on one
/// condition: nothing may try to export the private key. Nothing here does — the certificate is
/// handed to the TLS stack, which asks the key to sign without ever seeing it. Calling
/// <c>Export</c> with a private key would throw on a smart card and silently weaken security
/// everywhere else, so it does not appear in this file.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsCertificateSource : ICertificateSource
{
    private const string StoreSource = "from Windows certificate store";
    private const string FileSource = "from a .pfx file you chose";

    public ValueTask<IReadOnlyList<CertificateCandidate>> ListAsync(CancellationToken ct = default)
    {
        var candidates = new List<CertificateCandidate>();

        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);

            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException)
            {
                // LocalMachine\My is often unreadable without elevation. NFR-02 says Courier never
                // requires admin rights, so an unreadable store is skipped, not escalated.
                continue;
            }

            foreach (var certificate in store.Certificates)
            {
                // Only certificates that can actually be presented for client auth.
                if (!certificate.HasPrivateKey || !IsClientAuthCapable(certificate))
                {
                    continue;
                }

                candidates.Add(new CertificateCandidate(
                    certificate.Subject,
                    certificate.Thumbprint,
                    certificate.NotBefore,
                    certificate.NotAfter,
                    location == StoreLocation.CurrentUser ? StoreSource : $"{StoreSource} (machine)",
                    IsHardwareBacked(certificate)));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<CertificateCandidate>>(
            [.. candidates.OrderByDescending(c => c.NotAfter)]);
    }

    public ValueTask<X509Certificate2?> ResolveAsync(string thumbprint, CancellationToken ct = default)
    {
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);

            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException)
            {
                continue;
            }

            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (found.Count > 0)
            {
                return ValueTask.FromResult<X509Certificate2?>(found[0]);
            }
        }

        // Removed from the store since it was selected. The caller reports that plainly rather than
        // sending the request without the certificate and letting the server give a confusing 403.
        return ValueTask.FromResult<X509Certificate2?>(null);
    }

    public ValueTask<X509Certificate2> LoadFromFileAsync(string path, string? password, CancellationToken ct = default)
    {
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            path,
            password,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);

        return ValueTask.FromResult(certificate);
    }

    /// <summary>
    /// True when the Enhanced Key Usage extension permits client authentication, or there is no
    /// EKU at all (which historically means "any purpose").
    /// </summary>
    private static bool IsClientAuthCapable(X509Certificate2 certificate)
    {
        const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

        var ekus = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToList();
        if (ekus.Count == 0)
        {
            return true;
        }

        return ekus.Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ClientAuthenticationOid));
    }

    /// <summary>
    /// Smart-card or TPM backing, detected by the key's provider. Shown in the picker because a
    /// hardware-backed certificate behaves differently — it may prompt for a PIN mid-request.
    /// </summary>
    private static bool IsHardwareBacked(X509Certificate2 certificate)
    {
        try
        {
            using var rsa = certificate.GetRSAPrivateKey();
            if (rsa is RSACng rsaCng)
            {
                return IsHardwareProvider(rsaCng.Key.Provider?.Provider);
            }

            using var ecdsa = certificate.GetECDsaPrivateKey();
            if (ecdsa is ECDsaCng ecdsaCng)
            {
                return IsHardwareProvider(ecdsaCng.Key.Provider?.Provider);
            }
        }
        catch (CryptographicException)
        {
            // A card that is not currently inserted throws here. Treat it as hardware-backed,
            // which is both true and the safer assumption.
            return true;
        }

        return false;
    }

    private static bool IsHardwareProvider(string? provider) =>
        provider is not null
        && (provider.Contains("Smart Card", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("Platform Crypto", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("TPM", StringComparison.OrdinalIgnoreCase));
}
