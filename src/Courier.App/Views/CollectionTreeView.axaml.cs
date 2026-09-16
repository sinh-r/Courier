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

    /// <summary>Double-click opens the selected request, or — on the collection root — the auth
    /// settings that request left on "Inherit" resolve to. SCAN-01's whole point is a tree worth
    /// clicking into.</summary>
    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not CollectionTreeViewModel tree
            || this.FindAncestorOfType<Window>()?.DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        if (tree.Selected?.Request is { } request)
        {
            shell.OpenRequest(request);
        }
        else if (tree.Selected?.Kind == TreeNodeKind.Collection)
        {
            shell.OpenDialog(DialogKind.AuthProfile);
        }
    }
}
