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

    private Stopwatch? _sendStopwatch;
    private CancellationTokenSource? _sendCts;

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
        });

        QueryEditor = new KeyValueEditorViewModel(rows =>
        {
            if (State is null)
            {
                return;
            }

            State.Query = [.. rows.Select(r => new QueryParameter(r.Name, r.Value, r.Enabled, r.Description))];
            MarkDirty();
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

        // _url is set directly (constructor) or already matches State.Url by invariant — nothing
        // can touch State while a tab is suspended, so Activate() can never rehydrate a URL that
        // has drifted from the field. Only the row set needs rebuilding here.
        RefreshPathParams();
    }

    private void RefreshPathParams() => PathParamEditor.SetTokens(
        PathParameterNames.From(State?.Url ?? Url),
        State?.PathParams ?? new Dictionary<string, string>());

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

    /// <summary>
    /// The URL box binds here (rather than to <c>State.Url</c> directly) precisely so this exists:
    /// <c>TabState</c> is a plain POCO with no change notification, so without this hook nothing
    /// could tell the Path tab a token had appeared or disappeared as the user typed.
    /// </summary>
    partial void OnUrlChanged(string value)
    {
        if (State is not null && State.Url != value)
        {
            State.Url = value;
            MarkDirty();
        }

        RefreshPathParams();
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
