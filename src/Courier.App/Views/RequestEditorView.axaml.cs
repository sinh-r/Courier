using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Courier.App.ViewModels;
using Courier.Core.Import;

namespace Courier.App.Views;

public sealed partial class RequestEditorView : UserControl
{
    public RequestEditorView()
    {
        InitializeComponent();
        UrlBox.AddHandler(TextBox.PastingFromClipboardEvent, OnPastingFromClipboard);
    }

    /// <summary>
    /// Pasting a curl command into the URL box imports it instead of pasting the raw text — the
    /// fastest route from a Slack message to a running request. CORE-09.
    /// </summary>
    private async void OnPastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel shell || shell.TopLevel?.Clipboard is not { } clipboard)
        {
            return;
        }

        var text = await clipboard.TryGetTextAsync();

        if (text is null || !CurlImporter.LooksLikeCurl(text))
        {
            return;
        }

        e.Handled = true;
        shell.ImportCurl(text);
    }
}
