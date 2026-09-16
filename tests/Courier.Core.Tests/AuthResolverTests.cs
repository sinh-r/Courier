using Courier.Core.Auth;
using Courier.Core.Collections;

namespace Courier.Core.Tests;

/// <summary>
/// Precedence between a request's own auth, the collection default, and a saved profile. ENT-02.
/// </summary>
public sealed class AuthResolverTests
{
    private static readonly AuthProfile BearerProfile = new() { Name = "svc-bearer", Kind = AuthKind.Bearer };

    [Fact]
    public void A_request_left_on_Inherit_takes_the_collection_default()
    {
        var resolved = AuthResolver.Resolve(
            requestAuth: new AuthReference(AuthMode.Inherit),
            collectionAuth: new AuthReference(AuthMode.Profile, "svc-bearer"),
            profiles: name => name == "svc-bearer" ? BearerProfile : null);

        Assert.True(resolved.HasAuth);
        Assert.Equal(AuthKind.Bearer, resolved.Profile!.Kind);
        Assert.Null(resolved.Problem);
    }

    [Fact]
    public void A_request_with_its_own_profile_ignores_the_collection_default()
    {
        var requestProfile = new AuthProfile { Name = "svc-other", Kind = AuthKind.Basic };

        var resolved = AuthResolver.Resolve(
            requestAuth: new AuthReference(AuthMode.Profile, "svc-other"),
            collectionAuth: new AuthReference(AuthMode.Profile, "svc-bearer"),
            profiles: name => name switch
            {
                "svc-bearer" => BearerProfile,
                "svc-other" => requestProfile,
                _ => null,
            });

        Assert.Equal(AuthKind.Basic, resolved.Profile!.Kind);
    }

    [Fact]
    public void No_auth_on_either_the_request_or_the_collection_resolves_to_no_auth()
    {
        var resolved = AuthResolver.Resolve(null, null, _ => null);

        Assert.False(resolved.HasAuth);
        Assert.Null(resolved.Problem);
    }

    [Fact]
    public void Explicit_none_on_the_request_wins_even_when_the_collection_has_a_default()
    {
        var resolved = AuthResolver.Resolve(
            requestAuth: new AuthReference(AuthMode.None),
            collectionAuth: new AuthReference(AuthMode.Profile, "svc-bearer"),
            profiles: name => name == "svc-bearer" ? BearerProfile : null);

        Assert.False(resolved.HasAuth);
        Assert.Null(resolved.Problem);
    }

    [Fact]
    public void A_profile_name_that_does_not_exist_is_a_problem_never_silent_no_auth()
    {
        var resolved = AuthResolver.Resolve(
            requestAuth: new AuthReference(AuthMode.Profile, "ghost"),
            collectionAuth: null,
            profiles: _ => null);

        Assert.False(resolved.HasAuth);
        Assert.NotNull(resolved.Problem);
        Assert.Contains("ghost", resolved.Problem);
        Assert.Contains("auth/", resolved.Problem);
    }

    [Fact]
    public void Inline_auth_on_the_request_applies_directly_with_no_profile_lookup()
    {
        var inline = new AuthProfile { Kind = AuthKind.Bearer };

        var resolved = AuthResolver.Resolve(
            requestAuth: new AuthReference(AuthMode.Inline, Inline: inline),
            collectionAuth: null,
            profiles: _ => throw new InvalidOperationException("Inline auth must not look up a profile."));

        Assert.True(resolved.HasAuth);
        Assert.Same(inline, resolved.Profile);
    }

    [Fact]
    public void A_collection_with_no_auth_configured_means_no_auth_at_all()
    {
        var resolved = AuthResolver.Resolve(new AuthReference(AuthMode.Inherit), null, _ => null);

        Assert.False(resolved.HasAuth);
        Assert.Null(resolved.Problem);
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("Basic")]
    [InlineData("API Key")]
    public void Legacy_placeholder_profile_names_are_reported_not_silently_dropped(string name)
    {
        // Pre-ENT-02 sessions could write the request-auth combo box's literal placeholder text
        // ("Bearer", "Basic", "API Key") as if it were a profile name. None of those exist as real
        // profiles, so this must surface as a Problem exactly like any other missing profile —
        // never resolve to "no auth" as if nothing had been chosen.
        var resolved = AuthResolver.Resolve(new AuthReference(AuthMode.Profile, name), null, _ => null);

        Assert.False(resolved.HasAuth);
        Assert.NotNull(resolved.Problem);
    }
}
