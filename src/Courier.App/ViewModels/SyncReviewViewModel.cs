using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Courier.App.Services;

namespace Courier.App.ViewModels;

/// <summary>
/// The sync review. SCAN-10: report added, changed and removed endpoints as a reviewable diff
/// before applying.
/// </summary>
public sealed partial class SyncReviewViewModel : ObservableObject
{
    [ObservableProperty]
    private string _collection = "Orders.Api";

    [ObservableProperty]
    private DateTimeOffset _scannedUtc = DateTimeOffset.UtcNow;

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
    public string Summary => $"{Collection} · scanned {Ago(ScannedUtc)} · {TotalEndpoints} endpoints";

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
