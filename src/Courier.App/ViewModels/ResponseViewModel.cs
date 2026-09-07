using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.Core.Http;
using Courier.Core.Rendering;

namespace Courier.App.ViewModels;

/// <summary>
/// A response, indexed for virtualized display. PERF-05, CORE-03, UI_SPEC 5.8.
/// </summary>
/// <remarks>
/// Owns the buffer and the index and releases both when the tab suspends, which is what keeps 100
/// tabs holding large responses inside PERF-02's 900MB.
/// </remarks>
public sealed partial class ResponseViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Only this much is rendered at once. The mock's banner says so plainly rather than pretending
    /// the whole 40MB is on screen: "Response truncated for display · first 4 MB shown".
    /// </summary>
    public const int DisplayLimitBytes = 4 * 1024 * 1024;

    private ResponseBuffer? _buffer;

    [ObservableProperty]
    private JsonTreeProjection? _tree;

    [ObservableProperty]
    private string _searchTerm = string.Empty;

    [ObservableProperty]
    private SearchResults _matches = SearchResults.Empty;

    [ObservableProperty]
    private int _currentMatch;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrettyMode))]
    [NotifyPropertyChangedFor(nameof(IsRawMode))]
    private ResponseMode _mode = ResponseMode.Pretty;

    [ObservableProperty]
    private bool _showEntireBody;

    private string? _rawTextCache;

    public ResponseViewModel(ExchangeResult result)
    {
        Result = result;
        Load();
    }

    public ExchangeResult Result { get; }

    public JsonIndex? Index { get; private set; }

    public bool IsPrettyMode => Mode == ResponseMode.Pretty;

    public bool IsRawMode => Mode == ResponseMode.Raw;

    /// <summary>Request headers, one row per value — a repeated header name appears as repeated rows.</summary>
    public IReadOnlyList<HeaderRow> RequestHeaders { get; private set; } = [];

    public IReadOnlyList<HeaderRow> ResponseHeaders { get; private set; } = [];

    /// <summary>The raw text view, cached — a 4MB decode on every tab flip would defeat PERF-03.</summary>
    public string RawBodyText => _rawTextCache ??= RawText();

    [RelayCommand]
    private void SetMode(string mode)
    {
        if (Enum.TryParse<ResponseMode>(mode, out var parsed))
        {
            Mode = parsed;
        }
    }

    /// <summary>True when the body is larger than the display limit. Drives the banner.</summary>
    public bool IsTruncatedForDisplay { get; private set; }

    public long ContentLength => Result.Response?.ContentLength ?? 0;

    public int Status => Result.Response?.Status ?? 0;

    public bool TransportFailed => Result.Outcome == ExchangeOutcome.TransportFailure;

    /// <summary>"200 OK", "500 Internal Server Error", or the failure kind when nothing came back.</summary>
    public string StatusText => Result.Outcome switch
    {
        ExchangeOutcome.Completed => $"{Result.Response!.Status} {Result.Response.ReasonPhrase}".Trim(),
        ExchangeOutcome.TransportFailure => "request never left",
        ExchangeOutcome.Cancelled => "cancelled",
        ExchangeOutcome.Refused => "refused by Courier",
        _ => "unknown",
    };

    public string ElapsedText => Result.Elapsed.TotalSeconds >= 1
        ? $"{Result.Elapsed.TotalSeconds:0.#} s"
        : $"{(int)Result.Elapsed.TotalMilliseconds} ms";

    public string SizeText => FormatSize(ContentLength);

    /// <summary>"40.2 MB · 118,402 nodes · collapsed past depth 2" for the large-payload header.</summary>
    public string StructureText => Index is null
        ? SizeText
        : $"{SizeText} · {Index.Count:N0} nodes · collapsed past depth {JsonTreeProjection.DefaultCollapseDepth}";

    public string MatchesText => Matches.Total switch
    {
        0 when SearchTerm.Length > 0 => "no matches",
        0 => string.Empty,
        1 => "1 match",
        _ => $"{Matches.Total:N0} matches",
    };

    public string MatchPositionText => Matches.Total == 0
        ? string.Empty
        : $"‹ {CurrentMatch + 1} of {Matches.Total:N0} ›";

    /// <summary>Breadcrumb for the selected node: <c>results › [418] › lines › [2]</c>.</summary>
    public string BreadcrumbFor(int nodeIndex)
    {
        if (Index is null || _buffer is null)
        {
            return string.Empty;
        }

        return string.Join(" › ", Index.PathTo(nodeIndex, Span));
    }

    public ReadOnlySpan<byte> Span => _buffer is null
        ? []
        : _buffer.AsSpan(0, ShowEntireBody ? -1 : DisplayLimitBytes);

    /// <summary>
    /// Materializes one visible row. Lives here because the buffer may be a memory-mapped view and
    /// a span cannot leave the stack — this is the only place that can hand the row builder a view
    /// of the bytes without copying them.
    /// </summary>
    public Controls.JsonRow? CreateRow(int nodeIndex, bool expanded) =>
        Index is null ? null : Controls.JsonRow.Create(Index, Span, nodeIndex, expanded);

    private void Load()
    {
        RequestHeaders = [.. Result.Request.Headers.Select(h => new HeaderRow(h.Key, h.Value))];

        var response = Result.Response;
        if (response is null)
        {
            return;
        }

        ResponseHeaders = [.. response.Headers.Select(h => new HeaderRow(h.Key, h.Value))];

        _buffer = response.BodyPath is not null
            ? ResponseBuffer.FromFile(response.BodyPath, deleteOnDispose: false)
            : ResponseBuffer.FromBytes(response.Body ?? []);

        IsTruncatedForDisplay = _buffer.Length > DisplayLimitBytes;

        if (LooksLikeJson(response.ContentType))
        {
            Index = JsonIndex.TryBuild(Span);
            Tree = Index is null ? null : new JsonTreeProjection(Index);
        }
    }

    /// <summary>Loads the full body after the truncation banner's "Load all". PERF-05.</summary>
    public void LoadEverything()
    {
        if (!IsTruncatedForDisplay || ShowEntireBody)
        {
            return;
        }

        ShowEntireBody = true;
        Index = JsonIndex.TryBuild(Span);
        Tree = Index is null ? null : new JsonTreeProjection(Index);
        _rawTextCache = null;
        OnPropertyChanged(nameof(StructureText));
        OnPropertyChanged(nameof(RawBodyText));
        Search(SearchTerm);
    }

    /// <summary>
    /// Search runs over the raw buffer, not the tree. That is the only reason a live match count
    /// over 40MB is affordable.
    /// </summary>
    public void Search(string term)
    {
        SearchTerm = term;
        Matches = string.IsNullOrEmpty(term) ? SearchResults.Empty : BodySearch.Find(Span, term);
        CurrentMatch = 0;

        OnPropertyChanged(nameof(MatchesText));
        OnPropertyChanged(nameof(MatchPositionText));
    }

    /// <summary>Moves to the next hit, expanding whatever collapsed branch contains it.</summary>
    public int MoveToMatch(int delta)
    {
        if (Matches.Offsets.Count == 0 || Index is null || Tree is null)
        {
            return -1;
        }

        CurrentMatch = (CurrentMatch + delta + Matches.Offsets.Count) % Matches.Offsets.Count;
        OnPropertyChanged(nameof(MatchPositionText));

        var node = BodySearch.NodeContaining(Index, Matches.Offsets[CurrentMatch]);
        return node < 0 ? -1 : Tree.RevealAndFindRow(node);
    }

    /// <summary>The raw text view. Capped, because a 40MB string in a TextBox is not a view.</summary>
    public string RawText()
    {
        var span = Span;
        var take = Math.Min(span.Length, DisplayLimitBytes);
        return Encoding.UTF8.GetString(span[..take]);
    }

    /// <summary>Drops the buffer and index when the owning tab suspends. PERF-02.</summary>
    public void Release()
    {
        _buffer?.Dispose();
        _buffer = null;
        Index = null;
        Tree = null;
    }

    public void Dispose() => Release();

    private static bool LooksLikeJson(string? contentType) =>
        contentType is null
        || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase);

    internal static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}

public enum ResponseMode
{
    Pretty,
    Raw,
    Preview,
}

/// <summary>One header row for the Headers tab. A plain record rather than a raw KeyValuePair so
/// compiled bindings have a named type to resolve <c>Name</c>/<c>Value</c> against.</summary>
public sealed record HeaderRow(string Name, string Value);
