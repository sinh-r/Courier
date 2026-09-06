using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Courier.Core.Abstractions;

namespace Courier.Platform.Windows.Certificates;

/// <summary>
/// Server certificate validation against the Windows trust store, with actionable failures.
/// ENT-09, ENT-11.
/// </summary>
/// <remarks>
/// <para>
/// The chain is built explicitly rather than left to the platform default so corporate root CAs
/// installed by IT are trusted without configuration — which is the difference between Courier
/// working on a managed laptop and Courier being uninstalled on day one.
/// </para>
/// <para>
/// <b>There is deliberately no global "disable verification" toggle, and adding one is not an
/// enhancement.</b> ENT-11 permits a per-host exception, recorded with a timestamp and visible in
/// settings. A global switch is the thing every other client has and the thing that gets left on.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrustStore : ITrustStore
{
    private readonly ConcurrentDictionary<string, HostTrustException> _exceptions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<IReadOnlyList<HostTrustException>>? _load;
    private readonly Action<HostTrustException>? _persist;
    private readonly Action<string>? _forget;

    /// <param name="load">Restores recorded exceptions from the database. Null in tests.</param>
    public WindowsTrustStore(
        Func<IReadOnlyList<HostTrustException>>? load = null,
        Action<HostTrustException>? persist = null,
        Action<string>? forget = null)
    {
        _load = load;
        _persist = persist;
        _forget = forget;

        foreach (var exception in _load?.Invoke() ?? [])
        {
            _exceptions[exception.Host] = exception;
        }
    }

    public TrustDecision Validate(
        string host,
        X509Certificate2? certificate,
        X509Chain? chain,
        bool platformSaysValid)
    {
        if (platformSaysValid)
        {
            return TrustDecision.Trusted("from Windows certificate store");
        }

        if (certificate is null)
        {
            return new TrustDecision(false, "no certificate", [
                new ChainElementProblem(
                    host,
                    string.Empty,
                    "NoCertificate",
                    $"{host} did not present a certificate at all."),
            ]);
        }

        var problems = Describe(host, certificate, chain);

        // An exception the user recorded for this exact certificate on this exact host. Both must
        // match: a re-issued certificate revokes the exception, which is the correct behaviour and
        // the reason the thumbprint is stored alongside the host.
        if (_exceptions.TryGetValue(host, out var recorded)
            && string.Equals(recorded.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return new TrustDecision(
                true,
                $"allowed for this host only · set by you · {recorded.RecordedUtc:yyyy-MM-dd}",
                problems);
        }

        return new TrustDecision(false, "not trusted by Windows", problems);
    }

    /// <summary>
    /// Turns per-element chain status into sentences. ENT-11's whole value is here: "which
    /// certificate in the chain failed and why", not "an error occurred".
    /// </summary>
    private static IReadOnlyList<ChainElementProblem> Describe(
        string host,
        X509Certificate2 certificate,
        X509Chain? chain)
    {
        var problems = new List<ChainElementProblem>();

        // Rebuild against the Windows store explicitly so an IT-installed root CA is honoured.
        using var rebuilt = new X509Chain
        {
            ChainPolicy =
            {
                RevocationMode = X509RevocationMode.Online,
                RevocationFlag = X509RevocationFlag.ExcludeRoot,
                UrlRetrievalTimeout = TimeSpan.FromSeconds(5),
                TrustMode = X509ChainTrustMode.System,
                VerificationFlags = X509VerificationFlags.NoFlag,
            },
        };

        rebuilt.Build(certificate);
        var source = rebuilt.ChainElements.Count > 0 ? rebuilt : chain;

        if (source is null)
        {
            problems.Add(new ChainElementProblem(
                certificate.Subject,
                certificate.Thumbprint,
                "ChainUnavailable",
                $"Windows could not build a chain for the certificate {host} presented."));

            return problems;
        }

        foreach (var element in source.ChainElements)
        {
            foreach (var status in element.ChainElementStatus)
            {
                if (status.Status == X509ChainStatusFlags.NoError)
                {
                    continue;
                }

                problems.Add(new ChainElementProblem(
                    element.Certificate.Subject,
                    element.Certificate.Thumbprint,
                    status.Status.ToString(),
                    Explain(status.Status, element.Certificate, host)));
            }
        }

        if (problems.Count == 0)
        {
            problems.Add(new ChainElementProblem(
                certificate.Subject,
                certificate.Thumbprint,
                "NameMismatch",
                $"The certificate is valid but was not issued for {host}. It names "
                + $"{certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false)}."));
        }

        return problems;
    }

    private static string Explain(X509ChainStatusFlags status, X509Certificate2 certificate, string host) => status switch
    {
        X509ChainStatusFlags.UntrustedRoot =>
            $"The root '{Short(certificate.Subject)}' is not in this machine's trusted roots. "
            + "If this is your corporate proxy, add its root CA to the machine store, or allow it for this host only.",

        X509ChainStatusFlags.PartialChain =>
            $"The server did not send enough of the chain to reach a trusted root. '{Short(certificate.Subject)}' has no issuer here.",

        X509ChainStatusFlags.NotTimeValid =>
            $"'{Short(certificate.Subject)}' expired on {certificate.NotAfter:yyyy-MM-dd}. "
            + "Check this machine's clock before assuming the server is at fault.",

        X509ChainStatusFlags.Revoked =>
            $"'{Short(certificate.Subject)}' has been revoked by its issuer. Do not allow this host.",

        X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation =>
            $"Revocation for '{Short(certificate.Subject)}' could not be checked. "
            + "This is normal on an air-gapped machine and suspicious on a connected one.",

        X509ChainStatusFlags.NotValidForUsage =>
            $"'{Short(certificate.Subject)}' is not permitted to be used for server authentication.",

        X509ChainStatusFlags.CtlNotTimeValid or X509ChainStatusFlags.CtlNotSignatureValid =>
            $"The certificate trust list covering '{Short(certificate.Subject)}' is not valid.",

        _ => $"'{Short(certificate.Subject)}' failed validation: {status}.",
    };

    private static string Short(string distinguishedName)
    {
        var cn = distinguishedName
            .Split(',', StringSplitOptions.TrimEntries)
            .FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));

        return cn ?? distinguishedName;
    }

    public ValueTask<IReadOnlyList<HostTrustException>> ListExceptionsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HostTrustException>>([.. _exceptions.Values.OrderBy(e => e.Host)]);

    public ValueTask AllowHostAsync(string host, string thumbprint, string reason, CancellationToken ct = default)
    {
        var exception = new HostTrustException(host, thumbprint, reason, DateTimeOffset.UtcNow);
        _exceptions[host] = exception;
        _persist?.Invoke(exception);
        return ValueTask.CompletedTask;
    }

    public ValueTask RevokeHostAsync(string host, CancellationToken ct = default)
    {
        _exceptions.TryRemove(host, out _);
        _forget?.Invoke(host);
        return ValueTask.CompletedTask;
    }
}
