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
    }
}
