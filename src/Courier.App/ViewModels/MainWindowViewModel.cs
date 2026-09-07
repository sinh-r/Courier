using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.App.Services;
using Courier.Core.Abstractions;
using Courier.Core.Collections;
using Courier.Core.Export;
using Courier.Core.Http;
using Courier.Core.Import;
using Courier.Core.Privacy;
using Courier.Core.Storage;
using Courier.Core.Variables;
using Courier.Scanner;
using Courier.Scanner.Diffing;

namespace Courier.App.ViewModels;

/// <summary>
/// The shell: rail, tabs, request and response panes, inspector, status bar.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _elapsedTimer;
    private ScanResult? _lastScanResult;
    private IReadOnlyList<HeaderValue> _defaultHeaders = [];

    /// <summary>The window this shell is drawn in, set once the window opens. Needed for file pickers.</summary>
    public TopLevel? TopLevel { get; set; }

    /// <summary>
    /// "None", or a name in <see cref="AvailableEnvironments"/>. Two-way bound from the picker;
    /// <see cref="OnEnvironmentNameChanged"/> is what actually loads the environment.
    /// </summary>
    [ObservableProperty]
    private string _environmentName = "None";

    /// <summary>
    /// The loaded environment behind <see cref="EnvironmentName"/>, so CORE-04's resolution can
    /// reach <c>Shared</c> and, via <c>LocalNames</c>, the credential store. Null for "None".
    /// </summary>
    [ObservableProperty]
    private EnvironmentDefinition? _activeEnvironment;

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
        Environments = new EnvironmentsViewModel(services.SecretStore);
        Environments.EnvironmentCreated += name =>
        {
            RefreshAvailableEnvironments();
            EnvironmentName = name;
            Environments.Load(Tree.Folder, ActiveEnvironment);
        };
        Environments.EnvironmentSaved += definition =>
        {
            // Keeps ActiveEnvironment from going stale the instant a variable is saved — without
            // this, the next Load() (reopening the pane, or clicking to another settings section
            // and back) rebuilds the grid from the pre-save copy and a just-added row disappears.
            if (string.Equals(definition.Name, EnvironmentName, StringComparison.Ordinal))
            {
                ActiveEnvironment = definition;
                EnvironmentHasSecrets = definition.LocalNames.Count > 0;
            }
        };
        AuthProfileEditor = new AuthProfileEditorViewModel();
        Trust = new TrustSettingsViewModel();
        SyncReview = new SyncReviewViewModel();
        Telemetry = new TelemetryReconstructViewModel();

        _defaultHeaders = AppSettingsStore.Load().DefaultHeaders;
        DefaultHeadersEditor = new KeyValueEditorViewModel(rows =>
        {
            _defaultHeaders = [.. rows.Select(r => new HeaderValue(r.Name, r.Value, r.Enabled))];

            var settings = AppSettingsStore.Load();
            settings.DefaultHeaders = [.. _defaultHeaders];
            AppSettingsStore.Save(settings);

            OnPropertyChanged(nameof(InheritedHeadersForActiveTab));
        });
        DefaultHeadersEditor.Load(_defaultHeaders.Select(h => (h.Name, h.Value, h.Enabled, (string?)null)));

        // The Headers tab's "inherited" rows depend on which tab is active; this is what makes
        // switching tabs refresh them.
        Tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(TabCollection.Active))
            {
                OnPropertyChanged(nameof(InheritedHeadersForActiveTab));
            }
        };

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

    /// <summary>Environment names the open collection has, plus "None". Feeds the title-bar picker.</summary>
    public ObservableCollection<string> AvailableEnvironments { get; } = ["None"];

    /// <summary>Settings' "Default headers" grid. Accept/User-Agent by default; user-editable.</summary>
    public KeyValueEditorViewModel DefaultHeadersEditor { get; }

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
    public void OpenDialog(DialogKind kind)
    {
        Dialog = kind;

        if (kind == DialogKind.Environments)
        {
            Environments.Load(Tree.Folder, ActiveEnvironment);
        }
    }

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
    /// Parses a pasted curl command into a new tab. CORE-09. Reused by both entry points: the URL
    /// box's paste-intercept, and the "Paste curl from clipboard" menu item.
    /// </summary>
    public void ImportCurl(string command)
    {
        var request = CurlImporter.Parse(command);
        var state = TabState.FromDefinition(request, Tree.CollectionName, EnvironmentName);

        // The stripped -u password and a rejected --insecure have nowhere else to land — the
        // capsule banner is the one mechanism the tab already has for "this came from somewhere,
        // and here is what needs your attention".
        if (request.UnresolvedNotes.Count > 0)
        {
            state.Capsule = new CapsuleOrigin(
                FileName: "pasted curl",
                ExportedBy: null,
                ImportedUtc: DateTimeOffset.UtcNow,
                Resolved: [],
                Unresolved: request.UnresolvedNotes);
        }

        Tabs.Open(state);
    }

    /// <summary>The "Paste curl from clipboard" menu item — the entry point that needs no text box.</summary>
    [RelayCommand]
    public async Task ImportCurlFromClipboardAsync()
    {
        if (TopLevel?.Clipboard is not { } clipboard)
        {
            return;
        }

        var text = await clipboard.TryGetTextAsync().ConfigureAwait(true);

        if (!string.IsNullOrWhiteSpace(text))
        {
            ImportCurl(text);
        }
    }

    /// <summary>
    /// Copies the active tab as a runnable curl command. CORE-10. Never resolves variables — the
    /// recipient binds their own <c>{{name}}</c> references, same as every other exporter.
    /// </summary>
    [RelayCommand]
    public async Task CopyAsCurlAsync()
    {
        if (Tabs.Active?.State is not { } state || TopLevel?.Clipboard is not { } clipboard)
        {
            return;
        }

        var curl = RequestExporters.ToCurl(state.ToDefinition(), windowsLineContinuation: OperatingSystem.IsWindows());
        await clipboard.SetTextAsync(curl).ConfigureAwait(true);
    }

    /// <summary>
    /// Sends the active tab's request. CORE-01. Delegates the actual preparation to
    /// <see cref="RequestPreparer"/> — the same code <c>courier run</c> uses — rather than the
    /// empty-scopes, no-path-params, silently-abandoned version this used to be.
    /// </summary>
    [RelayCommand]
    public async Task SendAsync()
    {
        if (Tabs.Active is not { State: { } state } tab)
        {
            return;
        }

        tab.SendError = null;
        var ct = tab.BeginSend();

        try
        {
            var request = state.ToDefinition();

            var scopes = new VariableScopes
            {
                Environment = ActiveEnvironment,
                Collection = Tree.Definition?.Variables ?? new Dictionary<string, string>(StringComparer.Ordinal),
            };

            var inheritedHeaders = new Dictionary<string, HeaderValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in _defaultHeaders)
            {
                inheritedHeaders[header.Name] = header;
            }

            foreach (var header in Tree.Definition?.Headers ?? [])
            {
                inheritedHeaders[header.Name] = header;
            }

            var preparation = await RequestPreparer.PrepareAsync(
                request,
                scopes,
                _services.Variables,
                [.. inheritedHeaders.Values],
                Tree.Definition?.Settings,
                Tree.Definition?.InjectTraceParent ?? false,
                EnvironmentName == "None" ? null : EnvironmentName,
                ct).ConfigureAwait(true);

            if (!preparation.Succeeded)
            {
                tab.SendError = preparation.Error;
                return;
            }

            var result = await _services.Executor.SendAsync(preparation.Request!, ct).ConfigureAwait(true);

            tab.Response?.Dispose();
            tab.Response = new ResponseViewModel(result);

            var database = await _services.DatabaseAsync().ConfigureAwait(true);
            await new HistoryStore(database)
                .RecordAsync(result, EnvironmentName, Tree.CollectionName, state.EndpointId ?? state.RequestPath, ct)
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
        RefreshAvailableEnvironments();

        // Primes Environments with the open folder immediately, rather than waiting for Settings to
        // be opened once — the title-bar picker's inline "new environment" needs a folder to save
        // into the moment a collection opens, not only after a trip through the settings dialog.
        Environments.Load(Tree.Folder, ActiveEnvironment);
        Dialog = DialogKind.None;
    }

    /// <summary>
    /// Picks a folder or .sln to scan and runs the first scan against it. SCAN-01. The menu, the
    /// palette and the first-run screen all reach this, since none of them previously did anything
    /// at all — the dialog they opened had no way to start a scan.
    /// </summary>
    [RelayCommand]
    public async Task ImportFromCodeAsync()
    {
        if (TopLevel?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder or solution to scan",
            AllowMultiple = false,
        }).ConfigureAwait(true);

        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        SyncReview.SourcePath = path;
        SyncReview.OutputPath = Path.Combine(path, "collections", Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));
        Dialog = DialogKind.SyncFromCode;

        await RunScanAsync().ConfigureAwait(true);
    }

    /// <summary>The Browse button beside the source field, for correcting it before a rescan.</summary>
    [RelayCommand]
    public async Task BrowseScanSourceAsync()
    {
        if (TopLevel?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the folder or solution to scan",
            AllowMultiple = false,
        }).ConfigureAwait(true);

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            SyncReview.SourcePath = path;
        }
    }

    /// <summary>The Browse button beside the output field, for choosing where the collection lands.</summary>
    [RelayCommand]
    public async Task BrowseScanOutputAsync()
    {
        if (TopLevel?.StorageProvider is not { } storage)
        {
            return;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose where the collection is written",
            AllowMultiple = false,
        }).ConfigureAwait(true);

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            SyncReview.OutputPath = path;
        }
    }

    /// <summary>Re-runs the scan against whatever is currently in the source/output fields.</summary>
    [RelayCommand]
    public Task RescanAsync() => RunScanAsync();

    /// <summary>
    /// Scans, diffs against whatever collection already exists at the output path, and populates the
    /// review. <see cref="SolutionScanner.Scan"/> is synchronous and CPU-bound — walking a real
    /// codebase's worth of files is not free — so this is the one place in the app that reaches for
    /// <see cref="Task.Run"/> rather than an already-async I/O call.
    /// </summary>
    private async Task RunScanAsync()
    {
        var sourcePath = SyncReview.SourcePath;
        var outputPath = SyncReview.OutputPath;

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        SyncReview.IsScanning = true;

        try
        {
            var cache = string.IsNullOrWhiteSpace(outputPath) ? null : CollectionWriter.ReadCache(outputPath);

            var result = await Task.Run(
                () => _services.Scanner.Scan(sourcePath, cache?.Hashes, cache?.Endpoints))
                .ConfigureAwait(true);

            _lastScanResult = result;

            var current = result.Endpoints.Select(e => e.ToRequest("baseUrl")).ToList();
            var previous = LoadPreviousGenerated(outputPath);
            var changes = ScanDiff.Compare(previous, current);

            var collectionName = string.IsNullOrWhiteSpace(outputPath)
                ? Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar))
                : Path.GetFileName(outputPath.TrimEnd(Path.DirectorySeparatorChar));

            SyncReview.Load(changes, result, collectionName);
        }
        catch (DirectoryNotFoundException ex)
        {
            SyncReview.ErrorMessage = ex.Message;
        }
        finally
        {
            SyncReview.IsScanning = false;
        }
    }

    /// <summary>The previously generated set, for the diff. Empty on a first scan or a stale file.</summary>
    private static IReadOnlyList<RequestDefinition> LoadPreviousGenerated(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return [];
        }

        var path = Path.Combine(outputPath, CollectionFormat.GeneratedFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return new CollectionSerializer().DeserializeGenerated(File.ReadAllText(path)).Endpoints;
        }
        catch (CollectionFormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// Writes the generated file, creates the overlay if it does not exist yet, and opens the result
    /// in the tree. SCAN-09, P3: the overlay — the user's saved payloads — is never touched here.
    /// </summary>
    [RelayCommand]
    public async Task ApplyScanAsync()
    {
        if (_lastScanResult is not { } result || string.IsNullOrWhiteSpace(SyncReview.OutputPath))
        {
            return;
        }

        CollectionWriter.Write(SyncReview.OutputPath, result, "baseUrl");
        CollectionWriter.WriteEnvironments(SyncReview.OutputPath, result);
        CollectionWriter.WriteCollectionDefinition(
            SyncReview.OutputPath,
            Path.GetFileName(SyncReview.OutputPath.TrimEnd(Path.DirectorySeparatorChar)));
        EnsureDefaultEnvironment(SyncReview.OutputPath);

        await Tree.OpenFolderAsync(SyncReview.OutputPath).ConfigureAwait(true);
        CollectionSyncStatus = $"{Tree.CollectionName} open";
        RefreshAvailableEnvironments();
        Environments.Load(Tree.Folder, ActiveEnvironment);
        Dialog = DialogKind.None;
    }

    /// <summary>
    /// Scanned source with no <c>launchSettings.json</c>/<c>appsettings.*.json</c> the reader
    /// recognizes — the Conduit sample is exactly this — derives zero environments, and
    /// <see cref="CollectionWriter.WriteEnvironments"/> writes nothing for zero. Without this, that
    /// collection has no environment to select or edit until someone manually walks through
    /// Settings → Environments → "Create environment". A collection just imported from code should
    /// have at least one to put a <c>baseUrl</c> in.
    /// </summary>
    private static void EnsureDefaultEnvironment(string folder)
    {
        if (CollectionLoader.ListEnvironments(folder).Count > 0)
        {
            return;
        }

        CollectionLoader.SaveEnvironment(folder, new EnvironmentDefinition { Name = "Local" });
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

        // Load-modify-save: AppSettingsStore.Save replaces the whole file, so writing a fresh
        // AppSettings here would have silently reset the default headers back to the type's
        // defaults every time the user changed theme.
        var settings = AppSettingsStore.Load();
        settings.Theme = preference;
        AppSettingsStore.Save(settings);
    }

    /// <summary>
    /// Loads the picked environment. This is what the title-bar picker being a plain ComboBox two
    /// wired to a string still gets right: no separate "select" command needed, and switching tabs
    /// or reopening a collection can just set the property.
    /// </summary>
    partial void OnEnvironmentNameChanged(string value)
    {
        if (value == "None" || Tree.Folder is null)
        {
            ActiveEnvironment = null;
            EnvironmentHasSecrets = false;
            return;
        }

        ActiveEnvironment = CollectionLoader.LoadEnvironment(Tree.Folder, value);
        EnvironmentHasSecrets = ActiveEnvironment?.LocalNames.Count > 0;

        // Keeps the settings pane showing whichever environment is actually selected, if it
        // happens to be open while the title-bar picker switches — otherwise it would keep
        // rendering whatever was active when the pane was last opened.
        if (Dialog == DialogKind.Environments)
        {
            Environments.Load(Tree.Folder, ActiveEnvironment);
        }
    }

    /// <summary>
    /// Rebuilds the environment list from disk. Called after opening a folder and after applying a
    /// scan, since either can add environment files that were not there before.
    /// </summary>
    private void RefreshAvailableEnvironments()
    {
        AvailableEnvironments.Clear();
        AvailableEnvironments.Add("None");

        if (Tree.Folder is null)
        {
            return;
        }

        foreach (var name in CollectionLoader.ListEnvironments(Tree.Folder))
        {
            AvailableEnvironments.Add(name);
        }

        // A single environment is the common case for a freshly imported collection, and picking
        // it automatically is what lets Send work without an extra click nobody would expect to need.
        if (AvailableEnvironments.Count > 1 && EnvironmentName == "None")
        {
            EnvironmentName = AvailableEnvironments[1];
        }
    }

    /// <summary>
    /// App defaults and the collection's own headers that are not already overridden by the active
    /// tab's own headers — shown as muted rows above the editable Headers grid. P4: nothing reaches
    /// the wire that is not on screen, which this is the visible half of.
    /// </summary>
    public IReadOnlyList<InheritedHeaderRow> InheritedHeadersForActiveTab
    {
        get
        {
            if (Tabs.Active?.State is not { } state)
            {
                return [];
            }

            var requestNames = state.Headers
                .Where(h => h.Enabled)
                .Select(h => h.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var merged = new Dictionary<string, InheritedHeaderRow>(StringComparer.OrdinalIgnoreCase);

            foreach (var header in _defaultHeaders.Where(h => h.Enabled))
            {
                merged[header.Name] = new InheritedHeaderRow(header.Name, header.Value, "default");
            }

            foreach (var header in Tree.Definition?.Headers.Where(h => h.Enabled) ?? [])
            {
                merged[header.Name] = new InheritedHeaderRow(header.Name, header.Value, "collection");
            }

            return [.. merged.Values.Where(r => !requestNames.Contains(r.Name))];
        }
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

/// <param name="Origin">"default" or "collection" — where this header comes from, shown muted next to it.</param>
public sealed record InheritedHeaderRow(string Name, string Value, string Origin);

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
    DefaultHeaders,
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
