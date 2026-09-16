using Courier.Core.Abstractions;
using Courier.Core.Auth;
using Courier.Core.Collections;
using Courier.Core.Http;
using Courier.Core.Variables;

namespace Courier.Core.Tests;

/// <summary>
/// Auth actually applying at send time — the gap this closed. Before this, <see cref="RequestPreparer"/>
/// never added an auth header at all.
/// </summary>
public sealed class RequestPreparerAuthTests
{
    private static RequestDefinition Request(AuthReference? auth = null, List<HeaderValue>? headers = null) => new()
    {
        Method = "GET",
        Url = "https://api.example/orders",
        Headers = headers ?? [],
        Auth = auth,
    };

    private static AuthProviderRegistry RegistryWith(FakeSecretStore secrets) => new(
        new Dictionary<AuthKind, IAuthProvider>
        {
            [AuthKind.Bearer] = new BearerAuthProvider(secrets),
            [AuthKind.Basic] = new BasicAuthProvider(secrets),
        });

    [Fact]
    public async Task A_bearer_profile_adds_the_Authorization_header()
    {
        var secrets = new FakeSecretStore();
        var profile = new AuthProfile { Name = "svc", Kind = AuthKind.Bearer, SecretRef = "svc-ref" };
        await secrets.SetAsync(profile.SecretKey, "s3cr3t-token", TestContext.Current.CancellationToken);

        var request = Request(new AuthReference(AuthMode.Profile, "svc"));
        var resolved = AuthResolver.Resolve(request.Auth, null, name => name == "svc" ? profile : null);
        var plan = new AuthPlan(resolved, RegistryWith(secrets));

        var result = await RequestPreparer.PrepareAsync(
            request, new VariableScopes(), new VariableResolver(secrets), auth: plan,
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var authorization = result.Request!.Headers.Single(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Bearer s3cr3t-token", authorization.Value);
        Assert.Contains("s3cr3t-token", result.Request.SecretValues);
    }

    [Fact]
    public async Task Basic_auth_encodes_username_and_the_stored_password()
    {
        var secrets = new FakeSecretStore();
        var profile = new AuthProfile { Name = "svc", Kind = AuthKind.Basic, Username = "alice", SecretRef = "svc-ref" };
        await secrets.SetAsync(profile.SecretKey, "hunter2", TestContext.Current.CancellationToken);

        var request = Request(new AuthReference(AuthMode.Profile, "svc"));
        var resolved = AuthResolver.Resolve(request.Auth, null, _ => profile);
        var plan = new AuthPlan(resolved, RegistryWith(secrets));

        var result = await RequestPreparer.PrepareAsync(
            request, new VariableScopes(), new VariableResolver(secrets), auth: plan,
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var authorization = result.Request!.Headers.Single(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"Basic {Convert.ToBase64String("alice:hunter2"u8.ToArray())}", authorization.Value);
    }

    [Fact]
    public async Task The_auth_tab_replaces_an_Authorization_header_typed_by_hand()
    {
        // Decided precedence: the Auth tab wins over a header the user typed directly, so a stale
        // Authorization left over from before an auth profile was chosen cannot silently override it.
        var secrets = new FakeSecretStore();
        var profile = new AuthProfile { Name = "svc", Kind = AuthKind.Bearer, SecretRef = "svc-ref" };
        await secrets.SetAsync(profile.SecretKey, "fresh-token", TestContext.Current.CancellationToken);

        var request = Request(
            new AuthReference(AuthMode.Profile, "svc"),
            headers: [new HeaderValue("Authorization", "Bearer stale-token")]);

        var resolved = AuthResolver.Resolve(request.Auth, null, _ => profile);
        var plan = new AuthPlan(resolved, RegistryWith(secrets));

        var result = await RequestPreparer.PrepareAsync(
            request, new VariableScopes(), new VariableResolver(secrets), auth: plan,
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var authorizationHeaders = result.Request!.Headers.Where(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(authorizationHeaders);
        Assert.Equal("Bearer fresh-token", authorizationHeaders[0].Value);
    }

    [Fact]
    public async Task A_missing_secret_fails_preparation_with_a_clear_reason_rather_than_sending_unauthenticated()
    {
        var secrets = new FakeSecretStore();
        var profile = new AuthProfile { Name = "svc", Kind = AuthKind.Bearer, SecretRef = "svc-ref" };
        // Nothing stored for profile.SecretKey.

        var request = Request(new AuthReference(AuthMode.Profile, "svc"));
        var resolved = AuthResolver.Resolve(request.Auth, null, _ => profile);
        var plan = new AuthPlan(resolved, RegistryWith(secrets));

        var result = await RequestPreparer.PrepareAsync(
            request, new VariableScopes(), new VariableResolver(secrets), auth: plan,
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(result.NeedsInteractiveSignIn);
        Assert.Equal("svc", result.SignInProfile);
    }

    [Fact]
    public async Task A_profile_that_does_not_exist_fails_preparation_instead_of_sending_unauthenticated()
    {
        var secrets = new FakeSecretStore();
        var request = Request(new AuthReference(AuthMode.Profile, "ghost"));
        var resolved = AuthResolver.Resolve(request.Auth, null, _ => null);
        var plan = new AuthPlan(resolved, RegistryWith(secrets));

        var result = await RequestPreparer.PrepareAsync(
            request, new VariableScopes(), new VariableResolver(secrets), auth: plan,
            ct: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("ghost", result.Error);
    }

    [Fact]
    public async Task Inherit_with_no_collection_default_sends_with_no_auth_at_all()
    {
        var secrets = new FakeSecretStore();
        var request = Request(new AuthReference(AuthMode.Inherit));
        var resolved = AuthResolver.Resolve(request.Auth, null, _ => null);
        var plan = new AuthPlan(resolved, RegistryWith(secrets));

        var result = await RequestPreparer.PrepareAsync(
            request, new VariableScopes(), new VariableResolver(secrets), auth: plan,
            ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Request!.Headers, h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(result.Request.SecretValues);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string LocationDescription => "in-memory, for tests";

        public bool IsHardwareBacked => false;

        public ValueTask<string?> GetAsync(SecretKey key, CancellationToken ct = default) =>
            ValueTask.FromResult(_values.GetValueOrDefault(key.ToString()));

        public ValueTask SetAsync(SecretKey key, string value, CancellationToken ct = default)
        {
            _values[key.ToString()] = value;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(SecretKey key, CancellationToken ct = default)
        {
            _values.Remove(key.ToString());
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<SecretKey>> ListAsync(string scope, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<SecretKey>>([]);
    }
}
