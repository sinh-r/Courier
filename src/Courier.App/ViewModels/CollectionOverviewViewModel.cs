using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.Core.Collections;

namespace Courier.App.ViewModels;

/// <summary>
/// The collection's own page — what clicking the collection root in the tree opens in place of
/// the request and response panes, the way Postman opens a collection. Overview (description and
/// what the collection holds), Auth: the default every request left on "Inherit from collection"
/// resolves to, edited through <see cref="MainWindowViewModel.CollectionAuth"/>, and Environments,
/// edited through the shell's <see cref="MainWindowViewModel.Environments"/>.
/// A snapshot of <see cref="CollectionDefinition"/> taken when the page opens; rebuilt on reopen.
/// </summary>
public sealed partial class CollectionOverviewViewModel : ObservableObject
{
    public const int OverviewTab = 0;
    public const int AuthTab = 1;
    public const int EnvironmentsTab = 2;

    private string _savedDescription;

    public CollectionOverviewViewModel(
        string folder,
        CollectionDefinition definition,
        TreeNode? root,
        IReadOnlyList<string> environments)
    {
        Folder = folder;
        Definition = definition;
        Name = root?.Name ?? definition.Name;
        _savedDescription = definition.Description ?? string.Empty;
        _description = _savedDescription;
        RequestCount = root?.ChildRequestCount ?? 0;
        FolderCount = root is null ? 0 : CountFolders(root);
        _environments = environments;
        Variables = [.. definition.Variables.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => new CollectionValueRow(v.Key, v.Value))];
        Headers = [.. definition.Headers.Select(h => new CollectionValueRow(h.Name, h.Value, h.Enabled))];
        ScannedFrom = definition.ScannedFrom is { Path.Length: > 0 } scan
            ? scan.LastScannedUtc is { } at
                ? $"{scan.Path} · last scanned {at.ToLocalTime():yyyy-MM-dd HH:mm}"
                : scan.Path
            : null;
        AuthSummary = Describe(definition.Auth);
    }

    public string Folder { get; }

    /// <summary>The collection.yaml this page edits — its own, not whichever collection happens to
    /// be current by the time Save is clicked.</summary>
    public CollectionDefinition Definition { get; }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _description;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _authSummary;

    [ObservableProperty]
    private string? _status;

    public bool IsDirty => !string.Equals(Description, _savedDescription, StringComparison.Ordinal);

    public int RequestCount { get; }

    public int FolderCount { get; }

    public string ContentsLine =>
        $"{RequestCount} request{(RequestCount == 1 ? string.Empty : "s")} · {FolderCount} folder{(FolderCount == 1 ? string.Empty : "s")}";

    public string? ScannedFrom { get; }

    /// <summary>Refreshed by the shell when an environment is created from this page's own
    /// Environments tab, so the overview line never lags behind it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnvironmentsLine))]
    private IReadOnlyList<string> _environments;

    public string EnvironmentsLine => Environments.Count == 0 ? "none" : string.Join(", ", Environments);

    public IReadOnlyList<CollectionValueRow> Variables { get; }

    public IReadOnlyList<CollectionValueRow> Headers { get; }

    /// <summary>The overview's "Edit" beside the auth line.</summary>
    [RelayCommand]
    private void ShowAuth() => SelectedTabIndex = AuthTab;

    /// <summary>The overview's "Manage" beside the environments line.</summary>
    [RelayCommand]
    private void ShowEnvironments() => SelectedTabIndex = EnvironmentsTab;

    /// <summary>Called once the description and auth are written to <c>collection.yaml</c>.</summary>
    public void MarkSaved(AuthReference? auth)
    {
        _savedDescription = Description;
        AuthSummary = Describe(auth);
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>One line for "what does Inherit resolve to" — the overview and a request's Auth
    /// tab both show it.</summary>
    public static string Describe(AuthReference? auth) => auth switch
    {
        { Mode: AuthMode.Profile, Profile: { Length: > 0 } name } => $"profile '{name}'",
        { Mode: AuthMode.Inline, Inline: { } inline } => AuthKindOptions.LabelFor(inline.Kind),
        _ => "No auth",
    };

    private static int CountFolders(TreeNode node) =>
        node.Children.Count(c => c.Kind == TreeNodeKind.Folder)
        + node.Children.Where(c => c.Kind == TreeNodeKind.Folder).Sum(CountFolders);
}

/// <summary>A collection variable or header, shown read-only on the overview.</summary>
public sealed record CollectionValueRow(string Name, string Value, bool Enabled = true);
