using System.ComponentModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Courier.App.ViewModels;
using Courier.Core.Collections;

namespace Courier.App.Controls;

/// <summary>
/// The body editor. CORE-02, PERF-04.
/// </summary>
/// <remarks>
/// TECH_SPEC 4 on PERF-04: "AvaloniaEdit handles large documents well, but syntax highlighting and
/// validation must be debounced off the UI thread. Never re-parse on every keystroke."
///
/// That is the whole design here. A keystroke touches the document and restarts a timer; the parse
/// happens 250ms after typing stops, on a worker, and only the resulting message crosses back to
/// the UI thread. At a 1MB document that keeps keystroke-to-render inside 16ms because the
/// keystroke path does no parsing at all.
/// </remarks>
public sealed partial class BodyEditor : UserControl
{
    private static readonly TimeSpan ValidationDelay = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _validationTimer;
    private CancellationTokenSource? _validation;
    private MainWindowViewModel? _shell;
    private TabViewModel? _boundTab;
    private bool _suppressStateWrites;

    public BodyEditor()
    {
        InitializeComponent();

        _validationTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ValidationDelay };
        _validationTimer.Tick += (_, _) =>
        {
            _validationTimer.Stop();
            _ = ValidateAsync();
        };

        Editor.TextChanged += (_, _) =>
        {
            // Restart rather than accumulate: the only parse that matters is the one after the
            // user stops typing.
            _validationTimer.Stop();
            _validationTimer.Start();

            if (!_suppressStateWrites && _boundTab?.State is { } state)
            {
                state.BodyText = Editor.Document?.Text;
                _boundTab.MarkDirty();
            }
        };

        KindPicker.SelectionChanged += (_, _) =>
        {
            if (!_suppressStateWrites && _boundTab?.State is { } state && KindPicker.SelectedIndex >= 0)
            {
                state.BodyKind = (BodyKind)KindPicker.SelectedIndex;
                _boundTab.MarkDirty();
            }
        };

        FormatButton.Click += (_, _) => Format();

        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// The body's DataContext is inherited from the shell far above, and does not change when the
    /// active tab does — so this listens to the tab collection itself rather than to its own
    /// DataContext, and reloads the editor's content each time the active tab changes.
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_shell is not null)
        {
            _shell.Tabs.PropertyChanged -= OnTabsPropertyChanged;
        }

        _shell = DataContext as MainWindowViewModel;

        if (_shell is not null)
        {
            _shell.Tabs.PropertyChanged += OnTabsPropertyChanged;
        }

        LoadFromActiveTab();
    }

    private void OnTabsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(TabCollection.Active))
        {
            LoadFromActiveTab();
        }
    }

    private void LoadFromActiveTab()
    {
        _boundTab = _shell?.Tabs.Active;
        var state = _boundTab?.State;

        _suppressStateWrites = true;

        Editor.Document = new TextDocument(state?.BodyText ?? string.Empty);

        var index = (int)(state?.BodyKind ?? BodyKind.Json);
        if (index is >= 0 and < 8)
        {
            KindPicker.SelectedIndex = index;
        }

        _suppressStateWrites = false;
    }

    /// <summary>Format-on-demand, never format-as-you-type. CORE-02.</summary>
    private void Format()
    {
        if (Editor.Document is not { } document || document.TextLength == 0)
        {
            return;
        }

        try
        {
            using var parsed = JsonDocument.Parse(document.Text);
            var formatted = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });

            // Preserve the caret line so formatting does not lose the user's place.
            var line = Editor.TextArea.Caret.Line;
            Editor.Document = new TextDocument(formatted);
            Editor.TextArea.Caret.Line = Math.Min(line, Editor.Document.LineCount);
        }
        catch (JsonException ex)
        {
            ValidationMessage.Text = Describe(ex);
        }
    }

    private async Task ValidateAsync()
    {
        _validation?.Cancel();
        _validation = new CancellationTokenSource();
        var token = _validation.Token;

        var text = Editor.Document?.Text ?? string.Empty;
        if (text.Length == 0)
        {
            ValidationMessage.Text = string.Empty;
            return;
        }

        // Off the UI thread. A 1MB payload takes long enough to parse that doing it inline would
        // be visible as a stutter, which PERF-04 treats as a P1 bug.
        var message = await Task.Run(
            () =>
            {
                try
                {
                    using var _ = JsonDocument.Parse(text);
                    return string.Empty;
                }
                catch (JsonException ex)
                {
                    return Describe(ex);
                }
            },
            token).ConfigureAwait(true);

        if (!token.IsCancellationRequested)
        {
            ValidationMessage.Text = message;
        }
    }

    /// <summary>States what is wrong and where. UI_SPEC 3.7: never "an error occurred".</summary>
    private static string Describe(JsonException ex) =>
        ex.LineNumber is { } line
            ? $"not valid JSON at line {line + 1}: {ex.Message.Split(". LineNumber")[0]}"
            : $"not valid JSON: {ex.Message}";
}
