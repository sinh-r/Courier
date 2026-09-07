using Avalonia.Controls;
using Courier.App.ViewModels;

namespace Courier.App.Views;

public sealed partial class ResponseView : UserControl
{
    public ResponseView()
    {
        InitializeComponent();

        SearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && DataContext is MainWindowViewModel shell)
            {
                shell.Tabs.Active?.Response?.Search(SearchBox.Text ?? string.Empty);
            }
        };

        PrevMatchButton.Click += (_, _) => Navigate(-1);
        NextMatchButton.Click += (_, _) => Navigate(1);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel shell)
            {
                shell.Tabs.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TabCollection.Active))
                    {
                        SearchBox.Text = string.Empty;
                    }
                };
            }
        };
    }

    /// <summary>Moves to the next/previous hit and scrolls the tree to reveal it. UI_SPEC 5.8.</summary>
    private void Navigate(int delta)
    {
        if (DataContext is not MainWindowViewModel shell || shell.Tabs.Active?.Response is not { } response)
        {
            return;
        }

        var row = response.MoveToMatch(delta);

        if (row >= 0)
        {
            Tree.ScrollToRow(row);
        }
    }
}
