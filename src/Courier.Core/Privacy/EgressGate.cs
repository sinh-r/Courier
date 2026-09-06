using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Courier.Core.Abstractions;

namespace Courier.Core.Privacy;

/// <summary>
/// The single point in the process where an outbound socket is opened.
/// </summary>
/// <remarks>
/// <para>
/// SEC-01, SEC-02 and P4 are not achievable by convention. They need one auditable chokepoint, so
/// this class owns every HttpMessageHandler Courier constructs, records every destination with the
/// reason it was contacted, and refuses anything the user did not ask for.
/// </para>
/// <para>
/// <b>Do not construct an HttpClient, SocketsHttpHandler or HttpClientHandler anywhere else.</b>
/// Courier.Core.Tests/ArchitectureTests fails the build if you do. That test is the only thing
/// keeping the privacy claim true, so if it fires, fix the call site rather than the test.
/// </para>
/// </remarks>
public sealed class EgressGate : IDisposable
{
    private readonly IProxyResolver _proxyResolver;
    private readonly ITrustStore _trustStore;
    private readonly IEgressPolicy _policy;
    private readonly ConcurrentQueue<EgressRecord> _records = new();
    private readonly ConcurrentDictionary<HandlerProfile, Lazy<HttpMessageHandler>> _handlers = new();
    private readonly ConcurrentDictionary<HandlerProfile, Lazy<HttpMessageInvoker>> _invokers = new();
    private bool _disposed;

    public EgressGate(IProxyResolver proxyResolver, ITrustStore trustStore, IEgressPolicy policy)
    {
        _proxyResolver = proxyResolver;
        _trustStore = trustStore;
        _policy = policy;
    }

    /// <summary>Everything this process has connected to, or been refused. Feeds SEC-06.</summary>
    public IReadOnlyList<EgressRecord> Records => [.. _records];

    /// <summary>The most recent chain verdict, so the response pane can explain a TLS failure.</summary>
    public TrustDecision? LastTrustDecision { get; private set; }

    /// <summary>
    /// Raised on every allowed or denied connection so the status bar and the network statement can
    /// stay current without polling.
    /// </summary>
    public event Action<EgressRecord>? Recorded;

    /// <summary>
    /// Authorises one destination and returns the invoker to use. Throws
    /// <see cref="EgressDeniedException"/> rather than returning null, because a caller that
    /// silently skipped the check is exactly the bug this class exists to prevent.
    /// </summary>
    public HttpMessageInvoker Authorize(Uri destination, EgressPurpose purpose, string initiator, HandlerProfile profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var verdict = _policy.Evaluate(destination, purpose);
        var record = new EgressRecord(
            destination.Host,
            destination.Port,
            destination.Scheme,
            purpose,
            initiator,
            DateTimeOffset.UtcNow,
            verdict.IsAllowed,
            verdict.Reason);

        _records.Enqueue(record);
        Recorded?.Invoke(record);

        if (!verdict.IsAllowed)
        {
            throw new EgressDeniedException(record.Destination, purpose, verdict.Reason ?? "No reason given.");
        }

        return _invokers
            .GetOrAdd(profile, p => new Lazy<HttpMessageInvoker>(
                () => new HttpMessageInvoker(HandlerFor(p), disposeHandler: false)))
            .Value;
    }

    /// <summary>
    /// An HttpClient for a library that insists on owning its own transport — MSAL, Azure.Identity
    /// and Azure.Monitor.Query all do.
    /// </summary>
    /// <remarks>
    /// Without this those SDKs would open sockets Courier never sees, and SEC-06's statement would
    /// silently understate what the process does. The returned client runs every request through
    /// the same policy check and the same record, so a token endpoint appears in the statement
    /// exactly like a target request does.
    /// </remarks>
    public HttpClient CreateClient(EgressPurpose purpose, string initiator, HandlerProfile? profile = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var recording = new RecordingHandler(this, purpose, initiator)
        {
            InnerHandler = HandlerFor(profile ?? HandlerProfile.Default),
        };

        return new HttpClient(recording, disposeHandler: false);
    }

    private HttpMessageHandler HandlerFor(HandlerProfile profile) =>
        _handlers.GetOrAdd(profile, p => new Lazy<HttpMessageHandler>(() => Build(p))).Value;

