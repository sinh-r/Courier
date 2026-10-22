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
        Tree.Tapped += OnTreeTapped;
        Tree.DoubleTapped += OnTreeDoubleTapped;
    }

    /// <summary>A single click on the collection root opens its own page — Overview and Auth —
    /// in place of the request panes, the way Postman does. Handled on Tapped rather than on
    /// selection change so clicking the root again, after going back to a request, reopens it.</summary>
    private void OnTreeTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is CollectionTreeViewModel { Selected.Kind: TreeNodeKind.Collection }
            && FindShell() is { } shell)
        {
            shell.OpenCollectionOverview();
        }
    }

    /// <summary>Double-click opens the selected request. SCAN-01's whole point is a tree worth
    /// clicking into.</summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is CollectionTreeViewModel { Selected: { Request: { } request } node } && FindShell() is { } shell)
        {
            shell.OpenRequest(request, node.Collection);
        }
    }

    private MainWindowViewModel? FindShell() =>
        this.FindAncestorOfType<Window>()?.DataContext as MainWindowViewModel;
}
