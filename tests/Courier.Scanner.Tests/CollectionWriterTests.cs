using Courier.Core.Abstractions;
using Courier.Core.Collections;

namespace Courier.Scanner.Tests;

/// <summary>
/// SEC-03 for a scan-derived environment: a secret variable's value must reach the local credential
/// store and nowhere else — never the written YAML, never a skipped-because-it-already-exists file.
/// </summary>
public sealed class CollectionWriterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("courier-collection-writer-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Secret_variable_value_reaches_the_store_and_never_the_file()
    {
        var result = ResultWith(new DerivedEnvironment(
            "Test",
            "https://api.example.com",
            "from Test.postman_environment.json",
            Variables: new Dictionary<string, string> { ["region"] = "us-east-1" },
            SecretVariables: new Dictionary<string, string> { ["apiKey"] = "super-secret-value" }));

        var store = new FakeSecretStore();

        var report = await CollectionWriter.WriteEnvironments(_root, result, store, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.EnvironmentsWritten);
        Assert.Equal(1, report.SecretsStored);
        Assert.Empty(report.SecretFailures);

        var path = Path.Combine(_root, CollectionFormat.EnvironmentsFolder, $"Test{CollectionFormat.EnvironmentFileExtension}");
        var rawText = File.ReadAllText(path);

        Assert.DoesNotContain("super-secret-value", rawText, StringComparison.Ordinal);

        var definition = new CollectionSerializer().DeserializeEnvironment(rawText);
        Assert.Equal("https://api.example.com", definition.Shared["baseUrl"]);
        Assert.Equal("us-east-1", definition.Shared["region"]);
        Assert.Contains("apiKey", definition.LocalNames);
        Assert.False(definition.Shared.ContainsKey("apiKey"));

        Assert.Single(store.Calls, c => c.Key == definition.SecretKeyFor("apiKey") && c.Value == "super-secret-value");
    }

    [Fact]
    public async Task A_secret_store_that_refuses_writes_is_reported_not_thrown()
    {
        var result = ResultWith(new DerivedEnvironment(
            "Test",
            "https://api.example.com",
            "from Test.postman_environment.json",
            SecretVariables: new Dictionary<string, string> { ["apiKey"] = "super-secret-value" }));

        var store = new FakeSecretStore(refuse: true);

        var report = await CollectionWriter.WriteEnvironments(_root, result, store, TestContext.Current.CancellationToken);

        Assert.Equal(0, report.SecretsStored);
        var failure = Assert.Single(report.SecretFailures);
        Assert.Equal("Test", failure.Environment);
        Assert.Equal("apiKey", failure.VariableName);

        // The name still round-trips even though the value could not be stored — same shape as any
        // other unfilled local variable, so the user can fill it in later via the normal editor.
        var path = Path.Combine(_root, CollectionFormat.EnvironmentsFolder, $"Test{CollectionFormat.EnvironmentFileExtension}");
        var definition = new CollectionSerializer().DeserializeEnvironment(File.ReadAllText(path));
        Assert.Contains("apiKey", definition.LocalNames);
    }

    [Fact]
    public async Task An_existing_environment_file_is_skipped_whole_no_secret_write_attempted()
    {
        var environmentsFolder = Path.Combine(_root, CollectionFormat.EnvironmentsFolder);
        Directory.CreateDirectory(environmentsFolder);
        var path = Path.Combine(environmentsFolder, $"Test{CollectionFormat.EnvironmentFileExtension}");
        File.WriteAllText(path, "courier: 1\nname: Test\nshared: {}\nlocalNames: []\n");

        var result = ResultWith(new DerivedEnvironment(
            "Test",
            "https://api.example.com",
            "from Test.postman_environment.json",
            SecretVariables: new Dictionary<string, string> { ["apiKey"] = "super-secret-value" }));

        var store = new FakeSecretStore();

        var report = await CollectionWriter.WriteEnvironments(_root, result, store, TestContext.Current.CancellationToken);

        Assert.Equal(0, report.EnvironmentsWritten);
        Assert.Equal(0, report.SecretsStored);
        Assert.Empty(store.Calls);
    }

    private static ScanResult ResultWith(DerivedEnvironment environment) => new()
    {
        Endpoints = [],
        Unresolved = [],
        Tier = ScanTier.Syntax,
        Environments = [environment],
    };

    private sealed class FakeSecretStore(bool refuse = false) : ISecretStore
    {
        public List<(SecretKey Key, string Value)> Calls { get; } = [];

        public string LocationDescription => "in-memory, for tests";

        public bool IsHardwareBacked => false;

        public ValueTask<string?> GetAsync(SecretKey key, CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask SetAsync(SecretKey key, string value, CancellationToken ct = default)
        {
            if (refuse)
            {
                throw new NotSupportedException($"The test store does not write secrets. Set {key} elsewhere instead.");
            }

            Calls.Add((key, value));
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(SecretKey key, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<SecretKey>> ListAsync(string scope, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<SecretKey>>([]);
    }
}
