using Courier.Core.Abstractions;
using Courier.Core.Collections;
using Courier.Core.Http;
using Courier.Core.Variables;

namespace Courier.Core.Tests;

/// <summary>
/// Golden tests for the preparer both <c>courier run</c> and the desktop app's Send now share.
/// </summary>
public sealed class RequestPreparerTests
{
    private static readonly VariableResolver Variables = new(new FakeSecretStore());

    private static RequestDefinition Request(
        string url,
        Dictionary<string, string>? pathParams = null,
        List<HeaderValue>? headers = null) => new()
        {
            Method = "GET",
            Url = url,
            PathParams = pathParams ?? [],
            Headers = headers ?? [],
        };

    [Fact]
    public async Task Substitutes_a_variable_and_a_path_parameter_together()
    {
        var request = Request(
            "{{baseUrl}}/articles/{slug}",
            pathParams: new Dictionary<string, string> { ["slug"] = "hello-world" });

        var scopes = new VariableScopes
        {
            Collection = new Dictionary<string, string> { ["baseUrl"] = "https://api.example" },
        };

        var result = await RequestPreparer.PrepareAsync(request, scopes, Variables, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example/articles/hello-world", result.Request!.Url.ToString());
    }

    [Fact]
    public async Task Request_scope_wins_over_environment_over_collection_over_global()
    {
        var request = Request("https://api.example/{{name}}");

        var scopes = new VariableScopes
        {
            Request = new Dictionary<string, string> { ["name"] = "from-request" },
            Environment = new EnvironmentDefinition { Shared = { ["name"] = "from-environment" } },
            Collection = new Dictionary<string, string> { ["name"] = "from-collection" },
            Global = new Dictionary<string, string> { ["name"] = "from-global" },
        };

        var result = await RequestPreparer.PrepareAsync(request, scopes, Variables, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("https://api.example/from-request", result.Request!.Url.ToString());
    }

    [Fact]
    public async Task Reports_an_unresolved_variable_by_name_rather_than_sending_the_literal_token()
    {
        var request = Request("{{baseUrl}}/orders");

        var result = await RequestPreparer.PrepareAsync(request, new VariableScopes(), Variables, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("baseUrl", result.Error);
    }

    [Fact]
    public async Task Refuses_an_unfilled_path_parameter_rather_than_producing_a_wrong_url()
    {
        var request = Request(
            "https://api.example/articles/{slug}",
            pathParams: new Dictionary<string, string> { ["slug"] = string.Empty });

        var result = await RequestPreparer.PrepareAsync(request, new VariableScopes(), Variables, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("slug", result.Error);
    }

    [Fact]
    public async Task Reports_a_relative_url_as_not_absolute()
    {
        var request = Request("/orders");

        var result = await RequestPreparer.PrepareAsync(request, new VariableScopes(), Variables, ct: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("not an absolute URL", result.Error);
    }

    [Fact]
    public async Task Header_layers_apply_lowest_priority_first_and_the_request_always_wins()
    {
        var request = Request(
            "https://api.example/orders",
            headers: [new HeaderValue("X-Source", "request")]);

        var inherited = new List<HeaderValue>
        {
            new("Accept", "*/*"),
            new("X-Source", "inherited"),
        };

        var result = await RequestPreparer.PrepareAsync(request, new VariableScopes(), Variables, inherited, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var headers = result.Request!.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("*/*", headers["Accept"]);
        Assert.Equal("request", headers["X-Source"]);
    }

    [Fact]
    public async Task A_disabled_request_header_does_not_override_an_inherited_one()
    {
        var request = Request(
            "https://api.example/orders",
            headers: [new HeaderValue("Accept", "application/xml", Enabled: false)]);

        var inherited = new List<HeaderValue> { new("Accept", "*/*") };

        var result = await RequestPreparer.PrepareAsync(request, new VariableScopes(), Variables, inherited, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var accept = result.Request!.Headers.Single(h => h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("*/*", accept.Value);
    }

    [Fact]
    public async Task Query_parameters_are_appended_and_substituted()
    {
        var request = Request("https://api.example/orders");
        request.Query = [new QueryParameter("status", "{{status}}")];

        var scopes = new VariableScopes { Request = new Dictionary<string, string> { ["status"] = "open" } };

        var result = await RequestPreparer.PrepareAsync(request, scopes, Variables, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("status=open", result.Request!.Url.Query.TrimStart('?'));
    }

    [Fact]
    public async Task Settings_inherit_from_the_collection_and_the_request_still_wins()
    {
        var request = Request("https://api.example/orders");
        request.Settings = new RequestSettings { Retries = 3 };

        var collectionSettings = new RequestSettings { Retries = 0, TimeoutMilliseconds = 5000 };

        var result = await RequestPreparer.PrepareAsync(request, new VariableScopes(), Variables, collectionSettings: collectionSettings, ct: TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Request!.Settings.Retries);
        Assert.Equal(5000, result.Request!.Settings.TimeoutMilliseconds);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public string LocationDescription => "in-memory, for tests";

        public bool IsHardwareBacked => false;

        public ValueTask<string?> GetAsync(SecretKey key, CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask SetAsync(SecretKey key, string value, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAsync(SecretKey key, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<SecretKey>> ListAsync(string scope, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<SecretKey>>([]);
    }
}
