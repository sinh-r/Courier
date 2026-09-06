using Courier.Core.Abstractions;
using Courier.Core.Privacy;
using Microsoft.Identity.Client;

namespace Courier.Core.Auth;

/// <summary>
/// Entra ID token acquisition through MSAL. ENT-01, ENT-02, ENT-05.
/// </summary>
/// <remarks>
/// <para>
/// The broker — which is what delivers silent SSO from the machine's existing Windows sign-in, and
/// the feature nobody else has — needs a native window handle and a Windows-only package, so it is
/// contributed by <see cref="IBrokerConfigurator"/> from Courier.Platform.Windows. Everything else
/// here is cross-platform, which is what lets the CLI acquire client-credential tokens in CI.
/// </para>
/// <para>
/// MSAL is given an HttpClient from <see cref="EgressGate"/>. Left to itself it would open sockets
/// Courier never records, and the SEC-06 statement would quietly understate what the process does.
/// </para>
/// </remarks>
public sealed class EntraAuthProvider : IAuthProvider, IDisposable
{
    private const string Initiator = nameof(EntraAuthProvider);
    private const string DefaultAuthority = "https://login.microsoftonline.com";

    private readonly ISecretStore _secrets;
    private readonly EgressGate _gate;
    private readonly UserIntentEgressPolicy _policy;
    private readonly IBrokerConfigurator? _broker;
    private readonly Dictionary<string, IClientApplicationBase> _applications = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly MsalHttpClientFactory _httpClientFactory;

    public EntraAuthProvider(
        ISecretStore secrets,
        EgressGate gate,
        UserIntentEgressPolicy policy,
        IBrokerConfigurator? broker = null)
    {
        _secrets = secrets;
        _gate = gate;
        _policy = policy;
        _broker = broker;
        _httpClientFactory = new MsalHttpClientFactory(gate);
    }

    public AuthKind Kind => AuthKind.EntraWindowsSignIn;

    /// <summary>Raised when a device code flow needs the user to visit a URL. ENT-02.</summary>
    public event Action<DeviceCodePrompt>? DeviceCodeIssued;

    public async ValueTask<AuthResult> ApplyAsync(
        AuthProfile profile,
        AuthContext context,
        CancellationToken ct = default)
    {
        var token = await AcquireAsync(profile, interactiveAllowed: false, ct).ConfigureAwait(false);

        return new AuthResult(
            [new KeyValuePair<string, string>("Authorization", $"Bearer {token.AccessToken}")],
            [],
            Token: token.AccessToken,
            ExpiresOn: token.ExpiresOn,
            Interactive: token.WasInteractive,
            Identity: token.Account,
            GrantedScopes: token.Scopes);
    }

