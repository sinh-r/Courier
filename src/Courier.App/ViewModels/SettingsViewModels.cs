using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.Core.Abstractions;
using Courier.Core.Auth;
using Courier.Core.Collections;
using Courier.Core.Privacy;

namespace Courier.App.ViewModels;

/// <summary>
/// The environments table. SEC-03, CORE-12. Backed by <see cref="CollectionLoader.SaveEnvironment"/>
/// and <see cref="ISecretStore"/> — see <c>Load</c>/<c>Save</c> for the shared/local split this
/// projects to and from disk.
/// </summary>
public sealed partial class EnvironmentsViewModel : ObservableObject
{
    private readonly ISecretStore _secrets;
    private string? _folder;

    public EnvironmentsViewModel(ISecretStore secrets) => _secrets = secrets;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _newEnvironmentName = string.Empty;

    [ObservableProperty]
    private bool _hasFolder;

    public ObservableCollection<EnvironmentVariableViewModel> Variables { get; } = [];

    public bool HasEnvironment => Name.Length > 0;

    /// <summary>
    /// Raised after <see cref="NewEnvironmentCommand"/> saves, so the shell can make it the active
    /// environment and refresh the title-bar picker.
    /// </summary>
    public event Action<string>? EnvironmentCreated;

    /// <summary>
    /// Raised after every successful <see cref="SaveAsync"/> (add, edit, delete). Without this the
    /// shell's <c>ActiveEnvironment</c> — loaded once, when the picker's selection last changed —
    /// goes stale the moment a variable is saved, and the next <see cref="Load"/> (reopening the
    /// pane, or clicking away and back in the settings nav) rebuilds the grid from that stale copy:
    /// a just-added variable looks like it "vanished" even though it is correctly on disk.
    /// </summary>
    public event Action<EnvironmentDefinition>? EnvironmentSaved;

    /// <summary>
    /// Projects the currently active environment into editable rows. Called when the Environments
    /// settings pane opens and whenever the active environment changes. A null <paramref name="environment"/>
    /// is not an error — a freshly scanned collection with no derived <c>baseUrl</c>, or one nobody
    /// has picked an environment in yet, has nothing to edit until <see cref="NewEnvironmentCommand"/>
    /// makes one.
    /// </summary>
    public void Load(string? folder, EnvironmentDefinition? environment)
    {
        _folder = folder;
        HasFolder = folder is not null;
        Variables.Clear();

        Name = environment?.Name ?? string.Empty;
        FilePath = environment is null
            ? string.Empty
            : $"{CollectionFormat.EnvironmentsFolder}/{environment.Name}{CollectionFormat.EnvironmentFileExtension}";

        if (environment is not null)
        {
            foreach (var (key, value) in environment.Shared)
            {
                Variables.Add(new EnvironmentVariableViewModel { Name = key, SharedValue = value });
            }

            foreach (var localName in environment.LocalNames)
            {
                Variables.Add(new EnvironmentVariableViewModel
                {
                    Name = localName,
                    IsLocal = true,
                    OriginalLocalName = localName,
                });
            }
        }

        OnPropertyChanged(nameof(HasEnvironment));
    }

    [RelayCommand]
    private void AddVariable() => Variables.Add(new EnvironmentVariableViewModel());

    [RelayCommand]
    private async Task DeleteVariableAsync(EnvironmentVariableViewModel row)
    {
        Variables.Remove(row);

        if (row.OriginalLocalName is { } existing)
        {
            await _secrets.DeleteAsync(new SecretKey(SecretKey.EnvironmentScope, $"{Name}/{existing}"));
        }

        await SaveAsync();
    }

    /// <summary>
    /// The only place anything is written: a shared row's value goes straight into the YAML; a
    /// local row's value (if any is pending, from a just-clicked "Move to local") goes to
    /// <see cref="ISecretStore"/> and the row keeps only its name. A local row renamed since it was
    /// loaded is re-keyed rather than orphaned, since <see cref="EnvironmentDefinition.SecretKeyFor"/>
    /// embeds the variable name.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_folder is null || string.IsNullOrWhiteSpace(Name))
        {
            return;
        }

        var definition = new EnvironmentDefinition { Name = Name };

        foreach (var row in Variables)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                continue;
            }

            if (!row.IsLocal)
            {
                definition.Shared[row.Name] = row.SharedValue ?? string.Empty;
                continue;
            }

            definition.LocalNames.Add(row.Name);

            if (row.PendingLocalValue is { } value)
            {
                await _secrets.SetAsync(new SecretKey(SecretKey.EnvironmentScope, $"{Name}/{row.Name}"), value);
                row.ClearPendingLocalValue();
            }
            else if (row.OriginalLocalName is { } original && !string.Equals(original, row.Name, StringComparison.Ordinal))
            {
                var oldKey = new SecretKey(SecretKey.EnvironmentScope, $"{Name}/{original}");
                var existingValue = await _secrets.GetAsync(oldKey);
                if (existingValue is not null)
                {
                    await _secrets.SetAsync(new SecretKey(SecretKey.EnvironmentScope, $"{Name}/{row.Name}"), existingValue);
                    await _secrets.DeleteAsync(oldKey);
                }
            }

            row.OriginalLocalName = row.Name;
        }

        CollectionLoader.SaveEnvironment(_folder, definition);
        EnvironmentSaved?.Invoke(definition);
    }

    /// <summary>
    /// Environments today only ever come from a scan, so a collection whose source derives none —
    /// or one nobody has picked an environment in — would otherwise have nothing to edit. CORE-12.
    /// </summary>
    [RelayCommand]
    private void NewEnvironment()
    {
        if (_folder is null || string.IsNullOrWhiteSpace(NewEnvironmentName))
        {
            return;
        }

        var definition = new EnvironmentDefinition { Name = NewEnvironmentName.Trim() };
        CollectionLoader.SaveEnvironment(_folder, definition);

        NewEnvironmentName = string.Empty;
        EnvironmentCreated?.Invoke(definition.Name);
    }
}

