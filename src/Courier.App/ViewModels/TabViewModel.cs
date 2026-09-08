using System.Diagnostics;
using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.App.Services;
using Courier.Core.Collections;

namespace Courier.App.ViewModels;

/// <summary>
/// One open tab. Holds serialized state, never a realized control tree.
/// </summary>
/// <remarks>
/// <para>
/// The suspension contract, from UI_SPEC 4.2: "A tab that has been idle beyond a threshold renders
/// identically but is suspended underneath. There must be <b>no visual indication</b> of
/// suspension — the user should never learn the concept exists."
/// </para>
/// <para>
/// That is why <see cref="Title"/>, <see cref="Method"/> and <see cref="VerbBrush"/> stay live on a
/// suspended tab: the strip has to draw correctly without waking anything. Only the heavy part —
/// the body text, the response buffer, the parsed index — is dropped, and rehydrating it must fit
/// inside PERF-03's 50ms switch budget.
/// </para>
/// </remarks>
public sealed partial class TabViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions Serialization = new() { WriteIndented = false };

    private string? _suspendedPayload;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _method;

    [ObservableProperty]
    private ResponseViewModel? _response;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private TimeSpan _elapsed;

    [ObservableProperty]
    private string _url;

    /// <summary>
    /// Why the last Send could not build a request — an unresolved variable, an unfilled path
    /// parameter, or a URL that is not absolute. Null when there is nothing to report. UI_SPEC 3.7.
    /// </summary>
    [ObservableProperty]
    private string? _sendError;

    /// <summary>
    /// Mirrors <see cref="TabState.ActiveResponseTabIndex"/> so the strip has something bindable and
    /// change-notifying to watch — the POCO has no <c>INotifyPropertyChanged</c> of its own. Synced
    /// back in <see cref="OnActiveResponseTabIndexChanged"/>, same pattern as <see cref="Method"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodyTabActive))]
    [NotifyPropertyChangedFor(nameof(IsHeadersTabActive))]
    [NotifyPropertyChangedFor(nameof(IsTestsTabActive))]
    [NotifyPropertyChangedFor(nameof(IsServerTabActive))]
    private int _activeResponseTabIndex;

    /// <summary>
    /// Mirrors <see cref="TabState.ActiveRequestTabIndex"/> the same way <see cref="ActiveResponseTabIndex"/>
    /// mirrors its response-pane counterpart, so which of Path/Params/Headers/Body/Auth/Scripts was
    /// open survives suspend and restore.
    /// </summary>
    [ObservableProperty]
    private int _activeRequestTabIndex;

    private Stopwatch? _sendStopwatch;
    private CancellationTokenSource? _sendCts;

    /// <summary>True while <see cref="RecomposeUrlForDisplay"/> is writing to <see cref="Url"/>, so
    /// <see cref="OnUrlChanged"/> knows this is its own recompose rather than something typed.</summary>
    private bool _suppressUrlSync;

    public TabViewModel(TabState state)
    {
        State = state;
        _title = state.Title;
        _method = state.Method;
        _url = state.Url;
        _isDirty = state.IsDirty;
        _activeResponseTabIndex = state.ActiveResponseTabIndex;

        HeaderEditor = new KeyValueEditorViewModel(rows =>
        {
            if (State is null)
            {
                return;
            }

            State.Headers = [.. rows.Select(r => new HeaderValue(r.Name, r.Value, r.Enabled, r.Description))];
            MarkDirty();
            OnPropertyChanged(nameof(HeadersTabHeader));
        });

        QueryEditor = new KeyValueEditorViewModel(rows =>
        {
            if (State is null)
            {
                return;
            }

            State.Query = [.. rows.Select(r => new QueryParameter(r.Name, r.Value, r.Enabled, r.Description))];
            MarkDirty();
            OnPropertyChanged(nameof(ParamsTabHeader));

            // A row edited in the grid — a checkbox toggled, a value typed — is a discrete action,
            // not continuous typing, so it is safe to immediately reflect it in the URL bar. Typing
            // directly into the URL bar is the opposite case; see OnUrlChanged.
            RecomposeUrlForDisplay();
        });

        PathParamEditor = new PathParamEditorViewModel(values =>
        {
            if (State is null)
            {
                return;
            }

            State.PathParams = new Dictionary<string, string>(values, StringComparer.Ordinal);
            MarkDirty();
        });

        LoadRowsFromState();
        _activeRequestTabIndex = ResolveInitialRequestTabIndex(state.ActiveRequestTabIndex);
        RecomposeUrlForDisplay();
        RefreshTabHeaders();
    }

    /// <summary>The Params grid. UI_SPEC — the grid the user actually edits.</summary>
    public KeyValueEditorViewModel QueryEditor { get; }

    /// <summary>The Headers grid.</summary>
    public KeyValueEditorViewModel HeaderEditor { get; }

    /// <summary>The Path tab. Row names come from the URL; only shown when there are any.</summary>
    public PathParamEditorViewModel PathParamEditor { get; }

    private void LoadRowsFromState()
    {
        if (State is null)
        {
            return;
        }

        HeaderEditor.Load(State.Headers.Select(h => (h.Name, h.Value, h.Enabled, h.Description)));
        QueryEditor.Load(State.Query.Select(q => (q.Name, q.Value, q.Enabled, q.Description)));

        // Rebuilding the row sets does not by itself touch Url or the tab-header labels — Load
        // suppresses the editors' change callbacks so the rebuild is not mistaken for an edit.
        // Callers (the constructor, Activate) refresh both explicitly afterward.
        RefreshPathParams();
    }

    private void RefreshPathParams() => PathParamEditor.SetTokens(
        PathParameterNames.From(State?.Url ?? Url),
        State?.PathParams ?? new Dictionary<string, string>());

    /// <summary>
    /// Recomposes the URL bar from the bare <see cref="TabState.Url"/> plus the currently enabled
    /// Params rows. Goes through the <see cref="Url"/> property so the box's binding updates, but
    /// guarded by <see cref="_suppressUrlSync"/> so <see cref="OnUrlChanged"/> does not turn around
    /// and re-split the very query string this just built.
    /// </summary>
    private void RecomposeUrlForDisplay()
    {
        if (State is null)
        {
            return;
        }

        var composed = QueryString.Compose(State.Url, State.Query);
        if (composed == Url)
        {
            return;
        }

        _suppressUrlSync = true;
        try
        {
            Url = composed;
        }
        finally
        {
            _suppressUrlSync = false;
        }
    }

    /// <summary>"Params · 3" once there is something to look at, plain "Params" otherwise — same
    /// idea for Headers. Refreshed from both editors' change callbacks, which fire on every row
    /// add, edit and delete (see <see cref="KeyValueEditorViewModel.Load"/>'s suppression), and once
    /// up front here since a freshly loaded row set fires no callback of its own.</summary>
    private void RefreshTabHeaders()
    {
        OnPropertyChanged(nameof(ParamsTabHeader));
        OnPropertyChanged(nameof(HeadersTabHeader));
    }

    public string ParamsTabHeader => TabHeader("Params", QueryEditor.Rows.Count(r => !r.IsBlank));

    public string HeadersTabHeader => TabHeader("Headers", HeaderEditor.Rows.Count(r => !r.IsBlank));

    private static string TabHeader(string name, int count) => count > 0 ? $"{name} · {count}" : name;

    /// <summary>
    /// Index 0 is Path, hidden whenever the URL has no <c>{tokens}</c> — a brand-new blank tab (and
    /// <see cref="TabState"/>'s own field default) leaves the index unset at 0 with nothing there to
    /// select, which would otherwise land the strip on an invisible tab with nothing visibly active.
    /// Treating "still at 0" as "not yet decided" and picking Params-or-Body instead is safe: the
    /// user can only ever have actually chosen Path when it was visible, and it is exactly as
    /// visible now as it was then, so a real, deliberate 0 is indistinguishable from an unset one
    /// only in the one case where both mean the same thing.
    /// </summary>
    private int ResolveInitialRequestTabIndex(int persisted)
    {
        if (persisted != 0 || PathParamEditor.Rows.Count > 0)
        {
            return persisted;
        }

        return State is { } state && state.Query.Any(q => q.Name.Length > 0) && state.BodyKind == BodyKind.None
            ? 1
            : 3;
    }

    /// <summary>Null while suspended. Every access must go through <see cref="Activate"/> first.</summary>
    public TabState? State { get; private set; }

    public string Id => State?.Id ?? _suspendedId;

    private string _suspendedId = string.Empty;

    public bool IsSuspended => State is null;

    /// <summary>The verb badge colour. The only colour on the tab, and often the only thing read.</summary>
    public IBrush VerbBrush => VerbBrushes.For(Method);

    /// <summary>
    /// Middle-truncated title. UI_SPEC 4.2: <c>orders/…/lines</c>, never truncated from the end,
    /// because the end is the part that distinguishes two tabs on the same resource.
    /// </summary>
    public string DisplayTitle => MiddleTruncate(Title, 24);

    public Provenance Provenance => State?.Provenance ?? Provenance.Authored;

    public bool IsBodyTabActive => ActiveResponseTabIndex == 0;

    public bool IsHeadersTabActive => ActiveResponseTabIndex == 1;

    public bool IsTestsTabActive => ActiveResponseTabIndex == 2;

    public bool IsServerTabActive => ActiveResponseTabIndex == 3;

    /// <summary>
    /// Body, Headers, Tests, Server — by index, matching <see cref="ActiveResponseTabIndex"/>. Tests
    /// and Server stay <c>IsEnabled="False"</c> in the view, so only 0 and 1 are ever actually asked
    /// for; Raw/Pretty is a separate axis handled by <see cref="ResponseViewModel.Mode"/>.
    /// </summary>
    [RelayCommand]
    private void SetActiveResponseTab(string tab) => ActiveResponseTabIndex = tab switch
    {
        "Headers" => 1,
        "Tests" => 2,
        "Server" => 3,
        _ => 0,
    };

    /// <summary>
    /// Serializes and releases the heavy state. Called for tabs that have been idle past the
    /// threshold, so 100 open tabs cost roughly 100 short strings rather than 100 editors.
    /// </summary>
    public void Suspend()
    {
        if (State is null)
        {
            return;
        }

        _suspendedId = State.Id;
        _suspendedPayload = JsonSerializer.Serialize(State, Serialization);
        State = null;

        // The response is the expensive part: a 40MB buffer and its index. Dropping it is the
        // single biggest contributor to PERF-02.
        Response?.Release();
        Response = null;
    }

    /// <summary>
    /// Rehydrates. Must complete well inside PERF-03's 50ms, which is why the serialized form is
    /// compact JSON of a flat POCO rather than anything that needs reconstruction.
    /// </summary>
    public void Activate()
    {
        if (State is not null)
        {
            State.LastActive = DateTimeOffset.UtcNow;
            return;
        }

        State = _suspendedPayload is null
            ? new TabState { Id = _suspendedId, Title = Title, Method = Method }
            : JsonSerializer.Deserialize<TabState>(_suspendedPayload, Serialization)
              ?? new TabState { Id = _suspendedId };

        State.LastActive = DateTimeOffset.UtcNow;
        _suspendedPayload = null;
        ActiveResponseTabIndex = State.ActiveResponseTabIndex;
        LoadRowsFromState();
        ActiveRequestTabIndex = ResolveInitialRequestTabIndex(State.ActiveRequestTabIndex);
        RecomposeUrlForDisplay();
        RefreshTabHeaders();
    }

    /// <summary>Starts the in-flight state: elapsed timer running, a cancellable token live.</summary>
    public CancellationToken BeginSend()
    {
        _sendCts?.Cancel();
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        _sendStopwatch = Stopwatch.StartNew();
        Elapsed = TimeSpan.Zero;
        IsSending = true;
        return _sendCts.Token;
    }

    /// <summary>Called on a timer while sending so the elapsed-time label keeps moving.</summary>
    public void TickElapsed()
    {
        if (_sendStopwatch is not null)
        {
            Elapsed = _sendStopwatch.Elapsed;
        }
    }

    /// <summary>Requests cancellation of the in-flight send. Ctrl+. and the Cancel button both call this.</summary>
    public void CancelSend() => _sendCts?.Cancel();

    public void CompleteSend()
    {
        IsSending = false;
        _sendStopwatch = null;
        _sendCts?.Dispose();
        _sendCts = null;
    }

    /// <summary>The serialized form, for session restore and crash recovery. NFR-07.</summary>
    public string Serialize() => _suspendedPayload ?? JsonSerializer.Serialize(State, Serialization);

    public static TabViewModel Deserialize(string payload)
    {
        var state = JsonSerializer.Deserialize<TabState>(payload, Serialization) ?? new TabState();
        return new TabViewModel(state);
    }

    public void MarkDirty()
    {
        IsDirty = true;

        if (State is not null)
        {
            State.IsDirty = true;
        }
    }

    partial void OnMethodChanged(string value)
    {
        OnPropertyChanged(nameof(VerbBrush));

        // State and Method were two independent copies of the same fact until now: the method
        // combo box binds here (so the tab header's verb badge updates live), and this is the one
        // place that pushes the choice back into the state that actually gets sent and saved.
        if (State is not null && State.Method != value)
        {
            State.Method = value;
            MarkDirty();
        }
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));

    /// <summary>
    /// Not a content edit, so this deliberately never calls <see cref="MarkDirty"/> — switching to
    /// the Headers tab to look something up should not put a dot on the tab.
    /// </summary>
    partial void OnActiveResponseTabIndexChanged(int value)
    {
        if (State is not null)
        {
            State.ActiveResponseTabIndex = value;
        }
    }

    /// <summary>Same as <see cref="OnActiveResponseTabIndexChanged"/>, for the request-side strip.</summary>
    partial void OnActiveRequestTabIndexChanged(int value)
    {
        if (State is not null)
        {
            State.ActiveRequestTabIndex = value;
        }
    }

    /// <summary>
    /// The URL box binds here (rather than to <c>State.Url</c> directly) precisely so this exists:
    /// <c>TabState</c> is a plain POCO with no change notification, so without this hook nothing
    /// could tell the Path tab a token had appeared or disappeared as the user typed.
    /// </summary>
    /// <remarks>
    /// <see cref="Url"/> always displays the composed form — bare URL plus the enabled Params rows
    /// (see <see cref="RecomposeUrlForDisplay"/>) — so whatever the box currently shows other than
    /// that is exactly what the user just typed. Splitting the query back off here and folding it
    /// into <see cref="QueryEditor"/> is what makes typing <c>?page=2</c> into the box populate the
    /// grid, the other half of the sync <see cref="QueryEditor"/>'s own callback already does.
    /// </remarks>
    partial void OnUrlChanged(string value)
    {
        if (_suppressUrlSync)
        {
            return;
        }

        if (State is null)
        {
            RefreshPathParams();
            return;
        }

        var (bareUrl, typedQuery) = QueryString.Split(value);

        if (State.Url != bareUrl)
        {
            State.Url = bareUrl;
            MarkDirty();
        }

        SyncQueryFromUrl(typedQuery);
        RefreshPathParams();
    }

    /// <summary>
    /// Rebuilds the enabled Params rows from what the URL bar's query string now reads. A disabled
    /// row is invisible to that text (<see cref="RecomposeUrlForDisplay"/> never includes one), so it
    /// is left exactly as it is — the box only ever speaks for the enabled set, which is what stops
    /// unchecking a parameter elsewhere from being undone by the next keystroke here. This does not
    /// itself touch the URL bar: forcing a recompose mid-keystroke would reformat the text — and
    /// possibly move the caret — under the user's fingers.
    /// </summary>
    private void SyncQueryFromUrl(IReadOnlyList<QueryParameter> typedQuery)
    {
        if (State is null)
        {
            return;
        }

        var stillDisabled = State.Query.Where(q => !q.Enabled).ToList();
        var previouslyEnabled = State.Query
            .Where(q => q.Enabled)
            .ToDictionary(q => q.Name, q => q.Description, StringComparer.Ordinal);

        var rebuiltEnabled = typedQuery
            .Select(p => new QueryParameter(p.Name, p.Value, Enabled: true, previouslyEnabled.GetValueOrDefault(p.Name)))
            .ToList();

        var next = stillDisabled.Concat(rebuiltEnabled).ToList();

        if (next.SequenceEqual(State.Query))
        {
            return;
        }

        State.Query = next;
        MarkDirty();

        QueryEditor.Load(next.Select(q => (q.Name, q.Value, q.Enabled, q.Description)));
        OnPropertyChanged(nameof(ParamsTabHeader));
    }

    /// <summary>
    /// Truncates from the middle, keeping the last segment whole because that is the part that
    /// tells two tabs apart.
    /// </summary>
    internal static string MiddleTruncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var lastSlash = text.LastIndexOf('/');

        if (lastSlash > 0 && text.Length - lastSlash < max - 4)
        {
            var tail = text[lastSlash..];
            var headBudget = max - tail.Length - 1;
            return headBudget > 0 ? $"{text[..headBudget]}…{tail}" : $"…{tail}";
        }

        var half = (max - 1) / 2;
        return $"{text[..half]}…{text[^half..]}";
    }
}
