using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Courier.App.Services;

namespace Courier.App.ViewModels;

/// <summary>
/// Ctrl+K. Fuzzy across endpoints, tabs, environments and commands. CORE-08.
/// </summary>
/// <remarks>
/// UI_SPEC 5.11: "This is how power users navigate 60 tabs, so it should feel like the fastest
/// thing in the app." Which means the matcher runs synchronously on the keystroke over an
/// in-memory list, with no async, no debounce and no allocation per candidate beyond the score.
/// </remarks>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly MainWindowViewModel _shell;
    private readonly List<PaletteEntry> _all = [];

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private int _selectedIndex;

    public CommandPaletteViewModel(MainWindowViewModel shell)
    {
        _shell = shell;
        RegisterCommands();
    }

    public ObservableCollection<PaletteEntry> Results { get; } = [];

    public string ResultCountLabel => Results.Count == 1 ? "1 result" : $"{Results.Count} results";

    /// <summary>The key hints along the bottom of the palette.</summary>
    public string KeyHints => "↑↓ move   ↵ open   ⇧↵ open in new tab   esc dismiss";

    public void Refresh()
    {
        _all.RemoveAll(e => e.Kind is PaletteKind.Endpoint or PaletteKind.Tab);

        foreach (var node in _shell.Tree.Roots.SelectMany(Flatten))
        {
            if (node.Request is { } request)
            {
                _all.Add(new PaletteEntry(
                    PaletteKind.Endpoint,
                    request.Method,
                    request.Url,
                    _shell.Tree.CollectionName ?? "collection",
                    () => _shell.OpenRequest(request)));
            }
        }

        foreach (var tab in _shell.Tabs.Items)
        {
            _all.Add(new PaletteEntry(
                PaletteKind.Tab,
                tab.Method,
                tab.Title,
                $"open · {tab.State?.EnvironmentName ?? _shell.EnvironmentName}",
                () => _shell.Tabs.Select(tab)));
        }

        Search(Query);
    }

    partial void OnQueryChanged(string value) => Search(value);

    public void Search(string query)
    {
        Query = query;
        Results.Clear();

        var scored = string.IsNullOrWhiteSpace(query)
            ? _all.Take(50)
            : _all
                .Select(entry => (Entry: entry, Score: FuzzyMatcher.Score(entry.SearchText, query)))
                .Where(pair => pair.Score > 0)
                .OrderByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Entry.Title.Length)
                .Take(50)
                .Select(pair => pair.Entry);

        foreach (var entry in scored)
        {
            Results.Add(entry);
        }

        SelectedIndex = Results.Count == 0 ? -1 : 0;
        OnPropertyChanged(nameof(ResultCountLabel));
    }

    public void Move(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        SelectedIndex = (SelectedIndex + delta + Results.Count) % Results.Count;
    }

    public void Activate()
    {
        if (SelectedIndex >= 0 && SelectedIndex < Results.Count)
        {
            Results[SelectedIndex].Invoke();
            _shell.CloseDialog();
        }
    }

    private void RegisterCommands()
    {
        void Add(string title, string hint, Action action) =>
            _all.Add(new PaletteEntry(PaletteKind.Command, "cmd", title, hint, action));

        Add("Cancel in-flight request", "Ctrl+.", () => { });
        Add("New request", "Ctrl+N", _shell.NewTab);
        Add("Close tab", "Ctrl+W", _shell.CloseActiveTab);
        Add("Import from code", string.Empty, () => _ = _shell.ImportFromCodeAsync());
        Add("Export capsule", string.Empty, () => _shell.OpenDialog(DialogKind.ExportCapsule));
        Add("Reconstruct from telemetry", string.Empty, () => _shell.OpenDialog(DialogKind.ReconstructFromTelemetry));
        Add("Environments", string.Empty, () => _shell.OpenDialog(DialogKind.Environments));
        Add("Auth profiles", string.Empty, () => _shell.OpenDialog(DialogKind.AuthProfile));
        Add("Trust and network", string.Empty, () => _shell.OpenDialog(DialogKind.TrustAndNetwork));
        Add("Where my data is stored", string.Empty, () => _shell.OpenDialog(DialogKind.Storage));
        Add("Toggle inspector", "Ctrl+I", _shell.ToggleInspector);
    }

    private static IEnumerable<TreeNode> Flatten(TreeNode node)
    {
        yield return node;

        foreach (var child in node.Children.SelectMany(Flatten))
        {
            yield return child;
        }
    }
}

/// <param name="Badge">Verb for an endpoint or tab; a type label for a command or environment.</param>
public sealed record PaletteEntry(PaletteKind Kind, string Badge, string Title, string Hint, Action Invoke)
{
    public string SearchText { get; } = $"{Badge} {Title} {Hint}";

    /// <summary>Verb colour for endpoints and tabs; muted for everything else.</summary>
    public Avalonia.Media.IBrush BadgeBrush => Kind is PaletteKind.Endpoint or PaletteKind.Tab
        ? VerbBrushes.For(Badge)
        : VerbBrushes.For("OPTIONS");
}

public enum PaletteKind
{
    Endpoint,
    Tab,
    Command,
    Environment,
}

/// <summary>
/// Subsequence matching with a bonus for consecutive runs and word boundaries.
/// </summary>
/// <remarks>
/// Deliberately simple. A full fuzzy library would score better on pathological inputs and cost
/// more per keystroke than the whole palette is allowed to; UI_SPEC 5.11 asks for the fastest
/// thing in the app, and over a few thousand candidates this is a single allocation-free pass.
/// </remarks>
public static class FuzzyMatcher
{
    public static int Score(string candidate, string query)
    {
        if (query.Length == 0)
        {
            return 1;
        }

        var score = 0;
        var candidateIndex = 0;
        var run = 0;

        foreach (var queryChar in query)
        {
            if (queryChar == ' ')
            {
                run = 0;
                continue;
            }

            var found = -1;

            for (var i = candidateIndex; i < candidate.Length; i++)
            {
                if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(queryChar))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return 0;
            }

            // Consecutive characters and word starts are what people actually type toward.
            run = found == candidateIndex ? run + 1 : 0;
            score += 1 + run;

            if (found == 0 || candidate[found - 1] is ' ' or '/' or '.' or '-' or '_')
            {
                score += 4;
            }

            candidateIndex = found + 1;
        }

        return score;
    }
}
