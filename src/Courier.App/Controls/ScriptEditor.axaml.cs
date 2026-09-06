using System.ComponentModel;
using Avalonia.Controls;
using AvaloniaEdit.Document;
using Courier.App.ViewModels;

namespace Courier.App.Controls;

public sealed partial class ScriptEditor : UserControl
{
    private MainWindowViewModel? _shell;
    private TabViewModel? _boundTab;
    private bool _suppressStateWrites;

    public ScriptEditor()
    {
        InitializeComponent();

        PreRequest.TextChanged += (_, _) =>
        {
            if (!_suppressStateWrites && _boundTab?.State is { } state)
            {
                state.PreRequestScript = PreRequest.Document?.Text;
                _boundTab.MarkDirty();
            }
        };

        PostResponse.TextChanged += (_, _) =>
        {
            if (!_suppressStateWrites && _boundTab?.State is { } state)
            {
                state.PostResponseScript = PostResponse.Document?.Text;
                _boundTab.MarkDirty();
            }
        };

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_shell is not null)
        {
            _shell.Tabs.PropertyChanged -= OnTabsPropertyChanged;
        }

        _shell = DataContext as MainWindowViewModel;

        if (_shell is not null)
        {
            _shell.Tabs.PropertyChanged += OnTabsPropertyChanged;
        }

        LoadFromActiveTab();
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(TabCollection.Active))
        {
            LoadFromActiveTab();
        }
    }

    private void LoadFromActiveTab()
    {
        _boundTab = _shell?.Tabs.Active;
        var state = _boundTab?.State;

        _suppressStateWrites = true;
        PreRequest.Document = new TextDocument(state?.PreRequestScript ?? string.Empty);
        PostResponse.Document = new TextDocument(state?.PostResponseScript ?? string.Empty);
        _suppressStateWrites = false;
    }
}
