using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.Core.Abstractions;
using Courier.Core.Auth;
using Courier.Core.Privacy;

namespace Courier.App.ViewModels;

/// <summary>
/// The environments table. SEC-03, CORE-12.
/// </summary>
public sealed partial class EnvironmentsViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "QA-Internal";

    [ObservableProperty]
    private string _filePath = string.Empty;

    public ObservableCollection<EnvironmentVariableViewModel> Variables { get; } = [];
}

/// <summary>
/// One variable, in one of two columns. The split is the point: shared goes in git, local goes to
/// the credential store, and the two are never the same slot.
/// </summary>
public sealed partial class EnvironmentVariableViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string? _sharedValue;

    [ObservableProperty]
    private bool _isLocal;

    [ObservableProperty]
    private bool _warningDismissed;

    public string SharedDisplay => SharedValue ?? "—";

    /// <summary>A local value is located, never displayed. P2.</summary>
    public string LocalDisplay => IsLocal ? "••••••••" : "—";

    public string LocalSource => IsLocal ? "Credential Manager" : string.Empty;

    /// <summary>
    /// A secret-shaped value sitting in the shared column. SEC-03 requires a warning, and the
    /// warning has to be actionable rather than advisory, hence the two buttons beside it.
    /// </summary>
    public bool HasSharedSecretWarning =>
        !WarningDismissed
        && !IsLocal
        && SecretPatterns.Classify(SharedValue, Name).IsSecret;

    [RelayCommand]
    private void MoveToLocal()
    {
        IsLocal = true;
        SharedValue = null;
        Refresh();
    }

    /// <summary>
    /// The user overriding a false positive. The bias toward false positives (REQUIREMENTS 9) is
    /// only tolerable if dismissing one is a single click and it stays dismissed.
    /// </summary>
    [RelayCommand]
    private void DismissWarning()
    {
        WarningDismissed = true;
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(SharedDisplay));
        OnPropertyChanged(nameof(LocalDisplay));
        OnPropertyChanged(nameof(LocalSource));
        OnPropertyChanged(nameof(HasSharedSecretWarning));
    }
}

/// <summary>The auth profile editor and the decoded token panel. ENT-02, ENT-04.</summary>
public sealed partial class AuthProfileEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _tenant;

    [ObservableProperty]
    private string? _clientId;

    [ObservableProperty]
    private DecodedToken? _token;

    public ObservableCollection<string> Scopes { get; } = [];

    public ObservableCollection<TokenClaimViewModel> Claims { get; } = [];

    public string ScopeLine => Scopes.Count == 0 ? "none" : string.Join(" ", Scopes);

    /// <summary>"issued 13:15 · expires 14:49 · in 47m".</summary>
    public string TokenTimingLine => Token is null
        ? "no token yet"
        : $"issued {Token.IssuedAt:HH:mm} · expires {Token.ExpiresAt:HH:mm} · {Token.DescribeExpiry()}";

    /// <summary>
    /// Rebuilds the claim table. The warning column is where SCAN-06 meets ENT-04: a scope the
    /// endpoint declares but the token does not carry becomes a sentence before the send, not a 403
    /// after it.
    /// </summary>
    public void Show(DecodedToken? token, IReadOnlyList<string> requiredScopes)
    {
        Token = token;
        Claims.Clear();

        if (token is null)
        {
            return;
        }

        var missing = token.MissingScopes(requiredScopes);

        Claims.Add(new TokenClaimViewModel("aud", token.Audience ?? "—", null));
        Claims.Add(new TokenClaimViewModel("iss", token.Issuer ?? "—", null));
        Claims.Add(new TokenClaimViewModel(
            "scp",
            string.Join(" ", token.Scopes),
            missing.Count == 0 ? null : $"▲ {string.Join(", ", missing)} not granted — writes will 403"));
        Claims.Add(new TokenClaimViewModel("roles", string.Join(", ", token.Roles), null));
        Claims.Add(new TokenClaimViewModel("upn", token.UserPrincipalName ?? "—", null));
        Claims.Add(new TokenClaimViewModel(
            "exp",
            token.ExpiresAt is null ? "—" : $"{token.ExpiresAt.Value.ToUnixTimeSeconds()} · {token.ExpiresAt:HH:mm:ss}",
            token.IsExpiredByItsOwnClaim ? "▲ expired" : null));

        OnPropertyChanged(nameof(TokenTimingLine));
        OnPropertyChanged(nameof(ScopeLine));
    }
}

/// <param name="Warning">Non-null only when the claim needs the reader to act. ENT-04, SCAN-06.</param>
public sealed record TokenClaimViewModel(string Name, string Value, string? Warning);

/// <summary>Trust and network. Every row states its source. UI_SPEC 5.7.</summary>
public sealed partial class TrustSettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private string? _proxyUri;

    [ObservableProperty]
    private string _proxySource = "from system proxy";

    [ObservableProperty]
    private string _bypassLine = string.Empty;

    [ObservableProperty]
    private string? _failureHeadline;

    [ObservableProperty]
    private string? _failureRemedy;

    public ObservableCollection<CertificateCandidate> Certificates { get; } = [];

    public ObservableCollection<HostTrustException> Exceptions { get; } = [];

    public bool HasFailure => FailureHeadline is not null;

    /// <summary>
    /// Shows a TLS failure the way ENT-11 requires: which certificate in the chain failed and why,
    /// followed by what to do about it.
    /// </summary>
    public void ShowFailure(TrustDecision decision)
    {
        var first = decision.Problems.FirstOrDefault();

        FailureHeadline = first is null
            ? "The certificate is not trusted by this machine."
            : first.Explanation;

        FailureRemedy = "Add its root CA to the machine store, or allow it for this host only.";
        OnPropertyChanged(nameof(HasFailure));
    }
}

public sealed record KeyBindingViewModel(string Gesture, string Description);
