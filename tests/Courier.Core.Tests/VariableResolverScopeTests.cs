using Courier.Core.Abstractions;
using Courier.Core.Collections;
using Courier.Core.Variables;

namespace Courier.Core.Tests;

/// <summary>
/// Collection versus global: an unqualified name resolves in the collection first and falls back
/// to the globals; <c>global.</c> and <c>collection.Env.</c> pin a reference to one scope.
/// </summary>
public sealed class VariableResolverScopeTests
{
    private readonly FakeSecretStore _secrets = new();

    private static readonly EnvironmentDefinition Globals = new()
    {
        Name = "Globals",
        Shared = { ["baseUrl"] = "https://global.example", ["tenant"] = "contoso" },
        LocalNames = { "apiKey" },
    };

    private static readonly EnvironmentDefinition Local = new()
    {
        Name = "Local",
        Shared = { ["baseUrl"] = "https://localhost:5001" },
    };

    private static readonly EnvironmentDefinition Qa = new()
    {
        Name = "QA",
        Shared = { ["baseUrl"] = "https://qa.example" },
    };

    private VariableResolver Resolver => new(_secrets);

    private static VariableScopes Scopes() => new()
    {
        Environment = Local,
        Collection = new Dictionary<string, string> { ["version"] = "v2" },
        GlobalEnvironment = Globals,
        EnvironmentLookup = name => name == "QA" ? Qa : null,
    };

    [Fact]
    public async Task The_collection_wins_over_a_global_of_the_same_name()
    {
        var resolved = await Resolver.ResolveAsync("baseUrl", Scopes(), TestContext.Current.CancellationToken);

        Assert.Equal("https://localhost:5001", resolved.Value);
        Assert.Equal(VariableOrigin.EnvironmentShared, resolved.Origin);
    }

    [Fact]
    public async Task A_name_the_collection_lacks_falls_back_to_the_global()
    {
        var resolved = await Resolver.ResolveAsync("tenant", Scopes(), TestContext.Current.CancellationToken);

        Assert.Equal("contoso", resolved.Value);
        Assert.Equal(VariableOrigin.Global, resolved.Origin);
    }

    [Fact]
    public async Task The_global_prefix_reads_the_global_even_when_the_collection_has_the_name()
    {
        var resolved = await Resolver.ResolveAsync("global.baseUrl", Scopes(), TestContext.Current.CancellationToken);

        Assert.Equal("https://global.example", resolved.Value);
        Assert.Equal("global.baseUrl", resolved.Name);
    }

    [Fact]
    public async Task The_global_prefix_never_falls_back_to_the_collection()
    {
        var resolved = await Resolver.ResolveAsync("global.version", Scopes(), TestContext.Current.CancellationToken);

        Assert.False(resolved.IsResolved);
    }

    [Fact]
    public async Task The_collection_prefix_reads_a_named_environment_that_is_not_selected()
    {
        var resolved = await Resolver.ResolveAsync("collection.QA.baseUrl", Scopes(), TestContext.Current.CancellationToken);

        Assert.Equal("https://qa.example", resolved.Value);
    }

    [Fact]
    public async Task The_collection_prefix_never_falls_back_to_a_global()
    {
        var resolved = await Resolver.ResolveAsync("collection.QA.tenant", Scopes(), TestContext.Current.CancellationToken);

        Assert.False(resolved.IsResolved);
    }

    [Fact]
    public async Task The_collection_prefix_without_an_environment_reads_the_collections_own_scopes()
    {
        var resolved = await Resolver.ResolveAsync("collection.version", Scopes(), TestContext.Current.CancellationToken);

        Assert.Equal("v2", resolved.Value);
        Assert.Equal(VariableOrigin.Collection, resolved.Origin);
    }

    [Fact]
    public async Task A_local_global_is_read_from_the_global_secret_scope_not_the_environment_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await _secrets.SetAsync(new SecretKey(SecretKey.EnvironmentScope, "Globals/apiKey"), "collection-env-value", ct);
        await _secrets.SetAsync(new SecretKey(SecretKey.GlobalScope, "Globals/apiKey"), "global-value", ct);

        var resolved = await Resolver.ResolveAsync("apiKey", Scopes(), ct);

        Assert.Equal("global-value", resolved.Value);
        Assert.Equal(VariableOrigin.GlobalLocal, resolved.Origin);
        Assert.True(resolved.IsSecret);
    }

    [Fact]
    public async Task Substitution_mixes_scopes_in_one_url()
    {
        var result = await Resolver.SubstituteAsync(
            "{{baseUrl}}/{{version}}/{{tenant}}?mirror={{collection.QA.baseUrl}}",
            Scopes(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsComplete);
        Assert.Equal("https://localhost:5001/v2/contoso?mirror=https://qa.example", result.Text);
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
