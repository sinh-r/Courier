using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    /// <summary>Bound to the header's "New folder…" flyout textbox. Accepts <c>Parent/Child</c> for
    /// a one-shot nested create, the same convention <see cref="TreeNode.EnsureFolder"/> uses.</summary>
    [ObservableProperty]
    private string _newFolderName = string.Empty;

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

        // Declared-but-empty folders (CollectionDefinition.Folders) get seeded before the
        // request-derived ones below, so a folder nobody has filed anything into yet still shows
        // up — EnsureFolder reuses the node either way, so order between the two doesn't matter.
        foreach (var declared in loaded.Definition.Folders)
        {
            root.EnsureFolder(declared);
        }

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
    /// Declares <see cref="NewFolderName"/> in <c>collection.yaml</c> and reloads. The header's
    /// "New folder…" flyout — organizational structure a hand-built collection can have before
    /// anything is filed into it, the way an empty folder in Postman works.
    /// </summary>
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var path = NewFolderName.Trim().Trim('/');

        if (path.Length == 0 || Folder is null)
        {
            return;
        }

        var definition = Definition ?? new CollectionDefinition { Name = CollectionName ?? "Collection" };

        if (!definition.Folders.Contains(path, StringComparer.Ordinal))
        {
            definition.Folders.Add(path);
            CollectionLoader.SaveCollectionDefinition(Folder, definition);
        }

        NewFolderName = string.Empty;
        await OpenFolderAsync(Folder).ConfigureAwait(true);
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

    /// <summary>
    /// This node's own slash-separated folder path, e.g. <c>"Articles/Nested"</c> — set by
    /// <see cref="EnsureFolder"/> as it walks segments, since nothing else tracks a parent
    /// reference. Null for a Request or Collection node; the "New request" context menu reads this
    /// to know where a request created under this node belongs.
    /// </summary>
    public string? FolderPath { get; init; }

    /// <summary>Collection and Folder nodes can hold a new request or a nested folder; a Request
    /// node cannot. Drives the "New request" context-menu item's visibility.</summary>
    public bool CanContainChildren => Kind != TreeNodeKind.Request;

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
        var accumulated = new List<string>();

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated.Add(segment);

            var existing = current.Children.FirstOrDefault(
                c => c.Kind == TreeNodeKind.Folder && c.Name == segment);

            if (existing is null)
            {
                existing = new TreeNode(segment, TreeNodeKind.Folder) { FolderPath = string.Join('/', accumulated) };
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
