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

    /// <summary>
    /// The collection the shell is pointed at: whichever one was last selected in the tree, opened,
    /// or owns the active tab. <see cref="Folder"/>, <see cref="CollectionName"/> and
    /// <see cref="Definition"/> all read through it, so the environment picker, the header's
    /// "New folder" and everything else built for one open collection follow it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Folder))]
    [NotifyPropertyChangedFor(nameof(CollectionName))]
    [NotifyPropertyChangedFor(nameof(Definition))]
    private OpenCollection? _current;

    public string? Folder => Current?.Folder;

    public string? CollectionName => Current?.Name;

    /// <summary>
    /// <c>collection.yaml</c> for the current collection. Carries the shared variables, default
    /// headers and settings CORE-04/CORE-11 promise a collection applies to every request in it.
    /// </summary>
    public CollectionDefinition? Definition => Current?.Definition;

    /// <summary>Every collection open in the tree, in the order they were opened.</summary>
    public ObservableCollection<OpenCollection> Collections { get; } = [];

    /// <summary>The environment a collection starts on the first time it is opened in this tree —
    /// set by the shell, which knows what was picked last session.</summary>
    public Func<string, string>? InitialEnvironmentFor { get; set; }

    [ObservableProperty]
    private TreeNode? _selected;

    /// <summary>Selecting anything in a collection — its root, a folder, a request — makes that
    /// collection current.</summary>
    partial void OnSelectedChanged(TreeNode? value)
    {
        if (value?.Collection is { } collection)
        {
            Current = collection;
        }
    }

    /// <summary>The open collection at <paramref name="folder"/>, if there is one.</summary>
    public OpenCollection? Find(string? folder) =>
        folder is null ? null : Collections.FirstOrDefault(c => OpenCollection.IsSameFolder(c.Folder, folder));

    /// <summary>The open collection a file belongs to — for a tab restored from before tabs
    /// recorded their collection, which still has the path of the request file it came from.</summary>
    public OpenCollection? FindContaining(string? path) =>
        path is null ? null : Collections.FirstOrDefault(c => OpenCollection.Contains(c.Folder, path));

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
    /// Opens a collection folder alongside whatever is already open, and makes it current. Opening
    /// one that is already open reloads it in place — same position in the tree, same environment
    /// — which is also how a save refreshes it. Nothing is imported or copied: the files stay where
    /// the user put them, which is the promise the first-run screen makes.
    /// </summary>
    public async Task<OpenCollection> OpenFolderAsync(string folder, CancellationToken ct = default)
    {
        folder = OpenCollection.Normalize(folder);
        var name = Path.GetFileName(folder);

        // Loaded the same way the CLI loads it, so collection.yaml's variables, headers and
        // settings are finally something the GUI can see too, not just courier run.
        var loaded = await Task.Run(() => CollectionLoader.Load(folder), ct).ConfigureAwait(true);

        var collection = Find(folder);
        if (collection is null)
        {
            collection = new OpenCollection(folder, name)
            {
                EnvironmentName = InitialEnvironmentFor?.Invoke(folder) ?? "None",
            };
            Collections.Add(collection);
        }

        collection.Definition = loaded.Definition;
        var root = BuildRoot(collection, loaded);
        var previous = collection.Root;
        collection.Root = root;

        var index = previous is null ? -1 : Roots.IndexOf(previous);
        if (index >= 0)
        {
            Roots[index] = root;
        }
        else
        {
            Roots.Add(root);
        }

        OnPropertyChanged(nameof(IsEmpty));
        ApplyFilter();

        // Raises the property change even when it was already current: Definition was just
        // replaced underneath it, and anything reading through Current needs to know.
        if (Current == collection)
        {
            OnPropertyChanged(nameof(Current));
            OnPropertyChanged(nameof(Definition));
        }
        else
        {
            Current = collection;
        }

        await RefreshGitStatusAsync(collection, ct).ConfigureAwait(true);
        return collection;
    }

    /// <summary>Takes a collection out of the tree. Its files are untouched — this is the
    /// workspace forgetting it, not a delete.</summary>
    public void Close(OpenCollection collection)
    {
        Collections.Remove(collection);

        if (collection.Root is { } root)
        {
            Roots.Remove(root);
        }

        if (Current == collection)
        {
            Current = Collections.LastOrDefault();
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private static TreeNode BuildRoot(OpenCollection collection, LoadedCollection loaded)
    {
        var requests = loaded.Requests;

        var root = new TreeNode(collection.Name, TreeNodeKind.Collection) { Collection = collection };

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
                    Collection = collection,
                });
            }
        }

        return root;
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

        if (path.Length == 0 || Folder is not { } folder)
        {
            return;
        }

        var definition = Definition ?? new CollectionDefinition { Name = CollectionName ?? "Collection" };

        if (!definition.Folders.Contains(path, StringComparer.Ordinal))
        {
            definition.Folders.Add(path);
            CollectionLoader.SaveCollectionDefinition(folder, definition);
        }

        NewFolderName = string.Empty;
        await OpenFolderAsync(folder).ConfigureAwait(true);
    }

    /// <summary>
    /// Inline git status, for every open collection. Debounced by the caller and never run at
    /// startup — PERF-01 has no room for a git status on a cold HDD-backed image.
    /// </summary>
    public async Task RefreshGitStatusAsync(CancellationToken ct = default)
    {
        foreach (var collection in Collections.ToList())
        {
            await RefreshGitStatusAsync(collection, ct).ConfigureAwait(true);
        }
    }

    private async Task RefreshGitStatusAsync(OpenCollection collection, CancellationToken ct)
    {
        if (collection.Root is not { } root)
        {
            return;
        }

        var states = await _services.Git.GetStatusAsync(collection.Folder, ct).ConfigureAwait(true);
        if (states.Count == 0)
        {
            return;
        }

        foreach (var node in Flatten(root))
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

    /// <summary>The collection this node belongs to — every node has one, so a context-menu action
    /// on any of them knows which of the open collections it is acting on.</summary>
    public OpenCollection? Collection { get; init; }

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

    /// <summary>Drives the collection-only context-menu items: Environments, Close.</summary>
    public bool IsCollection => Kind == TreeNodeKind.Collection;

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
                existing = new TreeNode(segment, TreeNodeKind.Folder)
                {
                    FolderPath = string.Join('/', accumulated),
                    Collection = Collection,
                };
                current.Children.Add(existing);
            }

            current = existing;
        }

        return current;
    }
}

/// <summary>
/// One collection open in the tree: its folder, its <c>collection.yaml</c>, its root node, and the
/// environment it sends with. Each collection keeps its own environment choice, so switching
/// between two open collections never sends one with the other's <c>baseUrl</c>.
/// </summary>
public sealed class OpenCollection(string folder, string name)
{
    public string Folder { get; } = folder;

    public string Name { get; } = name;

    public CollectionDefinition Definition { get; set; } = new() { Name = name };

    public TreeNode? Root { get; set; }

    /// <summary>"None", or one of this collection's environment names.</summary>
    public string EnvironmentName { get; set; } = "None";

    /// <summary>Full path, no trailing separator — the one form folders are compared and
    /// remembered in.</summary>
    public static string Normalize(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    public static bool IsSameFolder(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    public static bool Contains(string folder, string path)
    {
        var prefix = Normalize(folder) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, PathComparison);
    }

    /// <summary>For keying anything by a <see cref="Normalize"/>d folder.</summary>
    public static StringComparer FolderComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}

public enum TreeNodeKind
{
    Collection,
    Folder,
    Request,
}
