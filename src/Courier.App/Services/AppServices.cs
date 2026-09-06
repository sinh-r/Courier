using Courier.Core.Abstractions;
using Courier.Core.Auth;
using Courier.Core.Capsules;
using Courier.Core.Http;
using Courier.Core.Privacy;
using Courier.Core.Storage;
using Courier.Core.Variables;
using Courier.Scanner;

#if WINDOWS
using Courier.Platform.Windows.Auth;
using Courier.Platform.Windows.Certificates;
using Courier.Platform.Windows.Proxy;
using Courier.Platform.Windows.Secrets;
using Courier.Platform.Windows.Shell;
#else
using Courier.Platform.Posix;
#endif

namespace Courier.App.Services;

/// <summary>
/// The composition root, built by hand rather than by a container.
/// </summary>
/// <remarks>
/// TECH_SPEC 6 suggests Microsoft.Extensions.DependencyInjection "but keep the app's own graph
/// shallow; startup cost is a gate". The graph here turned out shallow enough that a container
/// would add a scan and a resolution step to the PERF-01 budget and buy nothing back. Everything
/// expensive is a <see cref="Lazy{T}"/> so constructing this object touches no disk and no network.
/// </remarks>
public sealed class AppServices : IDisposable
{
    private readonly Lazy<Task<CourierDatabase>> _database;
    private readonly Lazy<EntraAuthProvider> _entra;
    private readonly Lazy<DeveloperToolTokenSource> _developerTokens;

    private AppServices(string[] commandLineArgs)
    {
        CommandLineArgs = commandLineArgs;

        SecretStore = CreateSecretStore();
        ProxyResolver = CreateProxyResolver();
        TrustStore = CreateTrustStore();
        CertificateSource = CreateCertificateSource();
        IntegratedAuth = CreateIntegratedAuth();
        FileRevealer = CreateFileRevealer();
        FileAssociations = CreateFileAssociationRegistrar();
        BrokerWindows = new ActiveWindowHandleProvider();

        EgressPolicy = new UserIntentEgressPolicy();
        Egress = new EgressGate(ProxyResolver, TrustStore, EgressPolicy);

        SecretRegistry = new SecretRegistry();
        Cookies = new CookieJar();
        Trace = new TraceInjector();
        Executor = new RequestExecutor(Egress, Cookies, Trace);
        Variables = new VariableResolver(SecretStore);
        Redaction = new RedactionEngine();
        Git = new GitStatusSource();
        Scanner = new SolutionScanner();

        // Deferred, and this is not a micro-optimisation. Touching EntraAuthProvider loads MSAL and
        // its broker runtime; touching DeveloperToolTokenSource loads Azure.Identity. Both are
        // large assembly graphs, both were on the path to first frame, and an app that may never
        // authenticate was paying for them on every launch. PERF-01 is a release gate with 800ms
        // for a warm start, and this is the single largest thing that was inside it.
        _entra = new Lazy<EntraAuthProvider>(
            () => new EntraAuthProvider(SecretStore, Egress, EgressPolicy, CreateBrokerConfigurator()));

        _developerTokens = new Lazy<DeveloperToolTokenSource>(() => new DeveloperToolTokenSource(Egress));

        // Deferred: opening SQLite migrates a schema, and PERF-01 does not have room for that
        // before first paint.
        _database = new Lazy<Task<CourierDatabase>>(() => CourierDatabase.OpenAsync());
        StartupJobs = new StartupJobQueue(this);
    }

    public string[] CommandLineArgs { get; }

    public ISecretStore SecretStore { get; }

    public IProxyResolver ProxyResolver { get; }

    public ITrustStore TrustStore { get; }

    public ICertificateSource CertificateSource { get; }

    public IIntegratedAuthProvider IntegratedAuth { get; }

    public IFileRevealer FileRevealer { get; }

    public IFileAssociationRegistrar FileAssociations { get; }

    public ActiveWindowHandleProvider BrokerWindows { get; }

    public UserIntentEgressPolicy EgressPolicy { get; }

    public EgressGate Egress { get; }

    public SecretRegistry SecretRegistry { get; }

    public CookieJar Cookies { get; }

    public TraceInjector Trace { get; }

    public RequestExecutor Executor { get; }

    public VariableResolver Variables { get; }

    public RedactionEngine Redaction { get; }

    public GitStatusSource Git { get; }

    /// <summary>Syntax-only, no restore or build required. Runs on demand, never at startup. SCAN-08.</summary>
    public SolutionScanner Scanner { get; }

    /// <summary>Constructed on first use. Loading MSAL is not free, and most sessions never need it.</summary>
    public EntraAuthProvider Entra => _entra.Value;

    /// <summary>Constructed on first use. Loading Azure.Identity is not free either.</summary>
    public DeveloperToolTokenSource DeveloperTokens => _developerTokens.Value;

    public StartupJobQueue StartupJobs { get; }

    public string Version { get; } =
        typeof(AppServices).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public Task<CourierDatabase> DatabaseAsync() => _database.Value;

    public static AppServices Build(string[] commandLineArgs) => new(commandLineArgs);

    public void Dispose()
    {
        if (_database.IsValueCreated && _database.Value.IsCompletedSuccessfully)
        {
            _database.Value.Result.Dispose();
        }

        if (_entra.IsValueCreated)
        {
            _entra.Value.Dispose();
        }

        Trace.Dispose();
        Egress.Dispose();

#if WINDOWS
        (ProxyResolver as WinHttpProxyResolver)?.Dispose();
#endif
    }

    // Platform selection is compile-time, so the Windows assembly is not even loaded on Linux.
#if WINDOWS
    private static ISecretStore CreateSecretStore() => new WindowsSecretStore();

    private static IProxyResolver CreateProxyResolver() => new WinHttpProxyResolver();

    private static ITrustStore CreateTrustStore() => new WindowsTrustStore();

    private static ICertificateSource CreateCertificateSource() => new WindowsCertificateSource();

    private static IIntegratedAuthProvider CreateIntegratedAuth() => new WindowsIntegratedAuth();

    private static IFileRevealer CreateFileRevealer() => new WindowsFileRevealer();

    private static IFileAssociationRegistrar CreateFileAssociationRegistrar() =>
        new WindowsFileAssociationRegistrar();

    private IBrokerConfigurator CreateBrokerConfigurator() => new WamBrokerConfigurator(BrokerWindows);
#else
    private static ISecretStore CreateSecretStore() => new FallbackSecretStore();

    private static IProxyResolver CreateProxyResolver() => new EnvironmentProxyResolver();

    private static ITrustStore CreateTrustStore() => new FallbackTrustStore();

    private static ICertificateSource CreateCertificateSource() => new FileCertificateSource();

    private static IIntegratedAuthProvider CreateIntegratedAuth() => new UnavailableIntegratedAuth();

    private static IFileRevealer CreateFileRevealer() => new PosixFileRevealer();

    private static IFileAssociationRegistrar CreateFileAssociationRegistrar() =>
        new NoFileAssociationRegistrar();

    private IBrokerConfigurator? CreateBrokerConfigurator() => null;
#endif
}
