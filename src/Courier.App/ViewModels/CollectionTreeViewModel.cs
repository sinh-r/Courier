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
    private readonly CollectionSerializer _serializer = new();

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private string? _folder;

    [ObservableProperty]
    private string? _collectionName;

    [ObservableProperty]
    private TreeNode? _selected;

    public CollectionTreeViewModel(AppServices services) => _services = services;

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

        var requests = await LoadRequestsAsync(folder, ct).ConfigureAwait(true);
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

        await RefreshGitStatusAsync(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Loads the generated set, the overlay, and any hand-authored request files, then joins them.
    /// The join happens in memory on every load, which is what makes SCAN-09 and P3 true:
    /// regenerating cannot lose an edit because the two files are never merged on disk.
    /// </summary>
    private async Task<IReadOnlyList<RequestDefinition>> LoadRequestsAsync(string folder, CancellationToken ct)
    {
        var requests = new List<RequestDefinition>();

        var generatedPath = Path.Combine(folder, CollectionFormat.GeneratedFileName);
        var overlayPath = Path.Combine(folder, CollectionFormat.OverlayFileName);

        if (File.Exists(generatedPath))
        {
            var generated = _serializer.DeserializeGenerated(
                await File.ReadAllTextAsync(generatedPath, ct).ConfigureAwait(false));

            var overlay = File.Exists(overlayPath)
                ? _serializer.DeserializeOverlay(await File.ReadAllTextAsync(overlayPath, ct).ConfigureAwait(false))
                : new OverlaySet();

            requests.AddRange(GeneratedOverlayJoiner.Join(generated, overlay));
        }

        var requestsFolder = Path.Combine(folder, CollectionFormat.RequestsFolder);
        if (Directory.Exists(requestsFolder))
        {
            foreach (var file in Directory.EnumerateFiles(
                requestsFolder,
                $"*{CollectionFormat.RequestFileExtension}",
                SearchOption.AllDirectories))
            {
                try
                {
                    var request = _serializer.DeserializeRequest(
                        await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));

                    request.Folder ??= Path.GetRelativePath(requestsFolder, Path.GetDirectoryName(file)!)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .TrimStart('.');

                    requests.Add(request);
                }
                catch (CollectionFormatException)
                {
                    // A file Courier cannot read is listed as unreadable rather than dropped, for
                    // the same reason an unresolvable endpoint is: silence is the hostile outcome.
                    requests.Add(new RequestDefinition
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        UnresolvedNotes = ["This file could not be read as a Courier request."],
                    });
                }
            }
        }

        return requests;
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
