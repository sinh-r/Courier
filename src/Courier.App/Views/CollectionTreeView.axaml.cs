using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Courier.App.ViewModels;

namespace Courier.App.Views;

public sealed partial class CollectionTreeView : UserControl
{
    public CollectionTreeView()
    {
        InitializeComponent();

        FocusFilterButton.Click += (_, _) => FilterBox.Focus();
        Tree.DoubleTapped += OnTreeDoubleTapped;
    }

    /// <summary>Double-click opens the selected request. SCAN-01's whole point is a tree worth clicking into.</summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not CollectionTreeViewModel tree || tree.Selected?.Request is not { } request)
        {
            return;
        }

        if (this.FindAncestorOfType<Window>()?.DataContext is MainWindowViewModel shell)
        {
            shell.OpenRequest(request);
        }
    }
}
