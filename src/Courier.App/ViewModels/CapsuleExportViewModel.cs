using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.Core.Capsules;
using Courier.Core.Http;

namespace Courier.App.ViewModels;

/// <summary>
/// The mandatory pre-export review. CAP-04.
/// </summary>
/// <remarks>
/// Nothing is written until this has been shown, which is why the view model owns the decisions
/// rather than the export command. A flagged item with no decision defaults to redacted: the
/// conservative default is the one that cannot leak.
/// </remarks>
public sealed partial class CapsuleExportViewModel : ObservableObject
{
    private readonly RedactionEngine _engine;
    private readonly CapsuleRequest _request;
    private readonly CapsuleResponse? _response;
    private readonly RedactionReview _review;

    [ObservableProperty]
    private string? _note;

    public CapsuleExportViewModel(
        RedactionEngine engine,
        ExchangeResult result,
        string? environmentName,
        string? traceId,
        string clientVersion)
    {
        _engine = engine;

        var step = CapsuleProjection.FromExchange(result);
        _request = step.Request;
        _response = step.Response;
        _review = engine.Review(_request, _response);

        EnvironmentName = environmentName;
        TraceId = traceId;
        ClientVersion = clientVersion;
        Method = result.Request.Method;
        Route = result.Request.Url.AbsolutePath;
        Status = result.Response?.Status ?? 0;
        StatusText = result.Response is null
            ? "request never left"
            : $"{result.Response.Status} {result.Response.ReasonPhrase}".Trim();
        CapturedAt = result.StartedUtc.ToLocalTime().ToString("HH:mm:ss");

        foreach (var item in _review.Included)
        {
            Included.Add(item);
        }

        foreach (var item in _review.Replaced)
        {
            Replaced.Add(item);
        }

        foreach (var item in _review.Flagged)
        {
            Flagged.Add(new FlaggedDecisionViewModel(item, OnDecision));
        }
    }

    public string Method { get; }

    public string Route { get; }

    public int Status { get; }

    public string StatusText { get; }

    public string CapturedAt { get; }

    public string? EnvironmentName { get; }

    public string? TraceId { get; }

    public string ClientVersion { get; }

    public ObservableCollection<IncludedItem> Included { get; } = [];

    public ObservableCollection<ReplacedItem> Replaced { get; } = [];

    public ObservableCollection<FlaggedDecisionViewModel> Flagged { get; } = [];

    /// <summary>CAP-02 is explicit: the environment's name travels, its values never do.</summary>
    public string EnvironmentLine => $"{EnvironmentName ?? "none"} · values not included";

    public string FileSummary => $"{SuggestedFileName} · {Replaced.Count + Flagged.Count} replacements";

    public string SuggestedFileName
    {
        get
        {
            var slug = Core.Collections.CollectionFormat.Slug($"{Route} {Status}");
            return $"{slug}{CapsuleFormat.Extension}";
        }
    }

    /// <summary>True once every flagged item has an explicit decision. CAP-04.</summary>
    public bool AllDecided => Flagged.All(f => f.Decided);

    /// <summary>Writes the capsule. Only reachable after the review has been rendered.</summary>
    public async Task WriteAsync(Stream destination, CancellationToken ct = default)
    {
        var decisions = Flagged.ToDictionary(f => f.Location, f => f.Keep);
        var (request, response, report) = _engine.Apply(_request, _response, _review, decisions);

        var manifest = new CapsuleManifest
        {
            Title = $"{Method} {Route}",
            EnvironmentName = EnvironmentName,
            TraceId = TraceId,
            ClientVersion = ClientVersion,
            Note = Note,
            CapturedUtc = DateTimeOffset.UtcNow,
        };

        await CapsuleArchive.WriteAsync(
            destination,
            manifest,
            [new CapsuleStep { Ordinal = 1, Name = manifest.Title, Request = request, Response = response }],
            report,
            ct).ConfigureAwait(false);
    }

    private void OnDecision() => OnPropertyChanged(nameof(AllDecided));
}

/// <summary>One flagged item awaiting a human decision. Redact or Keep; there is no third option.</summary>
public sealed partial class FlaggedDecisionViewModel : ObservableObject
{
    private readonly Action _onDecision;

    [ObservableProperty]
    private bool _keep;

    [ObservableProperty]
    private bool _decided;

    public FlaggedDecisionViewModel(FlaggedItem item, Action onDecision)
    {
        _onDecision = onDecision;
        Location = item.Location;
        Preview = item.Preview;
        Reason = item.Reason;
    }

    public string Location { get; }

    public string Preview { get; }

    public string Reason { get; }

    [RelayCommand]
    private void Redact()
    {
        Keep = false;
        Decided = true;
        _onDecision();
    }

    [RelayCommand]
    private void KeepValue()
    {
        Keep = true;
        Decided = true;
        _onDecision();
    }
}
