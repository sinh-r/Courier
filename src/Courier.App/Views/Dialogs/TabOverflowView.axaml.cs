using Avalonia.Controls;
using Courier.App.ViewModels;

namespace Courier.App.Views.Dialogs;

public sealed partial class TabOverflowView : UserControl
{
    public TabOverflowView()
    {
        InitializeComponent();

        TabList.SelectionChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel shell && TabList.SelectedItem is TabViewModel tab)
            {
                shell.Tabs.Select(tab);
                shell.CloseDialog();
            }
        };
    }
}
