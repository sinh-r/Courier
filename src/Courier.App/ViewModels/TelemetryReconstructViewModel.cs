using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Courier.App.ViewModels;

/// <summary>
/// Search a monitoring backend and turn a result into a runnable request. TEL-04.
/// </summary>
public sealed partial class TelemetryReconstructViewModel : ObservableObject
{
    [ObservableProperty]
    private string _backendName = "App Insights";

    [ObservableProperty]
    private string _resourceName = string.Empty;

    [ObservableProperty]
    private string _traceId = string.Empty;

    [ObservableProperty]
    private string _urlContains = string.Empty;

    [ObservableProperty]
    private TimeSpan _window = TimeSpan.FromHours(24);

    [ObservableProperty]
    private bool _isSearching;

    public ObservableCollection<TelemetryResultViewModel> Results { get; } = [];

    public string BackendSummary => $"{BackendName} · {ResourceName}";
}

/// <param name="Fidelity">
/// TEL-05. A backend only holds what the application logged, so this says whether the reconstructed
/// request will carry a real body or just the route and headers.
/// </param>
public sealed record TelemetryResultViewModel(
    string Time,
    string Method,
    string Path,
    int Status,
    ReconstructionFidelity Fidelity,
    string Source)
{
    /// <summary>Filled circle for full, half circle for partial. Glyph and text, never colour alone.</summary>
    public string FidelityGlyph => Fidelity == ReconstructionFidelity.FullBody ? "●" : "◐";

    public string FidelityLabel => Fidelity == ReconstructionFidelity.FullBody
        ? "full request body"
        : "headers and route only";
}

public enum ReconstructionFidelity
{
    /// <summary>The request body was logged and can be replayed exactly.</summary>
    FullBody,

    /// <summary>Route, method and headers only. Body fields arrive as placeholders to fill in.</summary>
    RouteAndHeaders,
}
