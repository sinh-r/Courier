using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Courier.App.ViewModels;

/// <summary>
/// Backs one editable params/headers grid. Owns the row collection, keeps exactly one trailing
/// blank row so a new entry never needs an explicit "add" click, and republishes a snapshot to the
/// owner whenever a row changes so it can be written back into <see cref="TabState"/>.
/// </summary>
public sealed class KeyValueEditorViewModel
{
    private readonly Action<IReadOnlyList<(string Name, string Value, bool Enabled, string? Description)>> _onChanged;
    private bool _suppress;

    public KeyValueEditorViewModel(
        Action<IReadOnlyList<(string Name, string Value, bool Enabled, string? Description)>> onChanged) =>
        _onChanged = onChanged;

    public ObservableCollection<KeyValueRowViewModel> Rows { get; } = [];

    /// <summary>Replaces every row from a saved list, plus one trailing blank row.</summary>
    public void Load(IEnumerable<(string Name, string Value, bool Enabled, string? Description)> items)
    {
        _suppress = true;

        Rows.Clear();

        foreach (var item in items)
        {
            Rows.Add(CreateRow(item.Name, item.Value, item.Enabled, item.Description));
        }

        Rows.Add(CreateRow(string.Empty, string.Empty, true, null));

        _suppress = false;
    }

    private KeyValueRowViewModel CreateRow(string name, string value, bool enabled, string? description) =>
        new(OnRowChanged, OnRowDeleteRequested)
        {
            Name = name,
            Value = value,
            Enabled = enabled,
            Description = description,
        };

    private void OnRowChanged(KeyValueRowViewModel row)
    {
        if (_suppress)
        {
            return;
        }

        // Exactly one trailing blank row: once the last row stops being blank, a fresh one appears.
        if (Rows.Count > 0 && ReferenceEquals(row, Rows[^1]) && !row.IsBlank)
        {
            Rows.Add(CreateRow(string.Empty, string.Empty, true, null));
        }

        Publish();
    }

    private void OnRowDeleteRequested(KeyValueRowViewModel row)
    {
        if (Rows.Count > 1)
        {
            Rows.Remove(row);
        }
        else
        {
            // The last row never disappears; clearing it is how you delete the only entry.
            row.Reset();
        }

        Publish();
    }

    private void Publish()
    {
        if (_suppress)
        {
            return;
        }

        var snapshot = Rows
            .Where(r => !r.IsBlank)
            .Select(r => (r.Name, r.Value, r.Enabled, r.Description))
            .ToList();

        _onChanged(snapshot);
    }
}

/// <summary>One editable row in a params or headers grid.</summary>
public sealed partial class KeyValueRowViewModel : ObservableObject
{
    private readonly Action<KeyValueRowViewModel> _onChanged;
    private readonly Action<KeyValueRowViewModel> _onDeleteRequested;

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private string? _description;

    public KeyValueRowViewModel(
        Action<KeyValueRowViewModel> onChanged,
        Action<KeyValueRowViewModel> onDeleteRequested)
    {
        _onChanged = onChanged;
        _onDeleteRequested = onDeleteRequested;
    }

    public bool IsBlank => Name.Length == 0 && Value.Length == 0;

    public void Reset()
    {
        Name = string.Empty;
        Value = string.Empty;
        Enabled = true;
        Description = null;
    }

    partial void OnEnabledChanged(bool value) => _onChanged(this);

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsBlank));
        _onChanged(this);
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsBlank));
        _onChanged(this);
    }

    partial void OnDescriptionChanged(string? value) => _onChanged(this);

    [RelayCommand]
    private void Delete() => _onDeleteRequested(this);
}
