using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Courier.App.ViewModels;

/// <summary>
/// Route-parameter values for the active tab's URL.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="KeyValueEditorViewModel"/>: the row set here is derived from the URL,
/// not user-edited, so there is no delete, no trailing blank row, and — the important difference —
/// an empty value is data the send has to refuse on, not a signal to drop the row. Reusing the
/// params/headers editor would silently delete a <c>{slug}</c> entry the moment its value was
/// cleared, because that editor's <c>Publish</c> filters out blank rows.
/// </remarks>
public sealed class PathParamEditorViewModel
{
    private readonly Action<IReadOnlyDictionary<string, string>> _onChanged;
    private bool _suppress;

    public PathParamEditorViewModel(Action<IReadOnlyDictionary<string, string>> onChanged) => _onChanged = onChanged;

    public ObservableCollection<PathParamRowViewModel> Rows { get; } = [];

    /// <summary>
    /// Rebuilds the row set from the URL's current tokens, keeping the value of any token that
    /// survives — typing a character in the middle of a route should not clear what was already
    /// filled in for the parameters either side of it.
    /// </summary>
    public void SetTokens(IReadOnlyList<string> tokens, IReadOnlyDictionary<string, string> savedValues)
    {
        _suppress = true;

        var kept = Rows.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal);
        Rows.Clear();

        foreach (var token in tokens)
        {
            var value = kept.TryGetValue(token, out var existing)
                ? existing
                : savedValues.GetValueOrDefault(token, string.Empty);

            Rows.Add(new PathParamRowViewModel(token, value, OnRowChanged));
        }

        _suppress = false;
    }

    private void OnRowChanged(PathParamRowViewModel row)
    {
        if (_suppress)
        {
            return;
        }

        _onChanged(Rows.ToDictionary(r => r.Name, r => r.Value, StringComparer.Ordinal));
    }
}

/// <summary>One route parameter. The name comes from the URL and cannot be edited; the value can.</summary>
public sealed partial class PathParamRowViewModel : ObservableObject
{
    private readonly Action<PathParamRowViewModel> _onChanged;

    [ObservableProperty]
    private string _value;

    public PathParamRowViewModel(string name, string value, Action<PathParamRowViewModel> onChanged)
    {
        Name = name;
        _value = value;
        _onChanged = onChanged;
    }

    public string Name { get; }

    partial void OnValueChanged(string value) => _onChanged(this);
}
