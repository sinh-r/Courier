namespace Courier.Core.Auth;

/// <summary>
/// Dispatches a resolved auth profile to the <see cref="IAuthProvider"/> that knows its
/// <see cref="AuthKind"/>. One place to look up "what actually applies this", instead of every
/// caller (the app's Send, <c>courier run</c>) switching on <see cref="AuthKind"/> itself.
/// </summary>
public sealed class AuthProviderRegistry
{
    private readonly IReadOnlyDictionary<AuthKind, IAuthProvider> _providers;

    /// <summary>
    /// Takes an explicit map rather than inferring one from each provider's own <see cref="IAuthProvider.Kind"/>:
    /// <see cref="EntraAuthProvider"/> reports one fixed <see cref="AuthKind"/> but actually
    /// dispatches on <see cref="AuthProfile.Kind"/> internally across all four Entra grants, so a
    /// map built from <c>.Kind</c> alone would only ever resolve one of them.
    /// </summary>
    public AuthProviderRegistry(IReadOnlyDictionary<AuthKind, IAuthProvider> providers)
    {
        _providers = providers;
    }

    /// <summary>
    /// Applies the resolved auth to one request. <see cref="AuthResult.None"/> for
    /// <see cref="AuthKind.None"/> or an unresolved profile — the caller already has
    /// <see cref="ResolvedAuth.Problem"/> to show for the latter.
    /// </summary>
    public async ValueTask<AuthResult> ApplyAsync(ResolvedAuth resolved, AuthContext context, CancellationToken ct = default)
    {
        if (!resolved.HasAuth || resolved.Profile is not { } profile)
        {
            return AuthResult.None;
        }

        if (!_providers.TryGetValue(profile.Kind, out var provider))
        {
            throw new NotSupportedException($"'{profile.Kind}' auth is not supported by this build of Courier.");
        }

        return await provider.ApplyAsync(profile, context, ct).ConfigureAwait(false);
    }
}
