using Courier.Core.Abstractions;
using Courier.Core.Collections;
using Courier.Core.Http;
using Courier.Core.Privacy;
using Courier.Core.Variables;
using Courier.Platform.Posix;

namespace Courier.Cli.Services;

/// <summary>
/// The CLI's composition root.
/// </summary>
/// <remarks>
/// Deliberately the cross-platform implementations even on Windows. The CLI runs on build agents,
/// and a build agent has no interactive session to unlock the Windows credential store — reaching
/// for it would hang the pipeline rather than fail it. Secrets on an agent come from environment
/// variables, which is what CI systems already inject.
/// </remarks>
internal sealed class CliServices : IDisposable
{
    private CliServices()
    {
        SecretStore = new EnvironmentSecretStore();
        ProxyResolver = new EnvironmentProxyResolver();
        TrustStore = new FallbackTrustStore();
        EgressPolicy = new UserIntentEgressPolicy();
        Egress = new EgressGate(ProxyResolver, TrustStore, EgressPolicy);
        Cookies = new CookieJar();
        Trace = new TraceInjector();
        Executor = new RequestExecutor(Egress, Cookies, Trace);
        Variables = new VariableResolver(SecretStore);
    }

    public ISecretStore SecretStore { get; }

    public IProxyResolver ProxyResolver { get; }

    public ITrustStore TrustStore { get; }

    public UserIntentEgressPolicy EgressPolicy { get; }

    public EgressGate Egress { get; }

    public CookieJar Cookies { get; }

    public TraceInjector Trace { get; }

    public RequestExecutor Executor { get; }

    public VariableResolver Variables { get; }

    public static CliServices Build() => new();

    public void Dispose()
    {
        Trace.Dispose();
        Egress.Dispose();
    }
}

/// <summary>
/// Secrets from environment variables, which is how CI systems already inject them.
/// </summary>
/// <remarks>
/// <c>COURIER_SECRET_ENV_APIKEY</c> supplies the <c>env/apiKey</c> secret. Uppercased with
/// non-alphanumerics folded to underscores, because that is the only shape every CI system's
/// secret store agrees on.
///
/// Writing is refused rather than silently dropped: a pipeline that thinks it stored a token and
/// did not is worse than one that stops and says so.
/// </remarks>
internal sealed class EnvironmentSecretStore : ISecretStore
{
    private const string Prefix = "COURIER_SECRET_";

    public string LocationDescription =>
        $"environment variables prefixed {Prefix}. Nothing is written to disk.";

    public bool IsHardwareBacked => false;

    public ValueTask<string?> GetAsync(SecretKey key, CancellationToken ct = default) =>
        ValueTask.FromResult(Environment.GetEnvironmentVariable(NameFor(key)));

    public ValueTask SetAsync(SecretKey key, string value, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"The CLI does not write secrets. Set {NameFor(key)} in the pipeline's secret store instead.");

    public ValueTask DeleteAsync(SecretKey key, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<SecretKey>> ListAsync(string scope, CancellationToken ct = default)
    {
        var prefix = $"{Prefix}{Normalise(scope)}_";
        var keys = new List<SecretKey>();

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                keys.Add(new SecretKey(scope, name[prefix.Length..]));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<SecretKey>>(keys);
    }

    private static string NameFor(SecretKey key) =>
        $"{Prefix}{Normalise(key.Scope)}_{Normalise(key.Name)}";

    private static string Normalise(string value) =>
        new([.. value.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_')]);
}

// CollectionLoader and LoadedCollection moved to Courier.Core.Collections, so the GUI can load
// collection.yaml and environments the same way the CLI does.
