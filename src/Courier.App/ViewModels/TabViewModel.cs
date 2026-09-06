using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public TabViewModel(TabState state)
    {
        State = state;
        _title = state.Title;
        _method = state.Method;
        _isDirty = state.IsDirty;
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

    partial void OnMethodChanged(string value) => OnPropertyChanged(nameof(VerbBrush));

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));

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