    /// <summary>
    /// Acquires a token, silently where possible.
    /// </summary>
    /// <param name="interactiveAllowed">
    /// False during a send. ENT-05: never block a send on an interactive prompt without warning, so
    /// the executor calls with false, catches <see cref="InteractiveAuthRequiredException"/>, warns,
    /// and only then calls again with true.
    /// </param>
    public async Task<AcquiredToken> AcquireAsync(
        AuthProfile profile,
        bool interactiveAllowed,
        CancellationToken ct = default)
    {
        RegisterAuthority(profile);

        var scopes = profile.Scopes.Count > 0
            ? profile.Scopes.ToArray()
            : [$"{profile.ClientId}/.default"];

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return profile.Kind switch
            {
                AuthKind.EntraClientCredentials =>
                    await ClientCredentialsAsync(profile, scopes, ct).ConfigureAwait(false),

                AuthKind.EntraDeviceCode =>
                    await DeviceCodeAsync(profile, scopes, interactiveAllowed, ct).ConfigureAwait(false),

                // Windows sign-in and authorization code share a path: try silent first, and only
                // prompt when the cache and the broker both come up empty.
                _ => await SilentThenInteractiveAsync(profile, scopes, interactiveAllowed, ct).ConfigureAwait(false),
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<AcquiredToken> ClientCredentialsAsync(
        AuthProfile profile,
        string[] scopes,
        CancellationToken ct)
    {
        var secret = await _secrets.GetAsync(profile.SecretKey, ct).ConfigureAwait(false)
            ?? throw new InteractiveAuthRequiredException(
                profile.Name,
                "No client secret is stored for this profile. Add it in the auth profile settings; "
                + "it goes to the credential store, never to the collection file.");

        var application = (IConfidentialClientApplication)GetOrCreate(profile, () =>
            ConfidentialClientApplicationBuilder
                .Create(profile.ClientId)
                .WithClientSecret(secret)
                .WithAuthority(AuthorityFor(profile))
                .WithHttpClientFactory(_httpClientFactory)
                .Build());

        var result = await application
            .AcquireTokenForClient(scopes)
            .ExecuteAsync(ct)
            .ConfigureAwait(false);

        return Describe(result, wasInteractive: false);
    }

    private async Task<AcquiredToken> DeviceCodeAsync(
        AuthProfile profile,
        string[] scopes,
        bool interactiveAllowed,
        CancellationToken ct)
    {
        var application = (IPublicClientApplication)GetOrCreate(profile, () => BuildPublicClient(profile));

        if (await TrySilentAsync(application, scopes, ct).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        if (!interactiveAllowed)
        {
            throw new InteractiveAuthRequiredException(
                profile.Name,
                "There is no cached token, so a device code sign-in is needed.");
        }

        var result = await application
            .AcquireTokenWithDeviceCode(scopes, callback =>
            {
                DeviceCodeIssued?.Invoke(new DeviceCodePrompt(
                    callback.UserCode,
                    callback.VerificationUrl,
                    callback.Message,
                    callback.ExpiresOn));

                return Task.CompletedTask;
            })
            .ExecuteAsync(ct)
            .ConfigureAwait(false);

        return Describe(result, wasInteractive: true);
    }

    private async Task<AcquiredToken> SilentThenInteractiveAsync(
        AuthProfile profile,
        string[] scopes,
        bool interactiveAllowed,
        CancellationToken ct)
    {
        var application = (IPublicClientApplication)GetOrCreate(profile, () => BuildPublicClient(profile));

        // With the broker configured this is the whole of ENT-01: the machine's existing Windows
        // sign-in satisfies the request and the user is never prompted at all.
        if (await TrySilentAsync(application, scopes, ct).ConfigureAwait(false) is { } silent)
        {
            return silent;
        }

        if (!interactiveAllowed)
        {
            throw new InteractiveAuthRequiredException(
                profile.Name,
                "Windows sign-in did not satisfy this request silently, so an interactive sign-in is needed.");
        }

        var builder = application.AcquireTokenInteractive(scopes);
        if (_broker?.GetParentWindow() is { } handle && handle != IntPtr.Zero)
        {
            builder = builder.WithParentActivityOrWindow(handle);
        }

        var result = await builder.ExecuteAsync(ct).ConfigureAwait(false);
        return Describe(result, wasInteractive: true);
    }

    /// <summary>
    /// Silent acquisition, including the broker's account discovery. Returns null rather than
    /// throwing on <c>MsalUiRequiredException</c>, which is an expected outcome, not a fault.
    /// </summary>
    private static async Task<AcquiredToken?> TrySilentAsync(
        IPublicClientApplication application,
        string[] scopes,
        CancellationToken ct)
    {
        try
        {
            var accounts = await application.GetAccountsAsync().ConfigureAwait(false);
            var account = accounts.FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;

            var result = await application
                .AcquireTokenSilent(scopes, account)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);

            return Describe(result, wasInteractive: false);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    private IPublicClientApplication BuildPublicClient(AuthProfile profile)
    {
        var builder = PublicClientApplicationBuilder
            .Create(profile.ClientId)
            .WithAuthority(AuthorityFor(profile))
            .WithHttpClientFactory(_httpClientFactory)
            .WithRedirectUri(profile.RedirectUri ?? "http://localhost");

        // Windows only. On any other platform the broker configurator is absent and MSAL falls back
        // to the system browser, which is the honest degradation.
        builder = _broker?.Configure(builder) ?? builder;

        return builder.Build();
    }

    private IClientApplicationBase GetOrCreate(AuthProfile profile, Func<IClientApplicationBase> factory)
    {
        var key = $"{profile.Name}|{profile.Kind}|{profile.Tenant}|{profile.ClientId}";
        if (!_applications.TryGetValue(key, out var application))
        {
            application = factory();
            _applications[key] = application;
        }

        return application;
    }

    private string AuthorityFor(AuthProfile profile) =>
        profile.Authority ?? $"{DefaultAuthority}/{profile.Tenant ?? "organizations"}";

    /// <summary>
    /// Makes the token authority a legitimate destination. P4: every network destination is
    /// user-visible and user-explicable, and configuring this profile is the user action that
    /// explains it.
    /// </summary>
    private void RegisterAuthority(AuthProfile profile)
    {
        if (Uri.TryCreate(AuthorityFor(profile), UriKind.Absolute, out var authority))
        {
            _policy.Register(authority, EgressPurpose.AuthAuthority);
        }
    }

    private static AcquiredToken Describe(AuthenticationResult result, bool wasInteractive) => new(
        result.AccessToken,
        result.ExpiresOn,
        result.Account?.Username,
        result.Scopes?.ToArray() ?? [],
        wasInteractive);

    public void Dispose() => _lock.Dispose();

    /// <summary>Routes MSAL's HTTP through the egress chokepoint. SEC-01, SEC-06.</summary>
    private sealed class MsalHttpClientFactory(EgressGate gate) : IMsalHttpClientFactory
    {
        private HttpClient? _client;

        public HttpClient GetHttpClient() =>
            _client ??= gate.CreateClient(EgressPurpose.AuthAuthority, Initiator);
    }
}

/// <param name="Scopes">What the authority actually granted, which is not always what was asked for.</param>
public sealed record AcquiredToken(
    string AccessToken,
    DateTimeOffset ExpiresOn,
    string? Account,
    IReadOnlyList<string> Scopes,
    bool WasInteractive)
{
    /// <summary>True when the token should be refreshed now rather than on the next 401. ENT-05.</summary>
    public bool ShouldRefresh(int leewaySeconds) =>
        DateTimeOffset.UtcNow >= ExpiresOn.AddSeconds(-leewaySeconds);
}

/// <summary>
/// Supplied by Courier.Platform.Windows. Adds the WAM broker and the parent window handle that
/// ENT-01 depends on, without dragging a Windows-only package into Courier.Core.
/// </summary>
public interface IBrokerConfigurator
{
    PublicClientApplicationBuilder Configure(PublicClientApplicationBuilder builder);

    IntPtr GetParentWindow();
}

public sealed record DeviceCodePrompt(string UserCode, string VerificationUrl, string Message, DateTimeOffset ExpiresOn);