/// <summary>
/// One variable, in one of two columns. The split is the point: shared goes in git, local goes to
/// the credential store, and the two are never the same slot. Persistence itself lives in
/// <see cref="EnvironmentsViewModel.SaveAsync"/> — this row only tracks enough state
/// (<see cref="OriginalLocalName"/>, <see cref="PendingLocalValue"/>) for that save to do the right
/// thing without every keystroke touching the credential store.
/// </summary>
public sealed partial class EnvironmentVariableViewModel : ObservableObject
{
    /// <summary>Set only when this row was already local on disk, so a rename can be re-keyed on save.</summary>
    internal string? OriginalLocalName { get; set; }

    /// <summary>
    /// Set only by <see cref="MoveToLocal"/>: the plaintext value, held in memory until
    /// <see cref="EnvironmentsViewModel.SaveAsync"/> writes it to the credential store and clears it.
    /// Never written to any file.
    /// </summary>
    internal string? PendingLocalValue { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedSecretWarning))]
    [NotifyPropertyChangedFor(nameof(SecretReason))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedSecretWarning))]
    [NotifyPropertyChangedFor(nameof(SecretReason))]
    [NotifyPropertyChangedFor(nameof(SharedDisplay))]
    private string? _sharedValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedSecretWarning))]
    [NotifyPropertyChangedFor(nameof(LocalDisplay))]
    [NotifyPropertyChangedFor(nameof(LocalSource))]
    [NotifyPropertyChangedFor(nameof(CanEditLocal))]
    private bool _isLocal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedSecretWarning))]
    private bool _warningDismissed;

    /// <summary>True while the masked local field has been swapped for an editable one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditLocal))]
    private bool _isEditingLocal;

    /// <summary>The typed replacement, held only until <see cref="ConfirmLocalEdit"/> commits it.</summary>
    [ObservableProperty]
    private string _localValueDraft = string.Empty;

    public string SharedDisplay => SharedValue ?? "—";

    /// <summary>A local value is located, never displayed. P2.</summary>
    public string LocalDisplay => IsLocal ? (PendingLocalValue is null ? "••••••••" : "•••••••• (unsaved)") : "—";

    public string LocalSource => IsLocal ? "Credential Manager" : string.Empty;

    /// <summary>The "Edit" affordance shows only for an already-local row that isn't mid-edit.</summary>
    public bool CanEditLocal => IsLocal && !IsEditingLocal;

    /// <summary>Why <see cref="HasSharedSecretWarning"/> fired, e.g. "field name 'apiKey' names a credential".</summary>
    public string SecretReason => SecretPatterns.Classify(SharedValue, Name).Reason ?? "This looks like a secret";

    /// <summary>
    /// A secret-shaped value sitting in the shared column. SEC-03 requires a warning, and the
    /// warning has to be actionable rather than advisory, hence the two buttons beside it. Live as
    /// you type, via the <c>NotifyPropertyChangedFor</c> attributes above.
    /// </summary>
    public bool HasSharedSecretWarning =>
        !WarningDismissed
        && !IsLocal
        && SecretPatterns.Classify(SharedValue, Name).IsSecret;

    [RelayCommand]
    private void MoveToLocal()
    {
        PendingLocalValue = SharedValue;
        IsLocal = true;
        SharedValue = null;
    }

    /// <summary>
    /// The user overriding a false positive. The bias toward false positives (REQUIREMENTS 9) is
    /// only tolerable if dismissing one is a single click and it stays dismissed.
    /// </summary>
    [RelayCommand]
    private void DismissWarning() => WarningDismissed = true;

    /// <summary>Swaps the masked placeholder for an editable field — the only way to replace an
    /// already-local value, since it is never read back out of the credential store to display.</summary>
    [RelayCommand]
    private void BeginEditLocal()
    {
        LocalValueDraft = string.Empty;
        IsEditingLocal = true;
    }

    [RelayCommand]
    private void ConfirmLocalEdit()
    {
        if (LocalValueDraft.Length > 0)
        {
            PendingLocalValue = LocalValueDraft;
            OnPropertyChanged(nameof(LocalDisplay));
        }

        LocalValueDraft = string.Empty;
        IsEditingLocal = false;
    }

    [RelayCommand]
    private void CancelLocalEdit()
    {
        LocalValueDraft = string.Empty;
        IsEditingLocal = false;
    }

    internal void ClearPendingLocalValue()
    {
        PendingLocalValue = null;
        OnPropertyChanged(nameof(LocalDisplay));
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
