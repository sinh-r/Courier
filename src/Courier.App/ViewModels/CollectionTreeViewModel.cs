using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Courier.App.Services;
using Courier.Core.Abstractions;
using Courier.Core.Collections;

namespace Courier.App.ViewModels;

/// <summary>
/// The collection tree: folders, requests, provenance markers and inline git status.
/// SCAN-12, STOR-05.
/// </summary>
public sealed partial class CollectionTreeViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private string? _folder;

    [ObservableProperty]
    private string? _collectionName;

    /// <summary>
    /// <c>collection.yaml</c> for the open folder. Carries the shared variables, default headers
    /// and settings CORE-04/CORE-11 promise a collection applies to every request in it.
    /// </summary>
    public CollectionDefinition? Definition { get; private set; }

    [ObservableProperty]
    private TreeNode? _selected;

    public CollectionTreeViewModel(AppServices services) => _services = services;

    partial void OnFilterChanged(string value) => ApplyFilter();

    /// <summary>
    /// Marks every node visible or not. A folder stays visible whenever anything under it matches,
    /// so filtering narrows the tree without ever hiding the path to a match.
    /// </summary>
    private void ApplyFilter()
    {
        foreach (var root in Roots)
        {
            ApplyFilter(root, Filter);
        }
    }

    private static bool ApplyFilter(TreeNode node, string filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            node.IsVisible = true;
            foreach (var child in node.Children)
            {
                ApplyFilter(child, filter);
            }

            return true;
        }

        var childVisible = false;
        foreach (var child in node.Children)
        {
            childVisible |= ApplyFilter(child, filter);
        }

        var selfMatches = node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        node.IsVisible = node.Kind == TreeNodeKind.Collection || selfMatches || childVisible;
        return node.IsVisible;
    }

    public ObservableCollection<TreeNode> Roots { get; } = [];

    /// <summary>The legend under the tree: ■ from code · ◧ edited · ○ yours.</summary>
    public IReadOnlyList<(string Glyph, string Label)> ProvenanceLegend =>
    [
        (VerbBrushes.ProvenanceGlyph(Provenance.Generated), VerbBrushes.ProvenanceLabel(Provenance.Generated)),
        (VerbBrushes.ProvenanceGlyph(Provenance.GeneratedEdited), VerbBrushes.ProvenanceLabel(Provenance.GeneratedEdited)),
        (VerbBrushes.ProvenanceGlyph(Provenance.Authored), VerbBrushes.ProvenanceLabel(Provenance.Authored)),
    ];

    public bool IsEmpty => Roots.Count == 0;

    /// <summary>
    /// Opens a folder of collections. Nothing is imported or copied: the files stay where the user
    /// put them, which is the promise the first-run screen makes.
    /// </summary>
    public async Task OpenFolderAsync(string folder, CancellationToken ct = default)
    {
        Folder = folder;
        CollectionName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        Roots.Clear();

        // Loaded the same way the CLI loads it, so collection.yaml's variables, headers and
        // settings are finally something the GUI can see too, not just courier run.
        var loaded = await Task.Run(() => CollectionLoader.Load(folder), ct).ConfigureAwait(true);
        Definition = loaded.Definition;
        var requests = loaded.Requests;

        var root = new TreeNode(CollectionName ?? "Collection", TreeNodeKind.Collection);

        foreach (var group in requests.GroupBy(r => r.Folder ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var parent = group.Key.Length == 0
                ? root
                : root.EnsureFolder(group.Key);

            foreach (var request in group.OrderBy(r => r.Name, StringComparer.Ordinal))
            {
                parent.Children.Add(new TreeNode(request.Name, TreeNodeKind.Request)
                {
                    Request = request,
                    Provenance = request.Provenance,
                });
            }
        }

        Roots.Add(root);
        OnPropertyChanged(nameof(IsEmpty));
        ApplyFilter();

        await RefreshGitStatusAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Inline git status. Debounced by the caller and never run at startup — PERF-01 has no room
    /// for a git status on a cold HDD-backed image.
    /// </summary>
    public async Task RefreshGitStatusAsync(CancellationToken ct = default)
    {
        if (Folder is null)
        {
            return;
        }

        var states = await _services.Git.GetStatusAsync(Folder, ct).ConfigureAwait(true);
        if (states.Count == 0)
        {
            return;
        }

        foreach (var node in Roots.SelectMany(Flatten))
        {
            if (node.RelativePath is { } path && states.TryGetValue(path, out var state))
            {
                node.GitState = state;
            }
        }
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

public sealed partial class TreeNode : ObservableObject
{
    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private GitFileState _gitState = GitFileState.Unchanged;

    public TreeNode(string name, TreeNodeKind kind)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; }

    public TreeNodeKind Kind { get; }

    public ObservableCollection<TreeNode> Children { get; } = [];

    public RequestDefinition? Request { get; init; }

    public Provenance Provenance { get; init; } = Provenance.Authored;

    public string? RelativePath { get; init; }

    public string ProvenanceGlyph => VerbBrushes.ProvenanceGlyph(Provenance);

    public string ProvenanceLabel => VerbBrushes.ProvenanceLabel(Provenance);

    /// <summary>The verb badge, in verb colour, left of the name. Often the only thing read.</summary>
    public string? Verb => Request?.Method switch
    {
        "DELETE" => "DEL",
        var method => method,
    };

    /// <summary>Inline git marker. Never colour alone: the glyph carries the meaning too.</summary>
    public string GitGlyph => GitState switch
    {
        GitFileState.Modified => "M",
        GitFileState.Added => "A",
        GitFileState.Deleted => "D",
        GitFileState.Renamed => "R",
        GitFileState.Untracked => "?",
        GitFileState.Conflicted => "!",
        _ => string.Empty,
    };

    public int ChildRequestCount => Children.Count(c => c.Kind == TreeNodeKind.Request)
        + Children.Where(c => c.Kind == TreeNodeKind.Folder).Sum(c => c.ChildRequestCount);

    public TreeNode EnsureFolder(string path)
    {
        var current = this;

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var existing = current.Children.FirstOrDefault(
                c => c.Kind == TreeNodeKind.Folder && c.Name == segment);

            if (existing is null)
            {
                existing = new TreeNode(segment, TreeNodeKind.Folder);
                current.Children.Add(existing);
            }

            current = existing;
        }

        return current;
    }
}

public enum TreeNodeKind
{
    Collection,
    Folder,
    Request,
}
