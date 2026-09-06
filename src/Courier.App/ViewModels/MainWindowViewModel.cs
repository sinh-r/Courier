using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.App.Services;
using Courier.Core.Abstractions;
using Courier.Core.Collections;
using Courier.Core.Http;
using Courier.Core.Privacy;
using Courier.Core.Storage;
using Courier.Core.Variables;

namespace Courier.App.ViewModels;

/// <summary>
/// The shell: rail, tabs, request and response panes, inspector, status bar.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _elapsedTimer;

    /// <summary>The window this shell is drawn in, set once the window opens. Needed for file pickers.</summary>
    public TopLevel? TopLevel { get; set; }

    [ObservableProperty]
    private string _environmentName = "Local";

    [ObservableProperty]
    private string _themeName = "System";

    [ObservableProperty]
    private CapsuleExportViewModel? _capsuleExport;

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

        // Drives the elapsed-time label beside Send while a request is in flight. 200ms is plenty
        // for a label a human is watching; it is not on any performance budget's critical path.
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _elapsedTimer.Tick += (_, _) => Tabs.Active?.TickElapsed();
        _elapsedTimer.Start();

        // Restores the last chosen theme. Setting the property (not the backing field) runs
        // OnThemeNameChanged, which is what actually applies it.
        ThemeName = AppSettingsStore.Load().Theme;
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
        new("Ctrl+S", "Save the active request"),
        new("Ctrl+O", "Open a collection folder"),
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

    /// <summary>
    /// Sends the active tab's request. CORE-01. Variable substitution runs first so the URL, headers
    /// and body that go on the wire match what CORE-04's hover already promised the user.
    /// </summary>
    [RelayCommand]
    public async Task SendAsync()
    {
        if (Tabs.Active is not { State: { } state } tab)
        {
            return;
        }

        var ct = tab.BeginSend();

        try
        {
            var scopes = new VariableScopes();

            var urlResult = await _services.Variables.SubstituteAsync(state.Url, scopes, ct).ConfigureAwait(true);
            if (!Uri.TryCreate(urlResult.Text, UriKind.Absolute, out var uri))
            {
                return;
            }

            var enabledQuery = state.Query.Where(q => q.Enabled && q.Name.Length > 0).ToList();
            if (enabledQuery.Count > 0)
            {
                var pairs = new List<string>();
                if (!string.IsNullOrEmpty(uri.Query))
                {
                    pairs.Add(uri.Query.TrimStart('?'));
                }

                foreach (var q in enabledQuery)
                {
                    var value = await _services.Variables.SubstituteAsync(q.Value, scopes, ct).ConfigureAwait(true);
                    pairs.Add($"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(value.Text)}");
                }

                uri = new UriBuilder(uri) { Query = string.Join("&", pairs) }.Uri;
            }

            var headers = new List<KeyValuePair<string, string>>();
            foreach (var h in state.Headers.Where(h => h.Enabled && h.Name.Length > 0))
            {
                var value = await _services.Variables.SubstituteAsync(h.Value, scopes, ct).ConfigureAwait(true);
                headers.Add(new KeyValuePair<string, string>(h.Name, value.Text));
            }

            byte[]? bodyBytes = null;
            string? contentType = null;

            if (state.BodyKind != BodyKind.None && !string.IsNullOrEmpty(state.BodyText))
            {
                var body = await _services.Variables.SubstituteAsync(state.BodyText, scopes, ct).ConfigureAwait(true);
                bodyBytes = System.Text.Encoding.UTF8.GetBytes(body.Text);
                contentType = new RequestBody { Kind = state.BodyKind }.ResolveContentType();
            }

            var prepared = new PreparedRequest
            {
                Method = string.IsNullOrWhiteSpace(state.Method) ? "GET" : state.Method,
                Url = uri,
                Headers = headers,
                BodyBytes = bodyBytes,
                ContentType = contentType,
                Settings = state.Settings,
                EnvironmentName = state.EnvironmentName,
            };

            var result = await _services.Executor.SendAsync(prepared, ct).ConfigureAwait(true);

            tab.Response?.Dispose();
            tab.Response = new ResponseViewModel(result);

            var database = await _services.DatabaseAsync().ConfigureAwait(true);
            await new HistoryStore(database)
                .RecordAsync(result, state.EnvironmentName, Tree.CollectionName, state.EndpointId ?? state.RequestPath, ct)
                .ConfigureAwait(true);

            HistoryCount++;
            OnPropertyChanged(nameof(HistoryCountLabel));
        }
        catch (OperationCanceledException)
        {
            // Cancel is a normal outcome, not a failure to report.
        }
        finally
        {
            tab.CompleteSend();
        }
    }

    /// <summary>Ctrl+. and the Cancel button. Cancelling an idle tab is a harmless no-op.</summary>
    [RelayCommand]
    public void CancelSend() => Tabs.Active?.CancelSend();

    /// <summary>
    /// Writes the active tab to disk as a request file. Ctrl+S. Nothing in the app could do this
    /// before — every edit lived only in the session's SQLite blob until now.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (Tabs.Active is not { State: { } state } tab || Tree.Folder is null)
        {
            return;
        }

        var definition = state.ToDefinition();
        var serializer = new CollectionSerializer();
        var yaml = serializer.SerializeRequest(definition);

        var path = state.RequestPath ?? Path.Combine(
            Tree.Folder,
            CollectionFormat.RequestsFolder,
            CollectionFormat.FileNameFor(definition));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, yaml).ConfigureAwait(true);

        state.RequestPath = path;
        state.IsDirty = false;
        tab.IsDirty = false;
    }

    /// <summary>Opens a folder of collections. The menu, the rail's "…" button and Ctrl+O all reach this.</summary>
    [RelayCommand]
    public async Task OpenFolderAsync()
    {
        if (TopLevel?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a collection folder",
            AllowMultiple = false,
        }).ConfigureAwait(true);

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        await Tree.OpenFolderAsync(path).ConfigureAwait(true);
        CollectionSyncStatus = $"{Tree.CollectionName} open";
    }

    /// <summary>
    /// Opens the export-capsule review for the active tab's last response. CAP-01, CAP-04. Building
    /// the view model here rather than in the dialog is what fixes the dialog rendering empty: it
    /// was never being constructed at all.
    /// </summary>
    [RelayCommand]
    public void OpenCapsuleExport()
    {
        if (Tabs.Active?.Response?.Result is not { } result)
        {
            return;
        }

        CapsuleExport = new CapsuleExportViewModel(
            _services.Redaction,
            result,
            EnvironmentName,
            result.TraceId,
            _services.Version);

        Dialog = DialogKind.ExportCapsule;
    }

    /// <summary>Light, Dark or System. Applied immediately and remembered for next launch.</summary>
    [RelayCommand]
    public void SetTheme(string preference)
    {
        ThemeName = preference;
        AppSettingsStore.Save(new AppSettings { Theme = preference });
    }

    partial void OnThemeNameChanged(string value)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = value switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

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
    Appearance,
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
