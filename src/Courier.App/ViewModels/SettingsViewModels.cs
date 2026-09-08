using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.App.Services;
using Courier.Core.Abstractions;
using Courier.Core.Auth;
using Courier.Core.Collections;
using Courier.Core.Privacy;
using Courier.Scanner;

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
    private bool _suppressDirtyTracking;
    private bool _revertingSelection;
    private string? _pendingSwitch;

    public EnvironmentsViewModel(ISecretStore secrets)
    {
        _secrets = secrets;

        Variables.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
            {
                foreach (EnvironmentVariableViewModel row in e.OldItems)
                {
                    row.PropertyChanged -= OnVariableRowChanged;
                }
            }

            if (e.NewItems is not null)
            {
                foreach (EnvironmentVariableViewModel row in e.NewItems)
                {
                    row.PropertyChanged += OnVariableRowChanged;
                }
            }
        };
    }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _newEnvironmentName = string.Empty;

    /// <summary>Pre-filled from a picked <see cref="DetectedProfiles"/> entry, or from the
    /// currently loaded environment's own <c>baseUrl</c> when nothing was detected. Editable either
    /// way, so a create never depends on a profile existing.</summary>
    [ObservableProperty]
    private string _newEnvironmentBaseUrl = string.Empty;

    [ObservableProperty]
    private DetectedProfile? _selectedDetectedProfile;

    [ObservableProperty]
    private bool _hasFolder;

    /// <summary>True the moment any row changes after a <see cref="Load"/>; cleared by a successful
    /// <see cref="SaveAsync"/> or by <see cref="DiscardChangesCommand"/>. Drives the unsaved-changes
    /// bar shown when <see cref="SelectedEnvironmentName"/> is switched away from mid-edit.</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>The result of the last save attempt, shown beside the Save button. Cleared on the
    /// next <see cref="Load"/> so a stale "Saved" does not linger after switching environments.</summary>
    [ObservableProperty]
    private EnvironmentSaveStatus? _saveStatus;

    /// <summary>
    /// Which environment this pane is editing — independent of the title-bar picker's
    /// <c>EnvironmentName</c>, which is which environment requests are sent with. Switching this
    /// while <see cref="IsDirty"/> defers to <see cref="HasPendingSwitch"/> rather than discarding
    /// silently.
    /// </summary>
    [ObservableProperty]
    private string? _selectedEnvironmentName;

    public ObservableCollection<string> AvailableEnvironments { get; } = [];

    public ObservableCollection<EnvironmentVariableViewModel> Variables { get; } = [];

    /// <summary>Base URLs found in the scanned source's launch profiles and <c>appsettings.*.json</c>,
    /// offered when creating a new environment. Empty when the collection has no known source, or
    /// the source no longer resolves. SCAN-07.</summary>
    public ObservableCollection<DetectedProfile> DetectedProfiles { get; } = [];

    public bool HasEnvironment => Name.Length > 0;

    /// <summary>An environment other than the one just saved or discarded is waiting to be shown —
    /// the picker's visible selection has been reverted back to <see cref="Name"/> until then.</summary>
    public bool HasPendingSwitch => _pendingSwitch is not null;

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

        RefreshAvailableEnvironmentNames();

        _suppressDirtyTracking = true;
        try
        {
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
        }
        finally
        {
            _suppressDirtyTracking = false;
        }

        IsDirty = false;
        SaveStatus = null;
        RevertSelectionTo(environment?.Name);

        OnPropertyChanged(nameof(HasEnvironment));
    }

    /// <summary>
    /// Re-derives <see cref="DetectedProfiles"/> from the collection's recorded scan source, if it
    /// has one. Called whenever the Environments pane opens — the source's launch profiles can
    /// change between scans, and re-reading here costs nothing next to a filesystem probe or two.
    /// </summary>
    public void RefreshDetectedProfiles(string? scannedFromPath)
    {
        DetectedProfiles.Clear();
        SelectedDetectedProfile = null;

        if (scannedFromPath is not null)
        {
            var root = ResolveSourceRoot(scannedFromPath);

            if (Directory.Exists(root))
            {
                foreach (var derived in EnvironmentReader.Read(root))
                {
                    DetectedProfiles.Add(new DetectedProfile(derived.Name, derived.BaseUrl, derived.Source));
                }
            }
        }

        // No known source, or it derived nothing: fall back to whatever baseUrl the environment
        // currently open already has, so creating a sibling environment is not a blank slate.
        if (DetectedProfiles.Count == 0)
        {
            NewEnvironmentBaseUrl = Variables
                .FirstOrDefault(v => !v.IsLocal && string.Equals(v.Name, "baseUrl", StringComparison.OrdinalIgnoreCase))
                ?.SharedValue ?? string.Empty;
        }
    }

    private string ResolveSourceRoot(string scannedFromPath) =>
        Path.IsPathRooted(scannedFromPath) || _folder is null
            ? scannedFromPath
            : Path.GetFullPath(Path.Combine(_folder, scannedFromPath));

    partial void OnSelectedDetectedProfileChanged(DetectedProfile? value)
    {
        if (value is null)
        {
            return;
        }

        NewEnvironmentName = value.Name;
        NewEnvironmentBaseUrl = value.BaseUrl;
    }

    /// <summary>
    /// The picker's selection changing is either the user genuinely switching (apply immediately),
    /// or this class reverting the visible selection back to <see cref="Name"/> itself — guarded by
    /// <see cref="_revertingSelection"/> so that revert does not loop back through here as a second
    /// "switch". A null here is never a real user choice — nothing in the picker represents "no
    /// environment" — so a refresh's Reset briefly clearing the bound SelectedItem lands here too;
    /// snap straight back rather than leaving the picker blank.
    /// </summary>
    partial void OnSelectedEnvironmentNameChanged(string? value)
    {
        if (_revertingSelection)
        {
            return;
        }

        if (value is null)
        {
            if (Name.Length > 0)
            {
                RevertSelectionTo(Name);
            }

            return;
        }

        if (_folder is null || value == Name)
        {
            return;
        }

        if (IsDirty)
        {
            _pendingSwitch = value;
            OnPropertyChanged(nameof(HasPendingSwitch));
            RevertSelectionTo(Name);
            return;
        }

        Load(_folder, CollectionLoader.LoadEnvironment(_folder, value));
    }

    private void RevertSelectionTo(string? name)
    {
        _revertingSelection = true;
        SelectedEnvironmentName = name;
        _revertingSelection = false;
    }

    private void RefreshAvailableEnvironmentNames()
    {
        var names = _folder is null
            ? Array.Empty<string>()
            : CollectionLoader.ListEnvironments(_folder);

        ObservableListSync.SyncTo(AvailableEnvironments, names);
    }

    /// <summary>Discards the current edits and reloads <see cref="Name"/> from disk, then completes
    /// whatever switch was waiting on that decision. The unsaved-changes bar's other action.</summary>
    [RelayCommand]
    private void DiscardChanges()
    {
        if (_folder is not null && Name.Length > 0)
        {
            Load(_folder, CollectionLoader.LoadEnvironment(_folder, Name));
        }

        CompletePendingSwitch();
    }

    private void CompletePendingSwitch()
    {
        if (_pendingSwitch is not { } target || _folder is null)
        {
            return;
        }

        _pendingSwitch = null;
        OnPropertyChanged(nameof(HasPendingSwitch));
        Load(_folder, CollectionLoader.LoadEnvironment(_folder, target));
    }

    private void OnVariableRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressDirtyTracking)
        {
            return;
        }

        // Chrome around an edit in progress, not the saved shape itself — the warning banner being
        // dismissed or a masked field being opened for editing should not by itself dirty the pane.
        if (e.PropertyName is nameof(EnvironmentVariableViewModel.IsEditingLocal)
            or nameof(EnvironmentVariableViewModel.LocalValueDraft)
            or nameof(EnvironmentVariableViewModel.WarningDismissed))
        {
            return;
        }

        IsDirty = true;
    }

    [RelayCommand]
    private void AddVariable()
    {
        Variables.Add(new EnvironmentVariableViewModel());
        IsDirty = true;
    }

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
    /// embeds the variable name. Every exit path sets <see cref="SaveStatus"/> — the silence a
    /// failed save used to return with was indistinguishable from a successful one.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_folder is null)
        {
            SaveStatus = new EnvironmentSaveStatus(false, "Not saved — open a collection folder first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            SaveStatus = new EnvironmentSaveStatus(false, "Not saved — the environment needs a name.");
            return;
        }

        var definition = new EnvironmentDefinition { Name = Name };

        try
        {
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
        }
        catch (Exception ex)
        {
            // A save the user asked for failing silently is worse than one reported broadly: there
            // is no narrower recovery available here than "tell them and let them retry".
            SaveStatus = new EnvironmentSaveStatus(false, $"Not saved — {ex.Message}");
            return;
        }

        IsDirty = false;
        SaveStatus = new EnvironmentSaveStatus(
            true,
            $"Saved to {CollectionFormat.EnvironmentsFolder}/{Name}{CollectionFormat.EnvironmentFileExtension} · "
            + $"{DateTimeOffset.Now:HH:mm} · {definition.Shared.Count} shared, {definition.LocalNames.Count} local");

        RefreshAvailableEnvironmentNames();
        RevertSelectionTo(Name);
        EnvironmentSaved?.Invoke(definition);
        CompletePendingSwitch();
    }

    /// <summary>
    /// Environments today usually come from a scan, so a collection whose source derives none — or
    /// one nobody has picked an environment in — would otherwise have nothing to edit. CORE-12.
    /// Seeded from <see cref="SelectedDetectedProfile"/> when one was picked, so the common case —
    /// "make me the Local environment from launchSettings" — needs no typing at all.
    /// </summary>
    [RelayCommand]
    private void NewEnvironment()
    {
        if (_folder is null)
        {
            SaveStatus = new EnvironmentSaveStatus(false, "Not saved — open a collection folder first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewEnvironmentName))
        {
            SaveStatus = new EnvironmentSaveStatus(false, "Not saved — name the new environment first.");
            return;
        }

        var definition = new EnvironmentDefinition { Name = NewEnvironmentName.Trim() };

        if (!string.IsNullOrWhiteSpace(NewEnvironmentBaseUrl))
        {
            definition.Shared["baseUrl"] = NewEnvironmentBaseUrl.Trim();
        }

        CollectionLoader.SaveEnvironment(_folder, definition);

        var createdName = definition.Name;
        NewEnvironmentName = string.Empty;
        NewEnvironmentBaseUrl = string.Empty;
        SelectedDetectedProfile = null;
        SaveStatus = new EnvironmentSaveStatus(
            true,
            $"Created {CollectionFormat.EnvironmentsFolder}/{createdName}{CollectionFormat.EnvironmentFileExtension}.");

        RefreshAvailableEnvironmentNames();
        RevertSelectionTo(Name);
        EnvironmentCreated?.Invoke(createdName);
    }

    /// <summary>
    /// Re-entering the pane (settings nav, reopening the dialog) keeps whatever it was editing —
    /// and keeps unsaved edits — rather than snapping back to the shell's active environment.
    /// <paramref name="fallback"/> is used only when this pane has nothing open yet, or the
    /// environment it had open no longer exists.
    /// </summary>
    public void Reopen(string? folder, EnvironmentDefinition? fallback)
    {
        if (_folder == folder && IsDirty)
        {
            RefreshAvailableEnvironmentNames();
            RevertSelectionTo(Name);
            return;
        }

        if (Name.Length > 0
            && folder is not null
            && CollectionLoader.ListEnvironments(folder).Contains(Name, StringComparer.Ordinal))
        {
            Load(folder, CollectionLoader.LoadEnvironment(folder, Name));
            return;
        }

        Load(folder, fallback);
    }
}

/// <param name="Succeeded">Drives which of the two colours the message renders in.</param>
/// <param name="Message">Always names the file or the reason, never a bare "Saved"/"Failed" — SEC-04's
/// house style is specific and active.</param>
public sealed record EnvironmentSaveStatus(bool Succeeded, string Message);

/// <summary>One base URL found in a launch profile or an <c>appsettings.*.json</c>. SCAN-07.</summary>
public sealed record DetectedProfile(string Name, string BaseUrl, string Source);

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
