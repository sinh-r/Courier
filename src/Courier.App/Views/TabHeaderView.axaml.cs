using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Courier.App.ViewModels;

namespace Courier.App.Views;

public sealed partial class TabHeaderView : UserControl
{
    public TabHeaderView()
    {
        InitializeComponent();

        SelectButton.Click += (_, _) => Select();
        CloseButton.Click += (_, _) => Close();

        // Middle-click closes, matching the convention every browser already taught this gesture.
        Root.PointerPressed += OnRootPointerPressed;
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(Root).Properties.IsMiddleButtonPressed)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Select()
    {
        if (DataContext is TabViewModel tab && FindShell() is { } shell)
        {
            shell.Tabs.Select(tab);
        }
    }

    private void Close()
    {
        if (DataContext is TabViewModel tab && FindShell() is { } shell)
        {
            shell.Tabs.Close(tab);
        }
    }

    /// <summary>
    /// This control's own DataContext is the tab, not the shell — the shell lives on the enclosing
    /// window, which every dialog and pane in this app reaches through, so this is the one place
    /// that has to climb for it instead of just binding.
    /// </summary>
    private MainWindowViewModel? FindShell() =>
        this.FindAncestorOfType<Window>()?.DataContext as MainWindowViewModel;
}
