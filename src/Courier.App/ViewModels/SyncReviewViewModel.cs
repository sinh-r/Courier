using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Courier.App.Services;
using Courier.Core.Collections;
using Courier.Scanner;
using ScanDiffing = Courier.Scanner.Diffing;

namespace Courier.App.ViewModels;

/// <summary>
/// The sync review. SCAN-10: report added, changed and removed endpoints as a reviewable diff
/// before applying.
/// </summary>
public sealed partial class SyncReviewViewModel : ObservableObject
{
    /// <summary>Folder or .sln to scan. Set by the import command, editable before a rescan.</summary>
    [ObservableProperty]
    private string _sourcePath = string.Empty;

    /// <summary>Where the collection is written. Proposed by the import command, editable.</summary>
    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>"Syntax" or "Semantic". SCAN-08.</summary>
    [ObservableProperty]
    private string _tier = "Syntax";

    [ObservableProperty]
    private int _environmentCount;

    [NotifyPropertyChangedFor(nameof(Summary))]
    [ObservableProperty]
    private string _collection = string.Empty;

    [NotifyPropertyChangedFor(nameof(Summary))]
    [ObservableProperty]
    private DateTimeOffset _scannedUtc = DateTimeOffset.UtcNow;

    [NotifyPropertyChangedFor(nameof(Summary))]
    [ObservableProperty]
    private int _totalEndpoints;

    /// <summary>
    /// Checked by default, and it means what it says. The generator writes only
    /// endpoints.generated.yaml; the overlay holding the user's payloads is never touched.
    /// </summary>
    [ObservableProperty]
    private bool _keepUserWork = true;

    public ObservableCollection<EndpointChangeViewModel> Added { get; } = [];

    public ObservableCollection<EndpointChangeViewModel> Changed { get; } = [];

    public ObservableCollection<EndpointChangeViewModel> Removed { get; } = [];

    /// <summary>Endpoints the scanner found but could not fully derive. Listed, never dropped.</summary>
    public ObservableCollection<EndpointChangeViewModel> Unresolved { get; } = [];

    public int ChangeCount => Added.Count + Changed.Count + Removed.Count;

    public string ApplyLabel => $"Apply {ChangeCount} changes";

    /// <summary>"Orders.Api · scanned 2s ago · 47 endpoints".</summary>
    public string Summary => string.IsNullOrEmpty(Collection)
        ? "Choose a folder or .sln to scan"
        : $"{Collection} · scanned {Ago(ScannedUtc)} · {TotalEndpoints} endpoints";

    /// <summary>
    /// Turns a scan into what the screen shows. The collections are cleared and repopulated rather
    /// than replaced, so an in-place rescan does not tear down bindings the dialog already has.
    /// </summary>
    public void Load(ScanDiffing.ScanChangeSet changes, ScanResult result, string collectionName)
    {
        Collection = collectionName;
        ScannedUtc = DateTimeOffset.UtcNow;
        TotalEndpoints = result.Total;
        Tier = result.Tier.ToString();
        EnvironmentCount = result.Environments.Count;
        ErrorMessage = null;

        Added.Clear();
        foreach (var request in changes.Added)
        {
            Added.Add(new EndpointChangeViewModel(request.Method, request.Url, DetailForAdded(request), []));
        }

        Changed.Clear();
        foreach (var change in changes.Changed)
        {
            Changed.Add(new EndpointChangeViewModel(
                change.Endpoint.Method,
                change.Endpoint.Url,
                string.Empty,
                [.. change.Fields.Select(MapField)]));
        }

        Removed.Clear();
        foreach (var request in changes.Removed)
        {
            Removed.Add(new EndpointChangeViewModel(request.Method, request.Url, string.Empty, []));
        }

        Unresolved.Clear();
        foreach (var unresolved in result.Unresolved)
        {
            Unresolved.Add(new EndpointChangeViewModel(
                string.Empty,
                $"{unresolved.DeclaringType}.{unresolved.ActionName}",
                unresolved.Reason,
                []));
        }

        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(ApplyLabel));
    }

    private static string DetailForAdded(RequestDefinition request) => request.RequiredScopes.Count > 0
        ? $"requires {string.Join(", ", request.RequiredScopes)}"
        : request.Description ?? string.Empty;

    private static FieldChangeViewModel MapField(ScanDiffing.FieldChange field) => new(
        field.Section,
        field.Kind switch
        {
            ScanDiffing.FieldChangeKind.Added => FieldChangeKind.Added,
            ScanDiffing.FieldChangeKind.Removed => FieldChangeKind.Removed,
            _ => FieldChangeKind.Modified,
        },
        field.Description);

    private static string Ago(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.UtcNow - when;

        return elapsed switch
        {
            { TotalSeconds: < 60 } => $"{(int)elapsed.TotalSeconds}s ago",
            { TotalMinutes: < 60 } => $"{(int)elapsed.TotalMinutes}m ago",
            { TotalHours: < 24 } => $"{(int)elapsed.TotalHours}h ago",
            _ => $"{(int)elapsed.TotalDays}d ago",
        };
    }
}

/// <param name="Detail">
/// The right-hand note: the handler name for an added endpoint, the required scope, or why an
/// endpoint could not be resolved.
/// </param>
public sealed record EndpointChangeViewModel(
    string Method,
    string Route,
    string Detail,
    IReadOnlyList<FieldChangeViewModel> FieldChanges);

/// <param name="Section">"body" or "auth", the left-hand label in the mock's changed block.</param>
public sealed record FieldChangeViewModel(string Section, FieldChangeKind Kind, string Description)
{
    public string Marker => Kind switch
    {
        FieldChangeKind.Added => "+",
        FieldChangeKind.Removed => "−",
        _ => " ",
    };

    /// <summary>
    /// Added-vs-removed is one of the few places UI_SPEC 3.1 permits colour, because it carries
    /// meaning the reader would otherwise have to parse out of the text.
    /// </summary>
    public IBrush MarkerBrush => Kind switch
    {
        FieldChangeKind.Added => VerbBrushes.ForTrust(isSecret: false),
        FieldChangeKind.Removed => VerbBrushes.ForStatus(500),
        _ => VerbBrushes.For("OPTIONS"),
    };
}

public enum FieldChangeKind
{
    Added,
    Removed,
    Modified,
}
