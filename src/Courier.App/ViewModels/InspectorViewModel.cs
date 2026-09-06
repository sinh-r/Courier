using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Courier.App.Services;
using Courier.Core.Auth;

namespace Courier.App.ViewModels;

/// <summary>
/// The right pane: Auth, Trace, History, Capsule, Certificate.
/// </summary>
/// <remarks>
/// UI_SPEC 4.3: sections remember their expanded state per user, not per tab. That is a small
/// decision with a large effect — a user who keeps Trace open wants it open on every request, not
/// to reopen it each time they switch.
/// </remarks>
public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private bool _authExpanded = true;

    [ObservableProperty]
    private bool _traceExpanded = true;

    [ObservableProperty]
    private bool _historyExpanded = true;

    [ObservableProperty]
    private bool _capsuleExpanded;

    [ObservableProperty]
    private bool _certificateExpanded;

    [ObservableProperty]
    private string _activeSection = "Auth";

    [ObservableProperty]
    private AuthProfile? _profile;

    [ObservableProperty]
    private DecodedToken? _token;

    [ObservableProperty]
    private string? _identity;

    [ObservableProperty]
    private string? _traceId;

    [ObservableProperty]
    private int _dependencyCount;

    [ObservableProperty]
    private int _exceptionCount;

    [ObservableProperty]
    private string? _certificateSubject;

    [ObservableProperty]
    private string? _certificateThumbprint;

    public InspectorViewModel(AppServices services) => _services = services;

    /// <summary>Recent calls for the active tab only. The bottom-pane variant shows all tabs.</summary>
    public ObservableCollection<Courier.Core.Storage.HistoryEntry> TabHistory { get; } = [];

    public string ExpiryText => Token?.DescribeExpiry() ?? "no token";

    public string ScopesText => Token is null || Token.Scopes.Count == 0
        ? "none"
        : string.Join(", ", Token.Scopes);

    /// <summary>
    /// Scopes the endpoint declares it needs that the token does not carry. Surfaced before the
    /// send (SCAN-06), which is what turns "why did I get a 403" into a five-second answer.
    /// </summary>
    public IReadOnlyList<string> MissingScopes(IEnumerable<string> required) =>
        Token?.MissingScopes(required) ?? [];

    public string MissingScopeWarning(IEnumerable<string> required)
    {
        var missing = MissingScopes(required);
        return missing.Count == 0
            ? string.Empty
            : $"{string.Join(", ", missing)} not granted — writes will 403";
    }

    /// <summary>Decodes the token behind the active request, for the panel. ENT-04.</summary>
    public void ShowToken(string? accessToken, string? identity)
    {
        Token = TokenDecoder.TryDecode(accessToken);
        Identity = identity;

        OnPropertyChanged(nameof(ExpiryText));
        OnPropertyChanged(nameof(ScopesText));
    }

    public void ShowTrace(string? traceId, int dependencies, int exceptions)
    {
        TraceId = traceId;
        DependencyCount = dependencies;
        ExceptionCount = exceptions;
    }

    public void ShowCertificate(string? subject, string? thumbprint)
    {
        CertificateSubject = subject;
        CertificateThumbprint = thumbprint;
    }

    /// <summary>Abbreviated trace id, as the mock shows it: 4a1f8e2b…c92.</summary>
    public string TraceIdShort => TraceId is null or { Length: < 12 }
        ? TraceId ?? string.Empty
        : $"{TraceId[..8]}…{TraceId[^3..]}";
}
