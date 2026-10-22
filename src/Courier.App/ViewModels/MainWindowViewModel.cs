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
using Courier.Core.Auth;
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
    private bool _revertingEnvironmentSelection;

    /// <summary>True while the picker is being pointed at a different collection's environment, so
    /// that switch is not mistaken for the user choosing a new environment for that collection.</summary>
    private bool _switchingCollection;

    /// <summary>The machine-wide global variables, as last saved. Every send falls back to these.</summary>
    private EnvironmentDefinition _globals;

    private ScanResult? _lastScanResult;
    private IReadOnlyList<HeaderValue> _defaultHeaders = [];

    /// <summary>The profile the last Send actually resolved to, so a "Sign in" retry after a
    /// <see cref="PendingSignInProfile"/> failure re-acquires with the exact same configuration
    /// rather than re-resolving (and possibly re-reading a since-changed auth choice).</summary>
    private AuthProfile? _lastAuthProfile;

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

    /// <summary>
    /// Non-null only right after a Send failed because auth needs an interactive sign-in (ENT-05).
    /// Drives the "Sign in" button next to the send error — the alternative, prompting the moment
    /// Send is clicked, is exactly the silent block ENT-05 forbids.
    /// </summary>
    [ObservableProperty]
    private string? _pendingSignInProfile;

    /// <summary>The tab <see cref="RequestCloseTab"/> is asking about, while
    /// <see cref="DialogKind.ConfirmCloseTab"/> is open. Null the rest of the time.</summary>
    [ObservableProperty]
    private TabViewModel? _pendingCloseTab;

    /// <summary>
    /// False for a Bearer or Basic profile that simply has no secret stored yet — signing in fixes
    /// nothing there; the fix is adding the secret in the auth profile editor.
    /// </summary>
    public bool CanSignInForPendingProfile =>
        PendingSignInProfile is not null
        && _lastAuthProfile?.Kind is AuthKind.EntraClientCredentials or AuthKind.EntraAuthorizationCode;

    partial void OnPendingSignInProfileChanged(string? value) => OnPropertyChanged(nameof(CanSignInForPendingProfile));

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        Tabs = new TabCollection(services.DatabaseAsync);
        Tree = new CollectionTreeViewModel(services)
        {
            // A collection reopened next launch comes back on the environment it was left on; one
            // opened for the first time starts on its first environment, so Send works without
            // an extra click nobody would expect to need.
            InitialEnvironmentFor = folder =>
                AppSettingsStore.Load().CollectionEnvironments.TryGetValue(folder, out var saved)
                    ? saved
                    : CollectionLoader.ListEnvironments(folder).FirstOrDefault() ?? "None",
        };
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CollectionTreeViewModel.Current))
            {
                OnCurrentCollectionChanged();
            }
        };
        Inspector = new InspectorViewModel(services);
        Palette = new CommandPaletteViewModel(this);
        _environments = EnvironmentsEditorFor(null);

        // The globals are one environment file outside any collection. A file that cannot be read
        // starts the table empty rather than taking the app down; the next Save rewrites it.
        _globals = LoadGlobals();
        GlobalVariables = new EnvironmentsViewModel(services.SecretStore, isGlobal: true);
        GlobalVariables.EnvironmentSaved += definition => _globals = definition;
        GlobalVariables.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EnvironmentsViewModel.SaveStatus) && GlobalVariables.SaveStatus is { } status)
            {
                CollectionSyncStatus = status.Message;
            }
        };
        AuthProfileEditor = new AuthProfileEditorViewModel(services);
        AuthProfileEditor.TokenAcquireFailed += message => AuthTokenError = message;
        RequestAuth = new AuthChoiceViewModel(services);
        RequestAuth.ManageProfilesRequested += () => OpenDialog(DialogKind.AuthProfile);
        RequestAuth.TokenAcquireFailed += message => AuthTokenError = message;
        CollectionAuth = new AuthChoiceViewModel(services, offersInherit: false);
        CollectionAuth.ManageProfilesRequested += () => OpenDialog(DialogKind.AuthProfile);
        CollectionAuth.TokenAcquireFailed += message => AuthTokenError = message;
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
        // switching tabs refresh them. The auth pair does the same for RequestAuth, on both sides
        // of the switch: PropertyChanging flushes whatever was being typed into the tab being left
        // (RequestAuth is a live editor, not a direct binding onto TabState the way Url or Headers
        // are, so nothing else would persist that edit) before PropertyChanged loads the tab being
        // entered.
        Tabs.PropertyChanging += async (_, e) =>
        {
            if (e.PropertyName == nameof(TabCollection.Active) && Tabs.Active?.State is { } leaving)
            {
                leaving.Auth = await RequestAuth.ToReferenceAsync().ConfigureAwait(true);
            }
        };

        // Picking or opening any tab puts the request panes back in front of the collection page.
        Tabs.Chosen += () => CollectionOverview = null;

        Tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(TabCollection.Active))
            {
                // A tab belongs to a collection; making it active makes that collection current,
                // which is what points the environment picker at the environment it sends with.
                // A tab of a closed collection still sends with it, but the tree cannot point at
                // what it no longer shows, so the current collection stays put.
                if (CollectionFor(Tabs.Active?.State) is { } collection && Tree.Collections.Contains(collection))
                {
                    Tree.Current = collection;
                }

                OnPropertyChanged(nameof(InheritedHeadersForActiveTab));
                OnPropertyChanged(nameof(CollectionAuthSummary));
                _ = RequestAuth.LoadAsync(Tabs.Active?.State?.Auth, CollectionFor(Tabs.Active?.State)?.Folder);
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

    /// <summary>
    /// The current collection's environments — Settings, the title-bar picker's inline create, and
    /// the collection page's Environments tab all edit through this one. Each collection folder has
    /// its own editor (see <see cref="_environmentEditors"/>), swapped in when it becomes current,
    /// so unsaved edits in one collection survive switching to another and back.
    /// </summary>
    [ObservableProperty]
    private EnvironmentsViewModel _environments;

    /// <summary>One editor per collection folder, kept after a collection is closed too, so edits
    /// not yet saved are still there if it is reopened this session.</summary>
    private readonly Dictionary<string, EnvironmentsViewModel> _environmentEditors = new(OpenCollection.FolderComparer);

    /// <summary>The editor for <paramref name="folder"/>, created and loaded the first time it is
    /// needed. Null folder (nothing open) gets one shared, empty editor.</summary>
    private EnvironmentsViewModel EnvironmentsEditorFor(string? folder)
    {
        if (folder is null)
        {
            return _noCollectionEnvironments ??= CreateEnvironmentsEditor();
        }

        if (!_environmentEditors.TryGetValue(folder, out var editor))
        {
            editor = CreateEnvironmentsEditor();
            _environmentEditors[folder] = editor;
        }

        return editor;
    }

    private EnvironmentsViewModel? _noCollectionEnvironments;

    /// <summary>A collection's environments editor, wired to the shell. Every handler checks it is
    /// still the visible one: an editor for a collection that is not current must not repoint the
    /// picker or overwrite the loaded environment.</summary>
    private EnvironmentsViewModel CreateEnvironmentsEditor()
    {
        var editor = new EnvironmentsViewModel(_services.SecretStore);

        editor.EnvironmentCreated += name =>
        {
            if (!ReferenceEquals(editor, Environments))
            {
                return;
            }

            // Setting EnvironmentName runs OnEnvironmentNameChanged, which resolves
            // ActiveEnvironment. The explicit Load below keeps the pane's own idea of "what am I
            // editing" in sync with the newly created environment even while Settings is closed,
            // so a later Reopen (see OpenDialog) finds the right name to reload.
            RefreshAvailableEnvironments();
            EnvironmentName = name;
            editor.Load(Tree.Folder, ActiveEnvironment);

            if (CollectionOverview is { } overview && Tree.Folder is { } folder
                && OpenCollection.IsSameFolder(overview.Folder, folder))
            {
                overview.Environments = [.. AvailableEnvironments.Where(e => e != "None")];
            }
        };

        editor.EnvironmentSaved += definition =>
        {
            // Keeps ActiveEnvironment from going stale the instant a variable is saved — without
            // this, the next Load() (reopening the pane, or clicking to another settings section
            // and back) rebuilds the grid from the pre-save copy and a just-added row disappears.
            if (ReferenceEquals(editor, Environments)
                && string.Equals(definition.Name, EnvironmentName, StringComparison.Ordinal))
            {
                ActiveEnvironment = definition;
                EnvironmentHasSecrets = definition.LocalNames.Count > 0;
            }
        };

        editor.PropertyChanged += (_, e) =>
        {
            // Mirrored into the status bar so the confirmation (or the reason a save did not
            // happen) is still visible after the dialog closes, not only while it is open.
            if (e.PropertyName == nameof(EnvironmentsViewModel.SaveStatus) && editor.SaveStatus is { } status)
            {
                CollectionSyncStatus = status.Message;
            }
        };

        return editor;
    }

    /// <summary>The global variables: available to every collection, overridden by any collection
    /// variable of the same name, and reachable regardless as <c>{{global.name}}</c>.</summary>
    public EnvironmentsViewModel GlobalVariables { get; }

    public AuthProfileEditorViewModel AuthProfileEditor { get; }

    /// <summary>The active tab's auth choice. Bound from both the request tab's Auth panel and the
    /// inspector's Auth section, so the two always show the same thing.</summary>
    public AuthChoiceViewModel RequestAuth { get; }

    /// <summary>The open collection's default auth — a request left on "Inherit" resolves to this.
    /// A second, independent <see cref="AuthChoiceViewModel"/>, loaded and saved against
    /// <see cref="CollectionTreeViewModel.Definition"/> instead of a request's own state.</summary>
    public AuthChoiceViewModel CollectionAuth { get; }

    [ObservableProperty]
    private string? _collectionAuthStatus;

    /// <summary>Non-null while the collection's own page (Overview, Auth) is shown in place of the
    /// request and response panes — what clicking the collection root in the tree opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollectionOverviewOpen))]
    [NotifyPropertyChangedFor(nameof(IsRequestWorkspaceVisible))]
    private CollectionOverviewViewModel? _collectionOverview;

    public bool IsCollectionOverviewOpen => CollectionOverview is not null;

    public bool IsRequestWorkspaceVisible => CollectionOverview is null;

    /// <summary>What a request left on "Inherit from collection" resolves to, for its Auth tab.</summary>
    public string CollectionAuthSummary => CollectionFor(Tabs.Active?.State) is { } collection
        ? CollectionOverviewViewModel.Describe(collection.Definition.Auth)
        : "no collection open";

    /// <summary>Opens the current collection's page — Overview by default, or straight to its Auth
    /// tab from a request's "Edit collection auth". Reloads <see cref="CollectionAuth"/> each time,
    /// so it always starts from what <c>collection.yaml</c> actually says.</summary>
    [RelayCommand]
    public void OpenCollectionOverview(int tab = CollectionOverviewViewModel.OverviewTab)
    {
        if (Tree.Current is not { } collection)
        {
            return;
        }

        // The Environments tab edits whichever collection the page is showing, and keeps its own
        // unsaved edits if it was already on this one.
        Environments.Reopen(collection.Folder, ActiveEnvironment);
        Environments.RefreshDetectedProfiles(collection.Definition.ScannedFrom?.Path);

        // Already showing this collection: just switch pages, rather than rebuilding and dropping
        // a description or auth edit that has not been saved yet.
        if (CollectionOverview is { } open && OpenCollection.IsSameFolder(open.Folder, collection.Folder))
        {
            open.SelectedTabIndex = tab;
            return;
        }

        CollectionOverview = new CollectionOverviewViewModel(
            collection.Folder,
            collection.Definition,
            collection.Root,
            [.. AvailableEnvironments.Where(e => e != "None")])
        {
            SelectedTabIndex = tab,
        };

        _ = CollectionAuth.LoadAsync(collection.Definition.Auth, collection.Folder);
        CollectionAuthStatus = null;
    }

    [RelayCommand]
    public void OpenCollectionAuth() => OpenCollectionOverview(CollectionOverviewViewModel.AuthTab);

    /// <summary>The tree's "Environments…" on a collection root: its page, on the Environments tab.</summary>
    [RelayCommand]
    public void ManageCollectionEnvironments(TreeNode? node)
    {
        if (node?.Collection is { } collection)
        {
            Tree.Current = collection;
        }

        OpenCollectionOverview(CollectionOverviewViewModel.EnvironmentsTab);
    }

    /// <summary>The tree's "Close collection": takes it out of the workspace, leaves its files
    /// alone, and stops reopening it next launch.</summary>
    [RelayCommand]
    public void CloseCollection(TreeNode? node)
    {
        if (node?.Collection is not { } collection)
        {
            return;
        }

        if (CollectionOverview is { } overview && OpenCollection.IsSameFolder(overview.Folder, collection.Folder))
        {
            CollectionOverview = null;
        }

        Tree.Close(collection);
        RememberOpenCollections();

        // Its open tabs keep sending with it — same definition, same environment — just from here
        // instead of the tree.
        _detachedCollections[collection.Folder] = collection;
        CollectionSyncStatus = $"{collection.Name} closed";
    }

    [RelayCommand]
    public void CloseCollectionOverview() => CollectionOverview = null;

    /// <summary>The collection page's Save: its description and default auth, in one
    /// <c>collection.yaml</c> write — to the collection the page is showing, which is not
    /// necessarily the current one by the time Save is clicked.</summary>
    [RelayCommand]
    public async Task SaveCollectionOverviewAsync()
    {
        if (CollectionOverview is not { } overview)
        {
            return;
        }

        // The tree's copy when the collection is still open: a save or "New folder" since the page
        // opened reloads it, and writing the page's older snapshot back would undo that.
        var definition = Tree.Find(overview.Folder)?.Definition ?? overview.Definition;

        definition.Description = string.IsNullOrWhiteSpace(overview.Description) ? null : overview.Description;
        definition.Auth = await CollectionAuth.ToReferenceAsync().ConfigureAwait(true);
        CollectionLoader.SaveCollectionDefinition(overview.Folder, definition);

        overview.MarkSaved(definition.Auth);
        overview.Status = "Saved to collection.yaml";
        CollectionSyncStatus = $"Saved {overview.Name}";
        OnPropertyChanged(nameof(CollectionAuthSummary));
    }

    /// <summary>Non-null puts <see cref="Views.Dialogs.AuthTokenErrorView"/> on screen, layered over
    /// whatever else is open — a "Get token" failure needs to reach the user even while the Settings
    /// dialog that triggered it stays open, which the single-value <see cref="Dialog"/> state machine
    /// cannot do without replacing that dialog.</summary>
    [ObservableProperty]
    private string? _authTokenError;

    [RelayCommand]
    public void DismissAuthTokenError() => AuthTokenError = null;

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
        new("Ctrl+Shift+S", "Save every open, edited request"),
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
            // Reopen, not Load: this fires on every settings-nav click back to Environments, not
            // only the first time the dialog opens, and Load unconditionally rebuilds the grid from
            // ActiveEnvironment — which is whichever environment the title bar is sending requests
            // with, not necessarily the one this pane had open. Reopen keeps the pane (and any
            // unsaved edits) put unless it genuinely has nothing open yet.
            Environments.Reopen(Tree.Folder, ActiveEnvironment);
            Environments.RefreshDetectedProfiles(Tree.Definition?.ScannedFrom?.Path);
        }

        if (kind == DialogKind.GlobalVariables)
        {
            GlobalVariables.Reopen(StorageLocations.Globals, _globals);
        }

        if (kind == DialogKind.AuthProfile)
        {
            AuthProfileEditor.Load(Tree.Folder, Tree.Definition?.Auth);
            _ = CollectionAuth.LoadAsync(Tree.Definition?.Auth, Tree.Folder);
            CollectionAuthStatus = null;
        }
    }

    /// <summary>Persists <see cref="CollectionAuth"/>'s current choice as the collection's default —
    /// the same <c>collection.yaml</c> write <see cref="AuthProfileEditorViewModel.SetAsCollectionDefault"/>
    /// makes for a saved profile, but for a reference that may be typed inline instead of named.</summary>
    [RelayCommand]
    public async Task SaveCollectionAuthAsync()
    {
        if (Tree.Folder is not { } folder || Tree.Definition is not { } definition)
        {
            CollectionAuthStatus = "Open a collection first.";
            return;
        }

        definition.Auth = await CollectionAuth.ToReferenceAsync().ConfigureAwait(true);
        CollectionLoader.SaveCollectionDefinition(folder, definition);
        CollectionAuthStatus = "Collection default auth saved";

        if (CollectionOverview is { } overview)
        {
            overview.AuthSummary = CollectionOverviewViewModel.Describe(definition.Auth);
        }

        OnPropertyChanged(nameof(CollectionAuthSummary));
    }

    [RelayCommand]
    public void CloseDialog()
    {
        Dialog = DialogKind.None;
        PendingCloseTab = null;

        // A profile created, renamed or deleted in Settings should show up in the request tab's
        // picker the moment that dialog closes, not only after switching tabs.
        _ = RequestAuth.LoadAsync(Tabs.Active?.State?.Auth, CollectionFor(Tabs.Active?.State)?.Folder);
    }

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
    public void NewTab() => OpenNewRequestTab(folder: null);

    /// <summary>The tree's "New request" context-menu item, on a Collection or Folder node — a
    /// Request node has no <see cref="TreeNodeKind.Folder"/> children to scope one to.</summary>
    [RelayCommand]
    public void NewRequestInFolder(TreeNode? node)
    {
        if (node?.Collection is { } collection)
        {
            Tree.Current = collection;
        }

        OpenNewRequestTab(node?.Kind == TreeNodeKind.Folder ? node.FolderPath : null);
    }

    /// <summary>
    /// Shared by the generic "+"/"New request" (unscoped) and the tree's folder-scoped one. No
    /// naming prompt: the user types a Name (and, if they want, a Folder) right in the tab the same
    /// way they'd edit anything else, then Ctrl+S actually saves it.
    /// </summary>
    private void OpenNewRequestTab(string? folder) => Tabs.Open(new TabState
    {
        Title = "Untitled request",
        Method = "GET",
        EnvironmentName = EnvironmentName,
        CollectionName = Tree.CollectionName,
        CollectionFolder = Tree.Folder,
        Folder = folder,
    });

    [RelayCommand]
    public void CloseActiveTab()
    {
        if (Tabs.Active is { } active)
        {
            RequestCloseTab(active);
        }
    }

    /// <summary>
    /// Closes a tab, unless it has unsaved edits — every close path (the × button, middle-click,
    /// Ctrl+W, the palette's "Close tab") reaches this rather than calling <see cref="TabCollection.Close"/>
    /// directly, so none of them can silently drop an edit the way closing always used to.
    /// </summary>
    public void RequestCloseTab(TabViewModel tab)
    {
        if (!tab.IsDirty)
        {
            Tabs.Close(tab);
            return;
        }

        PendingCloseTab = tab;
        Dialog = DialogKind.ConfirmCloseTab;
    }

    /// <summary>"Save and close" on the confirm dialog.</summary>
    [RelayCommand]
    public async Task ConfirmCloseTabSaveAsync()
    {
        if (PendingCloseTab is not { } tab)
        {
            return;
        }

        if (await SaveTabAsync(tab).ConfigureAwait(true) && Tree.Find(tab.State?.CollectionFolder) is { } collection)
        {
            await Tree.OpenFolderAsync(collection.Folder).ConfigureAwait(true);
        }

        Tabs.Close(tab);
        PendingCloseTab = null;
        Dialog = DialogKind.None;
    }

    /// <summary>"Don't save" on the confirm dialog — closes and discards the edits.</summary>
    [RelayCommand]
    public void ConfirmCloseTabDiscard()
    {
        if (PendingCloseTab is { } tab)
        {
            Tabs.Close(tab);
        }

        PendingCloseTab = null;
        Dialog = DialogKind.None;
    }

    /// <summary>Opens a request in a tab that remembers which collection it came from — the tree
    /// passes the node's collection, so a request always sends with its own collection's variables.</summary>
    public void OpenRequest(RequestDefinition request, OpenCollection? collection = null)
    {
        collection ??= Tree.Current;

        if (collection is not null)
        {
            Tree.Current = collection;
        }

        var state = TabState.FromDefinition(request, collection?.Name, collection?.EnvironmentName ?? EnvironmentName);
        state.CollectionFolder = collection?.Folder;
        Tabs.Open(state);
    }

    /// <summary>
    /// Parses a pasted curl command into a new tab. CORE-09. Reused by both entry points: the URL
    /// box's paste-intercept, and the "Paste curl from clipboard" menu item.
    /// </summary>
    public void ImportCurl(string command)
    {
        var request = CurlImporter.Parse(command);
        var state = TabState.FromDefinition(request, Tree.CollectionName, EnvironmentName);
        state.CollectionFolder = Tree.Folder;

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
            // Committed first so Send always reflects whatever is currently selected in the Auth
            // panel, exactly like every other field on the tab.
            state.Auth = await RequestAuth.ToReferenceAsync(ct).ConfigureAwait(true);

            var request = state.ToDefinition();

            // The tab's own collection, which is not necessarily the one selected in the tree:
            // with several open, a request must never borrow another collection's baseUrl.
            var collection = CollectionFor(state);
            var environment = EnvironmentFor(collection);
            var scopes = new VariableScopes
            {
                Environment = environment,
                Collection = collection?.Definition.Variables ?? new Dictionary<string, string>(StringComparer.Ordinal),
                GlobalEnvironment = _globals,
                EnvironmentLookup = collection is null
                    ? null
                    : name => LoadEnvironmentOrNull(collection.Folder, name),
            };

            var inheritedHeaders = new Dictionary<string, HeaderValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in _defaultHeaders)
            {
                inheritedHeaders[header.Name] = header;
            }

            foreach (var header in collection?.Definition.Headers ?? [])
            {
                inheritedHeaders[header.Name] = header;
            }

            var resolved = AuthResolver.Resolve(
                request.Auth,
                collection?.Definition.Auth,
                name => collection is null ? null : AuthProfileStore.Load(collection.Folder, name));

            _lastAuthProfile = resolved.Profile;

            var preparation = await RequestPreparer.PrepareAsync(
                request,
                scopes,
                _services.Variables,
                inheritedHeaders: [.. inheritedHeaders.Values],
                collectionSettings: collection?.Definition.Settings,
                injectTraceParent: collection?.Definition.InjectTraceParent ?? false,
                environmentName: environment?.Name,
                auth: new AuthPlan(resolved, _services.AuthRegistry),
                ct: ct).ConfigureAwait(true);

            if (!preparation.Succeeded)
            {
                tab.SendError = preparation.Error;
                PendingSignInProfile = preparation.SignInProfile;
                return;
            }

            PendingSignInProfile = null;

            var result = await _services.Executor.SendAsync(preparation.Request!, ct).ConfigureAwait(true);

            tab.Response?.Dispose();
            tab.Response = new ResponseViewModel(result);

            Inspector.Profile = resolved.Profile;
            Inspector.ShowToken(
                preparation.Request!.Auth?.Token,
                preparation.Request.Auth?.Identity ?? resolved.Profile?.Username);

            var database = await _services.DatabaseAsync().ConfigureAwait(true);
            await new HistoryStore(database)
                .RecordAsync(
                    result,
                    environment?.Name ?? "None",
                    collection?.Name,
                    state.EndpointId ?? state.RequestPath,
                    preparation.Request.SecretValues,
                    ct)
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
    /// The "Sign in" button beside a send error that named <see cref="PendingSignInProfile"/>.
    /// ENT-05: the prompt only happens here, after the user has explicitly asked for it — never as
    /// a side effect of the Send button itself.
    /// </summary>
    [RelayCommand]
    public async Task SignInAndRetryAsync()
    {
        if (_lastAuthProfile is not { Kind: AuthKind.EntraClientCredentials or AuthKind.EntraAuthorizationCode } profile
            || Tabs.Active is not { } tab)
        {
            return;
        }

        tab.SendError = null;
        var ct = tab.BeginSend();

        try
        {
            await _services.Entra.AcquireAsync(profile, true, ct).ConfigureAwait(true);
            PendingSignInProfile = null;
        }
        catch (InteractiveAuthRequiredException ex)
        {
            tab.SendError = ex.Message;
            return;
        }
        finally
        {
            tab.CompleteSend();
        }

        await SendAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Writes the active tab to disk as a request file. Ctrl+S, the Save button next to Send, and
    /// the hamburger menu all reach this.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (Tabs.Active is not { } tab || !await SaveTabAsync(tab).ConfigureAwait(true))
        {
            return;
        }

        if (Tree.Find(tab.State?.CollectionFolder) is { } collection)
        {
            await Tree.OpenFolderAsync(collection.Folder).ConfigureAwait(true);
        }

        CollectionSyncStatus = $"Saved '{tab.Title}'";
    }

    /// <summary>
    /// Writes every dirty tab to disk in one action — the "save the collection" ask this whole
    /// command exists for, rather than clicking through each tab in turn.
    /// </summary>
    [RelayCommand]
    public async Task SaveAllAsync()
    {
        var dirty = Tabs.Items.Where(t => t.IsDirty).ToList();

        if (dirty.Count == 0)
        {
            CollectionSyncStatus = "Nothing to save";
            return;
        }

        var saved = 0;
        foreach (var tab in dirty)
        {
            if (await SaveTabAsync(tab).ConfigureAwait(true))
            {
                saved++;
            }
        }

        // Once per collection touched, not once per tab — the tree only needs to catch up on the
        // final state, and reopening it after every single write would be N times the disk I/O for
        // no benefit anyone would see.
        var touched = dirty
            .Where(t => !t.IsDirty)
            .Select(t => Tree.Find(t.State?.CollectionFolder))
            .OfType<OpenCollection>()
            .Distinct()
            .ToList();

        foreach (var collection in touched)
        {
            await Tree.OpenFolderAsync(collection.Folder).ConfigureAwait(true);
        }

        CollectionSyncStatus = saved == dirty.Count
            ? $"Saved {saved} request{(saved == 1 ? string.Empty : "s")}"
            : $"Saved {saved} of {dirty.Count} requests — open a collection folder to save the rest";
    }

    /// <summary>
    /// Writes one tab to disk as a request file. Shared by <see cref="SaveAsync"/>,
    /// <see cref="SaveAllAsync"/> and the close-tab confirmation's "Save and close".
    /// </summary>
    /// <returns>False when there is no collection folder to save into — the tab is left dirty.</returns>
    private async Task<bool> SaveTabAsync(TabViewModel tab)
    {
        // The tab's own collection, open or not. A tab that never belonged to one (a fresh "+"
        // before anything was open) lands in the current collection and belongs to it from then on.
        var folder = CollectionFor(tab.State)?.Folder;

        if (tab.State is not { } state || folder is null)
        {
            return false;
        }

        // Re-pointing a request at a different collection would be a move, not a save; a tab
        // keeps the collection it was opened in, and only an unowned one adopts one here.
        state.CollectionFolder ??= folder;
        state.CollectionName ??= Path.GetFileName(folder);

        // RequestAuth's fields reflect whichever tab is active; flushing it into a different,
        // merely-dirty tab's state would overwrite that tab's auth with the active tab's edits.
        // Every non-active tab already has an up-to-date state.Auth from the flush-on-tab-switch
        // wired into Tabs.PropertyChanging.
        if (ReferenceEquals(tab, Tabs.Active))
        {
            state.Auth = await RequestAuth.ToReferenceAsync().ConfigureAwait(true);
        }

        var definition = state.ToDefinition();
        var serializer = new CollectionSerializer();
        var yaml = serializer.SerializeRequest(definition);

        // Nested under the request's own Folder (root when unset — Path.Combine ignores an empty
        // segment cleanly), so the file layout a human browsing the git repo sees matches what the
        // tree already shows, not just what the YAML's own folder: field says.
        var path = state.RequestPath ?? Path.Combine(
            folder,
            CollectionFormat.RequestsFolder,
            state.Folder ?? string.Empty,
            CollectionFormat.FileNameFor(definition));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, yaml).ConfigureAwait(true);

        state.RequestPath = path;
        state.IsDirty = false;
        tab.IsDirty = false;

        return true;
    }

    /// <summary>Opens a collection folder next to the ones already open. The menu, the rail's "…"
    /// button and Ctrl+O all reach this.</summary>
    [RelayCommand]
    public async Task OpenFolderAsync()
    {
        if (await PickFolderAsync("Open a collection folder").ConfigureAwait(true) is not { } path)
        {
            return;
        }

        await OpenAndPrimeFolderAsync(path).ConfigureAwait(true);
    }

    /// <summary>
    /// Hand-builds a collection from nothing: pick or create an empty folder, write a
    /// <c>collection.yaml</c> naming it (harmless no-op if one already exists — e.g. the folder was
    /// already scanned into), and open it. The native picker already has its own "New folder"
    /// button, so this needs no dialog of its own. The menu and the palette reach this.
    /// </summary>
    [RelayCommand]
    public async Task NewCollectionAsync()
    {
        if (await PickFolderAsync("Choose or create a folder for the new collection").ConfigureAwait(true)
            is not { } path)
        {
            return;
        }

        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        CollectionWriter.WriteCollectionDefinition(path, name);

        await OpenAndPrimeFolderAsync(path).ConfigureAwait(true);

        // Auth is one of the three places your process notes call out: the request itself, the
        // collection default, or a saved profile set up separately — all optional, so this only
        // points at where to go, rather than opening the dialog and asking for it up front.
        CollectionSyncStatus = $"{name} created — click it in the tree to add a description or auth";
    }

    /// <summary>The folder picker both <see cref="OpenFolderAsync"/> and <see cref="NewCollectionAsync"/> use.</summary>
    private async Task<string?> PickFolderAsync(string title)
    {
        if (TopLevel?.StorageProvider is not { } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(true);

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>Opens a folder into the tree and primes every other pane that depends on which
    /// folder is active — the shared tail of <see cref="OpenFolderAsync"/> and
    /// <see cref="NewCollectionAsync"/>.</summary>
    private async Task OpenAndPrimeFolderAsync(string path)
    {
        // Opened alongside every collection already in the tree, never in place of them; opening
        // one that is already there just reloads it. Becoming current runs OnCurrentCollectionChanged,
        // which points the environment picker and the Environments pane at it.
        var collection = await Tree.OpenFolderAsync(path).ConfigureAwait(true);
        CollectionSyncStatus = $"{collection.Name} open";

        // Back in the tree, so its tabs read the tree's fresh copy rather than the one kept for them.
        _detachedCollections.Remove(collection.Folder);

        RememberOpenCollections();

        // A scan or a first open can add environment files the list did not have; a collection
        // still on "None" with environments now available picks the first one.
        RefreshAvailableEnvironments(pickFirstIfNone: true);

        // Primes Environments with the open folder immediately, rather than waiting for Settings to
        // be opened once — the title-bar picker's inline "new environment" needs a folder to save
        // into the moment a collection opens, not only after a trip through the settings dialog.
        // Reopen, not Load: this also runs when a save reloads an already-open collection, and
        // must not throw away environment edits that have not been saved yet.
        Environments.Reopen(Tree.Folder, ActiveEnvironment);
        Environments.RefreshDetectedProfiles(Tree.Definition?.ScannedFrom?.Path);
        _ = RequestAuth.LoadAsync(Tabs.Active?.State?.Auth, CollectionFor(Tabs.Active?.State)?.Folder);

        // A page showing a different collection has nothing to say about this one.
        if (CollectionOverview is { } overview && !OpenCollection.IsSameFolder(overview.Folder, collection.Folder))
        {
            CollectionOverview = null;
        }

        OnPropertyChanged(nameof(CollectionAuthSummary));
        OnPropertyChanged(nameof(InheritedHeadersForActiveTab));
        Dialog = DialogKind.None;
    }

    /// <summary>Remembers which collections are open, so the next launch reopens all of them.
    /// Load-modify-save, as <see cref="SetTheme"/>.</summary>
    private void RememberOpenCollections()
    {
        var settings = AppSettingsStore.Load();
        settings.OpenCollectionFolders = [.. Tree.Collections.Select(c => c.Folder)];
        settings.LastCollectionFolder = Tree.Folder;
        AppSettingsStore.Save(settings);
    }

    /// <summary>
    /// The collection a tab sends with and saves into: the one it was opened from, found by the
    /// folder it recorded, or — for a tab restored from before tabs recorded one — by the request
    /// file it came from. A tab that never had one follows the current collection.
    /// </summary>
    /// <remarks>
    /// A tab whose collection is no longer in the tree (closed, or not reopened this launch) still
    /// gets that collection, read from disk — never blank scopes, and never whichever collection
    /// happens to be current. Null only when its folder no longer exists at all.
    /// </remarks>
    private OpenCollection? CollectionFor(TabState? state)
    {
        if (state?.CollectionFolder is { } folder)
        {
            return Tree.Find(folder) ?? DetachedCollection(folder);
        }

        return Tree.FindContaining(state?.RequestPath) ?? Tree.Current;
    }

    /// <summary>Collections tabs still belong to but the tree no longer shows, by folder. Seeded
    /// by <see cref="CloseCollection"/> with the closed collection itself, so its environment
    /// choice carries straight over; otherwise loaded once from disk.</summary>
    private readonly Dictionary<string, OpenCollection> _detachedCollections = new(OpenCollection.FolderComparer);

    private OpenCollection? DetachedCollection(string folder)
    {
        folder = OpenCollection.Normalize(folder);

        if (_detachedCollections.TryGetValue(folder, out var cached))
        {
            return cached;
        }

        if (!Directory.Exists(folder))
        {
            return null;
        }

        LoadedCollection loaded;
        try
        {
            loaded = CollectionLoader.Load(folder);
        }
        catch (Exception ex) when (ex is CollectionFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var collection = new OpenCollection(folder, Path.GetFileName(folder))
        {
            Definition = loaded.Definition,
            EnvironmentName = Tree.InitialEnvironmentFor?.Invoke(folder) ?? "None",
        };

        _detachedCollections[folder] = collection;
        return collection;
    }

    /// <summary>The environment a collection sends with. The current collection's is already
    /// loaded (and kept fresh on every save); another open collection's is read from disk.</summary>
    private EnvironmentDefinition? EnvironmentFor(OpenCollection? collection)
    {
        if (collection is null || collection.EnvironmentName == "None")
        {
            return null;
        }

        return collection == Tree.Current
            ? ActiveEnvironment
            : LoadEnvironmentOrNull(collection.Folder, collection.EnvironmentName);
    }

    private static EnvironmentDefinition? LoadEnvironmentOrNull(string folder, string name)
    {
        try
        {
            return CollectionLoader.LoadEnvironment(folder, name);
        }
        catch (Exception ex) when (ex is CollectionFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static EnvironmentDefinition LoadGlobals() =>
        LoadEnvironmentOrNull(StorageLocations.Globals, EnvironmentsViewModel.GlobalsName)
            ?? new EnvironmentDefinition { Name = EnvironmentsViewModel.GlobalsName };

    /// <summary>
    /// Points the picker, the loaded environment and the Environments pane at the collection that
    /// just became current, on whatever environment that collection was last left on.
    /// </summary>
    private void OnCurrentCollectionChanged()
    {
        _switchingCollection = true;
        try
        {
            // Read before the list changes: a Reset on the bound list can write a spurious value
            // back through EnvironmentName, and with it into the collection's own choice.
            var target = Tree.Current?.EnvironmentName ?? "None";

            RefreshAvailableEnvironments(pickFirstIfNone: false);

            if (!AvailableEnvironments.Contains(target))
            {
                target = "None";
            }

            if (EnvironmentName == target)
            {
                // Same name, different collection: OnEnvironmentNameChanged will not run, but the
                // environment behind the name is a different file.
                LoadActiveEnvironment(target);
            }
            else
            {
                EnvironmentName = target;
            }

            if (Tree.Current is { } current)
            {
                current.EnvironmentName = target;
            }
        }
        finally
        {
            _switchingCollection = false;
        }

        // Swap in this collection's own editor rather than reloading the shared one, so whatever
        // was being edited in the collection just left stays exactly as it was, unsaved edits
        // included. Reopen keeps a dirty editor untouched and refreshes a clean one from disk.
        Environments = EnvironmentsEditorFor(Tree.Folder);
        Environments.Reopen(Tree.Folder, ActiveEnvironment);
        Environments.RefreshDetectedProfiles(Tree.Definition?.ScannedFrom?.Path);

        // The page's Environments tab now shows a different collection's editor; leaving the page
        // up would show one collection's environments under another's name.
        if (CollectionOverview is { } overview
            && (Tree.Folder is not { } folder || !OpenCollection.IsSameFolder(overview.Folder, folder)))
        {
            CollectionOverview = null;
        }

        OnPropertyChanged(nameof(CollectionAuthSummary));
        OnPropertyChanged(nameof(InheritedHeadersForActiveTab));
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
        await CollectionWriter.WriteEnvironments(SyncReview.OutputPath, result, _services.SecretStore)
            .ConfigureAwait(true);
        CollectionWriter.WriteCollectionDefinition(
            SyncReview.OutputPath,
            Path.GetFileName(SyncReview.OutputPath.TrimEnd(Path.DirectorySeparatorChar)),
            SyncReview.SourcePath);
        EnsureDefaultEnvironment(SyncReview.OutputPath);

        await OpenAndPrimeFolderAsync(SyncReview.OutputPath).ConfigureAwait(true);
    }

    /// <summary>
    /// Scanned source with no <c>launchSettings.json</c>/<c>appsettings.*.json</c> the reader
    /// recognizes — the Conduit sample is exactly this — derives zero environments, and
    /// <see cref="CollectionWriter.WriteEnvironments"/> writes nothing for zero. Without this, that
    /// collection has no environment to select or edit until someone manually walks through
    /// Settings → Environments → "Create environment". A collection just imported from code should
    /// have at least one, with a <c>baseUrl</c> row already there to fill in — every generated
    /// request URL is <c>{{baseUrl}}/...</c>, so a row with nothing in it beats no row at all.
    /// </summary>
    private static void EnsureDefaultEnvironment(string folder)
    {
        if (CollectionLoader.ListEnvironments(folder).Count > 0)
        {
            return;
        }

        var definition = new EnvironmentDefinition { Name = "Local" };
        definition.Shared["baseUrl"] = string.Empty;
        CollectionLoader.SaveEnvironment(folder, definition);
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
    partial void OnEnvironmentNameChanged(string? oldValue, string newValue)
    {
        // A Reset on the bound AvailableEnvironments collection can briefly write null back through
        // the two-way SelectedItem binding, despite this property's own non-null annotation —
        // string.IsNullOrEmpty is what stays safe against that at runtime. Unlike a genuine "None"
        // pick, a spurious null is not the user clearing the environment: snap the picker straight
        // back to whatever it actually held rather than leaving it blank or, worse, blanking
        // ActiveEnvironment underneath it — same idea as EnvironmentsViewModel's own revert for its
        // "Editing" picker.
        if (string.IsNullOrEmpty(newValue))
        {
            if (!_revertingEnvironmentSelection && !string.IsNullOrEmpty(oldValue))
            {
                _revertingEnvironmentSelection = true;
                EnvironmentName = oldValue;
                _revertingEnvironmentSelection = false;
            }

            return;
        }

        LoadActiveEnvironment(newValue);

        // A pick made in the picker is that collection's choice, kept for when it is current again
        // and for next launch. A collection switch merely showing its existing choice is not.
        if (!_switchingCollection && Tree.Current is { } collection)
        {
            collection.EnvironmentName = newValue;

            var settings = AppSettingsStore.Load();
            settings.CollectionEnvironments[collection.Folder] = newValue;
            AppSettingsStore.Save(settings);
        }

        // Keeps the settings pane showing whichever environment is actually selected, if it
        // happens to be open while the title-bar picker switches — otherwise it would keep
        // rendering whatever was active when the pane was last opened.
        if (Dialog == DialogKind.Environments && !_switchingCollection)
        {
            Environments.Load(Tree.Folder, ActiveEnvironment);
        }
    }

    private void LoadActiveEnvironment(string name)
    {
        ActiveEnvironment = name == "None" || Tree.Folder is null
            ? null
            : LoadEnvironmentOrNull(Tree.Folder, name);

        EnvironmentHasSecrets = ActiveEnvironment?.LocalNames.Count > 0;
    }

    /// <summary>
    /// Rebuilds the environment list from the current collection's folder. Called after opening a
    /// folder, after applying a scan — either can add environment files that were not there
    /// before — and whenever a different collection becomes current.
    /// </summary>
    /// <param name="pickFirstIfNone">Picks the first environment for a collection still on "None".
    /// Off when merely switching collections, where "None" may be what that collection was left on.</param>
    private void RefreshAvailableEnvironments(bool pickFirstIfNone = true)
    {
        List<string> names = ["None"];

        if (Tree.Folder is not null)
        {
            names.AddRange(CollectionLoader.ListEnvironments(Tree.Folder));
        }

        // Reconciled in place, not Clear()-then-refill: a Clear() raises a Reset, which drops the
        // bound flyout ListBox's selection and writes null straight back through EnvironmentName's
        // two-way binding — see OnEnvironmentNameChanged's guard against exactly that.
        ObservableListSync.SyncTo(AvailableEnvironments, names);

        // A single environment is the common case for a freshly imported collection, and picking
        // it automatically is what lets Send work without an extra click nobody would expect to need.
        if (pickFirstIfNone && AvailableEnvironments.Count > 1 && EnvironmentName == "None")
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

            foreach (var header in CollectionFor(state)?.Definition.Headers.Where(h => h.Enabled) ?? [])
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

    /// <summary>Restores the tabs, then reopens every collection that was open last time — minus
    /// any since moved or deleted — and lands on the one that was current, or the active tab's.</summary>
    public async Task RestoreSessionAsync()
    {
        await Tabs.RestoreSessionAsync().ConfigureAwait(true);

        if (Tree.Collections.Count > 0)
        {
            return;
        }

        var settings = AppSettingsStore.Load();

        // Settings from before several collections could be open only know the one.
        var folders = settings.OpenCollectionFolders is { Count: > 0 } open
            ? open
            : settings.LastCollectionFolder is { Length: > 0 } last ? [last] : [];

        foreach (var folder in folders.Where(Directory.Exists))
        {
            await OpenAndPrimeFolderAsync(folder).ConfigureAwait(true);
        }

        var landing = Tree.Find(Tabs.Active?.State?.CollectionFolder) ?? Tree.Find(settings.LastCollectionFolder);

        if (landing is not null)
        {
            Tree.Current = landing;
        }

        RememberOpenCollections();
    }

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
    GlobalVariables,
    AuthProfile,
    TrustAndNetwork,
    Storage,
    Keyboard,
    Appearance,
    DefaultHeaders,
    ConfirmCloseTab,
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
