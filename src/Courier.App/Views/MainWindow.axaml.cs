using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Courier.App.Services;
using Courier.App.ViewModels;

namespace Courier.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // CAP-05: import by drag-drop. The drop target is the whole window, because a tester
        // arriving with a capsule should not have to find a target.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
    }

    private MainWindowViewModel? Shell => DataContext as MainWindowViewModel;

    /// <summary>
    /// Clamps the default size to the screen's working area.
    /// </summary>
    /// <remarks>
    /// Without this the default 1280x800 runs off a 1366x768 corporate laptop and the status bar —
    /// which SEC-04 requires to be permanently visible — is the first thing to go under the edge of
    /// the screen. NFR-05 also has this at 100 through 200 per cent scaling, where the working area
    /// in device-independent pixels shrinks as scaling rises.
    /// </remarks>
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (Environment.GetEnvironmentVariable("COURIER_LAYOUT_DIAGNOSTICS") == "1")
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => LayoutDiagnostics.Dump(this),
                Avalonia.Threading.DispatcherPriority.Background);
        }

        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        var working = screen.WorkingArea;
        var scale = screen.Scaling;
        var availableWidth = working.Width / scale;
        var availableHeight = working.Height / scale;

        if (Width > availableWidth || Height > availableHeight)
        {
            Width = Math.Max(MinWidth, Math.Min(Width, availableWidth - 16));
            Height = Math.Max(MinHeight, Math.Min(Height, availableHeight - 16));
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Position = new PixelPoint(
                working.X + (int)((working.Width - (Width * scale)) / 2),
                working.Y + (int)((working.Height - (Height * scale)) / 2));
        }
    }

    /// <summary>
    /// Full keyboard operability (NFR-04) starts here. Every binding is also reachable from the
    /// command palette, so nothing is keyboard-only knowledge.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Shell is not { } shell)
        {
            base.OnKeyDown(e);
            return;
        }

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.K when control:
                shell.OpenDialog(DialogKind.CommandPalette);
                shell.Palette.Refresh();
                e.Handled = true;
                break;

            case Key.N when control:
                shell.NewTab();
                e.Handled = true;
                break;

            case Key.W when control:
                shell.CloseActiveTab();
                e.Handled = true;
                break;

            case Key.I when control:
                shell.ToggleInspector();
                e.Handled = true;
                break;

            case Key.Escape when shell.IsDialogOpen:
                shell.CloseDialog();
                e.Handled = true;
                break;

            case Key.Tab when control:
                CycleTab(shell, e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
                break;
        }

        if (!e.Handled)
        {
            base.OnKeyDown(e);
        }
    }

    private static void CycleTab(MainWindowViewModel shell, int delta)
    {
        if (shell.Tabs.Items.Count == 0)
        {
            return;
        }

        var index = shell.Tabs.Active is null ? 0 : shell.Tabs.Items.IndexOf(shell.Tabs.Active);
        var next = (index + delta + shell.Tabs.Items.Count) % shell.Tabs.Items.Count;
        shell.Tabs.Select(shell.Tabs.Items[next]);
    }

    /// <summary>
    /// Below 1200px the inspector auto-collapses. UI_SPEC 6, and the reason the centre pane stays
    /// usable on a 1024-wide window.
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // Layout fires this with a zero width before the window is realized. Acting on that
        // collapses the inspector for one pass and leaves the centre pane sized for the whole
        // window, which then arranges the inspector off the right edge.
        if (Shell is not { } shell || Bounds.Width < MinWidth)
        {
            return;
        }

        shell.IsInspectorVisible = Bounds.Width >= 1200;

        // The tab strip never shrinks or scrolls tabs; it collapses the excess into +n.
        // UI_SPEC 4.2, and the reason a hundred open tabs costs eight realized headers.
        var stripWidth = Bounds.Width - 260 - (shell.IsInspectorVisible ? 300 : 0) - 40;
        shell.Tabs.VisibleHeaderCount = Math.Max(1, (int)(stripWidth / 180));
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // NFR-07: unsaved edits and open tabs survive a close, a relaunch and a crash.
        _ = Shell?.Tabs.SaveSessionAsync();
        base.OnClosing(e);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasCapsule(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Shell is { } shell && HasCapsule(e))
        {
            shell.OpenDialog(DialogKind.CapsuleDrop);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Only a capsule is accepted. Dropping anything else is a no-op rather than an error dialog:
    /// the window is the drop target precisely so a tester with a capsule cannot miss, and that
    /// means most drops on it are accidental.
    /// </summary>
    private static bool HasCapsule(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Any(
            f => f.Name.EndsWith(Core.Capsules.CapsuleFormat.Extension, StringComparison.OrdinalIgnoreCase))
        ?? false;
}
