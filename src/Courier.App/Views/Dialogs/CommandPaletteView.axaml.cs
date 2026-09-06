using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Courier.App.ViewModels;

namespace Courier.App.Views.Dialogs;

public sealed partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();

        QueryBox.KeyDown += OnQueryBoxKeyDown;

        // Every dialog in DialogHost is realized once and toggled with IsVisible, so
        // AttachedToVisualTree fires only on first paint — this is what actually catches every
        // re-open and gives the query box focus and a clean selection.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                QueryBox.Focus();
                QueryBox.SelectAll();
            }
        };
    }

    private void OnQueryBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel shell)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                shell.Palette.Move(1);
                e.Handled = true;
                break;

            case Key.Up:
                shell.Palette.Move(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                shell.Palette.Activate();
                e.Handled = true;
                break;
        }
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel shell)
        {
            shell.Palette.Activate();
        }
    }
}
