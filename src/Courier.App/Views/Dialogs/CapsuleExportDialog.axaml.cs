using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Courier.App.ViewModels;
using Courier.Core.Capsules;

namespace Courier.App.Views.Dialogs;

public sealed partial class CapsuleExportDialog : UserControl
{
    public CapsuleExportDialog()
    {
        InitializeComponent();

        SaveButton.Click += OnSaveClick;
        CopyButton.Click += OnCopyClick;
    }

    /// <summary>CAP-01: the review has already run by the time this fires, so the write is just bytes.</summary>
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CapsuleExportViewModel vm)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save capsule",
            SuggestedFileName = vm.SuggestedFileName,
            DefaultExtension = CapsuleFormat.Extension.TrimStart('.'),
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await vm.WriteAsync(stream);
    }

    /// <summary>
    /// "Copy" has no clipboard-friendly capsule bytes to offer — a capsule is a zip archive, not
    /// text — so this copies the one-line summary a bug report would otherwise have to retype.
    /// </summary>
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CapsuleExportViewModel vm)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(vm.FileSummary);
        }
    }
}
