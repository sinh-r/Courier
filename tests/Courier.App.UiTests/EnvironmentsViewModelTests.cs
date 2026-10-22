using Courier.App.ViewModels;
using Courier.Core.Abstractions;
using Courier.Core.Collections;

namespace Courier.App.UiTests;

/// <summary>
/// Regression tests for the Environments "Editing" picker going blank or snapping to the wrong
/// environment after Save, Create, or a trip to another settings section. See
/// <see cref="ObservableListSyncTests"/> for the underlying Reset/selection mechanism.
/// </summary>
public sealed class EnvironmentsViewModelTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("courier-env-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private void WriteEnvironment(string name, string sharedValue = "https://api.example.com")
    {
        CollectionLoader.SaveEnvironment(_folder, new EnvironmentDefinition
        {
            Name = name,
            Shared = { ["baseUrl"] = sharedValue },
        });
    }

    [Fact]
    public async Task Save_keeps_the_picker_on_the_environment_just_saved()
    {
        WriteEnvironment("Local");
        WriteEnvironment("Staging");

        var vm = new EnvironmentsViewModel(new FakeSecretStore());
        vm.Load(_folder, CollectionLoader.LoadEnvironment(_folder, "Local"));

        vm.Variables[0].SharedValue = "https://api.local.example.com";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Local", vm.SelectedEnvironmentName);
        Assert.Equal(["Local", "Staging"], vm.AvailableEnvironments);
        Assert.True(vm.SaveStatus?.Succeeded);
    }

    [Fact]
    public void Create_keeps_the_picker_on_whatever_the_pane_was_already_editing()
    {
        WriteEnvironment("Local");

        var vm = new EnvironmentsViewModel(new FakeSecretStore());
        vm.Load(_folder, CollectionLoader.LoadEnvironment(_folder, "Local"));

        vm.NewEnvironmentName = "Staging";
        vm.NewEnvironmentCommand.Execute(null);

        Assert.Equal("Local", vm.SelectedEnvironmentName);
        Assert.Contains("Staging", vm.AvailableEnvironments);
    }

    [Fact]
    public void A_spurious_null_selection_snaps_back_to_the_open_environment()
    {
        WriteEnvironment("Local");

        var vm = new EnvironmentsViewModel(new FakeSecretStore());
        vm.Load(_folder, CollectionLoader.LoadEnvironment(_folder, "Local"));

        // What a Reset-driven two-way binding write-back looks like from the view model's side.
        vm.SelectedEnvironmentName = null;

        Assert.Equal("Local", vm.SelectedEnvironmentName);
    }

    [Fact]
    public void Reopen_with_unsaved_edits_leaves_the_pane_exactly_as_it_was()
    {
        WriteEnvironment("Local");
        WriteEnvironment("Staging");

        var vm = new EnvironmentsViewModel(new FakeSecretStore());
        vm.Load(_folder, CollectionLoader.LoadEnvironment(_folder, "Staging"));
        vm.Variables[0].SharedValue = "https://not-yet-saved.example.com";

        Assert.True(vm.IsDirty);

        // Simulates the settings nav re-entering this pane (e.g. after a trip to Auth profiles)
        // while the shell's own active environment is still "Local".
        vm.Reopen(_folder, CollectionLoader.LoadEnvironment(_folder, "Local"));

        Assert.Equal("Staging", vm.Name);
        Assert.True(vm.IsDirty);
        Assert.Equal("https://not-yet-saved.example.com", vm.Variables[0].SharedValue);
    }

    [Fact]
    public void Reopen_with_nothing_dirty_falls_back_to_the_shells_active_environment()
    {
        WriteEnvironment("Local");

        var vm = new EnvironmentsViewModel(new FakeSecretStore());

        vm.Reopen(_folder, CollectionLoader.LoadEnvironment(_folder, "Local"));

        Assert.Equal("Local", vm.Name);
    }

    [Fact]
    public async Task Global_mode_stores_a_local_value_under_the_global_secret_scope()
    {
        var secrets = new FakeSecretStore();
        var vm = new EnvironmentsViewModel(secrets, isGlobal: true);
        vm.Load(_folder, new EnvironmentDefinition { Name = EnvironmentsViewModel.GlobalsName });

        vm.AddVariableCommand.Execute(null);
        vm.Variables[0].Name = "apiKey";
        vm.Variables[0].SharedValue = "sk-test";
        vm.Variables[0].MoveToLocalCommand.Execute(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal([new SecretKey(SecretKey.GlobalScope, "Globals/apiKey")], secrets.Written);
        Assert.False(vm.AllowsMultipleEnvironments);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public string LocationDescription => "in-memory, for tests";

        public bool IsHardwareBacked => false;

        public ValueTask<string?> GetAsync(SecretKey key, CancellationToken ct = default) =>
            ValueTask.FromResult<string?>(null);

        public List<SecretKey> Written { get; } = [];

        public ValueTask SetAsync(SecretKey key, string value, CancellationToken ct = default)
        {
            Written.Add(key);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(SecretKey key, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<SecretKey>> ListAsync(string scope, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<SecretKey>>([]);
    }
}