    /// <summary>
    /// Handler selection, keyed on the resolved request configuration. TECH_SPEC 3.1 warns that
    /// retrofitting this is painful and three of four enterprise auth requirements depend on it,
    /// so it is here from the first commit.
    /// </summary>
    private HttpMessageHandler Build(HandlerProfile profile)
    {
        // Integrated Windows auth (ENT-07) is the one case that cannot use SocketsHttpHandler:
        // NTLM and Kerberos with the current identity need HttpClientHandler.UseDefaultCredentials.
        if (profile.UseIntegratedAuth)
        {
            var integrated = new HttpClientHandler
            {
                UseDefaultCredentials = true,
                PreAuthenticate = true,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                Proxy = new ResolvedWebProxy(_proxyResolver),
                UseProxy = true,
            };

            foreach (var certificate in profile.ClientCertificates)
            {
                integrated.ClientCertificates.Add(certificate);
            }

            integrated.ServerCertificateCustomValidationCallback =
                (_, cert, chain, errors) => ValidateServerCertificate(profile.Host, cert, chain, errors);

            return integrated;
        }

        // Everything else, including PAC-based proxying. TECH_SPEC 3.1 suggests WinHttpHandler for
        // PAC, but that would cost HTTP/2 and HTTP/3, which CORE-01 requires. Instead the PAC
        // evaluation happens inside IProxyResolver (WinHTTP P/Invoke on Windows) and its answer is
        // handed to SocketsHttpHandler as an already-resolved proxy, keeping the modern transports.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,          // Redirects are a per-request policy. CORE-11.
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
            Proxy = new ResolvedWebProxy(_proxyResolver),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            EnableMultipleHttp2Connections = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                    ValidateServerCertificate(profile.Host, cert as X509Certificate2, chain, errors),
            },
        };

        if (profile.ClientCertificates.Count > 0)
        {
            // Smart-card-backed certificates work through CNG without extra handling, as long as
            // nothing tries to export the private key. Nothing here does.
            handler.SslOptions.ClientCertificates = [.. profile.ClientCertificates];
            handler.SslOptions.LocalCertificateSelectionCallback =
                (_, _, _, _, _) => profile.ClientCertificates[0];
        }

        return handler;
    }

    /// <summary>
    /// Chain validation is delegated to the trust store so corporate root CAs installed by IT are
    /// trusted without configuration (ENT-09), and so a failure can name the certificate in the
    /// chain that failed and why (ENT-11).
    /// </summary>
    private bool ValidateServerCertificate(
        string? host,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        var resolvedHost = host
            ?? certificate?.GetNameInfo(X509NameType.DnsName, forIssuer: false)
            ?? "unknown";

        var decision = _trustStore.Validate(
            resolvedHost,
            certificate,
            chain,
            platformSaysValid: errors == SslPolicyErrors.None);

        LastTrustDecision = decision;
        return decision.IsTrusted;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var invoker in _invokers.Values.Where(i => i.IsValueCreated))
        {
            invoker.Value.Dispose();
        }

        foreach (var handler in _handlers.Values.Where(h => h.IsValueCreated))
        {
            handler.Value.Dispose();
        }

        _invokers.Clear();
        _handlers.Clear();
    }

    /// <summary>
    /// Applies the policy and writes a record for a client handed to an outside library, which
    /// cannot be trusted to call <see cref="Authorize"/> itself.
    /// </summary>
    private sealed class RecordingHandler(EgressGate gate, EgressPurpose purpose, string initiator)
        : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } destination)
            {
                // Authorize records and throws on denial. The invoker it returns is discarded here:
                // this handler already has its inner handler, and re-entering would double-record.
                gate.Authorize(destination, purpose, initiator, HandlerProfile.Default);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>Bridges IProxyResolver to the synchronous IWebProxy the handlers expect.</summary>
    private sealed class ResolvedWebProxy(IProxyResolver resolver) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) =>
            resolver.ResolveAsync(destination).AsTask().GetAwaiter().GetResult().ProxyUri;

        public bool IsBypassed(Uri destination) => GetProxy(destination) is null;
    }
}
