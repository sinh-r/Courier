using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.App.Services;
using Courier.Core.Abstractions;
using Courier.Core.Collections;
using Courier.Core.Privacy;
using Courier.Core.Storage;

namespace Courier.App.ViewModels;

/// <summary>
/// The shell: rail, tabs, request and response panes, inspector, status bar.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private string _environmentName = "Local";

    [ObservableProperty]
    private bool _environmentHasSecrets;

    [ObservableProperty]
    private DialogKind _dialog = DialogKind.None;

    [ObservableProperty]
    private bool _isInspectorVisible = true;

    [ObservableProperty]
    private InspectorLayout _inspectorLayout = InspectorLayout.Stack;

    [ObservableProperty]
    private HistoryPlacement _historyPlacement = HistoryPlacement.Inspector;

    [ObservableProperty]
    private string _collectionSyncStatus = "no collection open";

    [ObservableProperty]
    private string? _clientCertificateSubject;

    [ObservableProperty]
    private string _proxyStatus = "proxy: system";

    [ObservableProperty]
    private int _historyCount;

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        Tabs = new TabCollection(services.DatabaseAsync);
        Tree = new CollectionTreeViewModel(services);
        Inspector = new InspectorViewModel(services);
        Palette = new CommandPaletteViewModel(this);
        Environments = new EnvironmentsViewModel();
        AuthProfileEditor = new AuthProfileEditorViewModel();
        Trust = new TrustSettingsViewModel();
        SyncReview = new SyncReviewViewModel();
        Telemetry = new TelemetryReconstructViewModel();

        CrashLog.Secrets = services.SecretRegistry;

        // One empty tab so the request pane has something to bind to on a first launch. Session
        // restore replaces this if there is a previous session; it runs after first paint, so the
        // window is never blank while the database opens.
        NewTab();

        // The status bar is a privacy instrument (UI_SPEC 4.1) and this is the claim it makes.
        // It updates from the egress gate's own records rather than from a flag someone could
        // forget to clear.
        services.Egress.Recorded += _ => OnPropertyChanged(nameof(NetworkSummary));
    }

    public TabCollection Tabs { get; }

    public CollectionTreeViewModel Tree { get; }

    public InspectorViewModel Inspector { get; }

    public CommandPaletteViewModel Palette { get; }

    public EnvironmentsViewModel Environments { get; }

    public AuthProfileEditorViewModel AuthProfileEditor { get; }

    public TrustSettingsViewModel Trust { get; }

    public SyncReviewViewModel SyncReview { get; }

    public TelemetryReconstructViewModel Telemetry { get; }

    public ObservableCollection<HistoryEntry> History { get; } = [];

    /// <summary>
    /// Every binding, listed. NFR-04 requires full keyboard operability, and a settings page that
    /// enumerates the bindings is how a user finds out that it is true.
    /// </summary>
    public IReadOnlyList<KeyBindingViewModel> KeyBindings { get; } =
    [
        new("Ctrl+K", "Command palette: endpoints, tabs, environments and commands"),
        new("Ctrl+N", "New request"),
        new("Ctrl+W", "Close the active tab"),
        new("Ctrl+Tab", "Next tab"),
        new("Ctrl+Shift+Tab", "Previous tab"),
        new("Ctrl+I", "Show or hide the inspector"),
        new("Ctrl+Enter", "Send the active request"),
        new("Ctrl+.", "Cancel the in-flight request"),
        new("Ctrl+F", "Search within the response"),
        new("Escape", "Dismiss the open dialog"),
    ];

    public AppServices Services => _services;

    /// <summary>
    /// The permanent leftmost slot. UI_SPEC 4.1: "That claim is the product's whole thesis; it
    /// should never scroll away."
    /// </summary>
    public string OfflineIndicator => "Offline · no sync";

    /// <summary>What the status bar's network slot shows once anything has been sent.</summary>
    public string NetworkSummary
    {
        get
        {
            var hosts = _services.Egress.Records
                .Where(r => r.WasAllowed)
                .Select(r => r.Host)
                .Distinct()
                .Count();

            return hosts switch
            {
                0 => "no connections this session",
                1 => "1 host contacted",
                _ => $"{hosts} hosts contacted",
            };
        }
    }

    public string HistoryCountLabel => $"{HistoryCount:N0} requests kept locally";

    public bool IsDialogOpen => Dialog != DialogKind.None;

    public bool IsScrimVisible => Dialog is not DialogKind.None and not DialogKind.FirstRun;

    [RelayCommand]
    public void OpenDialog(DialogKind kind) => Dialog = kind;

    [RelayCommand]
    public void CloseDialog() => Dialog = DialogKind.None;

    [RelayCommand]
    public void ToggleInspector() => IsInspectorVisible = !IsInspectorVisible;

    /// <summary>
    /// UI_SPEC 8 leaves open whether the inspector should be tabbed once four or more sections
    /// exist, and whether history belongs in the inspector or a fourth pane. The mock ships a
    /// toggle for both, so both are built and the user chooses.
    /// </summary>
    [RelayCommand]
    public void ToggleInspectorLayout() =>
        InspectorLayout = InspectorLayout == InspectorLayout.Stack ? InspectorLayout.Tabs : InspectorLayout.Stack;

    [RelayCommand]
    public void ToggleHistoryPlacement() =>
        HistoryPlacement = HistoryPlacement == HistoryPlacement.Inspector
            ? HistoryPlacement.BottomPane
            : HistoryPlacement.Inspector;

    [RelayCommand]
    public void NewTab() => Tabs.Open(new TabState
    {
        Title = "Untitled request",
        Method = "GET",
        EnvironmentName = EnvironmentName,
    });

    [RelayCommand]
    public void CloseActiveTab()
    {
        if (Tabs.Active is { } active)
        {
            Tabs.Close(active);
        }
    }

    [RelayCommand]
    public void OpenRequest(RequestDefinition request) =>
        Tabs.Open(TabState.FromDefinition(request, Tree.CollectionName, EnvironmentName));

    /// <summary>Deferred past first paint. PERF-01.</summary>
    public async Task LoadHistoryAsync()
    {
        var database = await _services.DatabaseAsync().ConfigureAwait(true);
        var store = new HistoryStore(database);

        HistoryCount = await store.CountAsync().ConfigureAwait(true);
        OnPropertyChanged(nameof(HistoryCountLabel));

        History.Clear();
        foreach (var entry in await store.QueryAsync(new HistoryQuery { Limit = 200 }).ConfigureAwait(true))
        {
            History.Add(entry);
        }
    }

    /// <summary>Deferred, and debounced thereafter. A git status on a cold image is not free.</summary>
    public Task RefreshGitStatusAsync() => Tree.RefreshGitStatusAsync();

    public Task RestoreSessionAsync() => Tabs.RestoreSessionAsync();

    /// <summary>
    /// The exportable statement a security reviewer signs off on. SEC-06, generated from the
    /// egress gate's own records so it cannot claim something the code does not do.
    /// </summary>
    public string RenderNetworkStatement() => NetworkStatement.Render(
        _services.Egress.Records,
        _services.EgressPolicy,
        _services.Version,
        DateTimeOffset.UtcNow);

    /// <summary>Every on-disk location, for the storage settings page. SEC-05.</summary>
    public IReadOnlyList<StorageLocation> StorageLocationsList => StorageLocations.Describe();

    public string SecretStoreDescription => _services.SecretStore.LocationDescription;

    partial void OnDialogChanged(DialogKind value)
    {
        OnPropertyChanged(nameof(IsDialogOpen));
        OnPropertyChanged(nameof(IsScrimVisible));
    }
}

/// <summary>The modal surfaces, one per mock state.</summary>
public enum DialogKind
{
    None,
    FirstRun,
    SyncFromCode,
    ExportCapsule,
    CapsuleDrop,
    ReconstructFromTelemetry,
    CommandPalette,
    TabOverflow,
    Environments,
    AuthProfile,
    TrustAndNetwork,
    Storage,
    Keyboard,
}

public enum InspectorLayout
{
    Stack,
    Tabs,
}

public enum HistoryPlacement
{
    Inspector,
    BottomPane,
}
