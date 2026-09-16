using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Courier.App.Services;
using Courier.Core.Abstractions;
using Courier.Core.Auth;
using Courier.Core.Collections;

namespace Courier.App.ViewModels;

/// <summary>
/// One request's auth choice — or a collection's default — expressed the same way: Inherit, no
/// auth, a saved profile, or a configuration typed directly here. ENT-02. The request tab's Auth
/// panel and the inspector's Auth section bind to the same instance of this for a request —
/// <see cref="MainWindowViewModel"/> owns one and reloads it whenever the active tab changes, which
/// is what keeps the two views showing the same choice your notes call for ("the same selected
/// option should be there at inspector tab as well"). <see cref="MainWindowViewModel.CollectionAuth"/>
/// is a second, independent instance of this same class, loaded against the collection's own
/// <see cref="AuthReference"/> instead of a request's.
/// </summary>
public sealed partial class AuthChoiceViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ISecretStore _secrets;
    private string? _collectionFolder;
    private AuthProfile? _loadedInline;

    public AuthChoiceViewModel(AppServices services)
    {
        _services = services;
        _secrets = services.SecretStore;
    }

    /// <summary>"Inherit from collection", "No auth", one entry per <see cref="AuthKindOptions"/>,
    /// a separator-like "── Saved profiles ──" when there are any, then each profile name.</summary>
    public ObservableCollection<string> Options { get; } = [InheritOption, NoneOption];

    [ObservableProperty]
    private string _selectedOption = InheritOption;

    /// <summary>Set only while <see cref="SelectedOption"/> names a saved profile, for the "from
    /// profile 'x'" line — distinct from typing a profile name that happens to match nothing.</summary>
    [ObservableProperty]
    private string? _resolutionNote;

    // --- Inline editor fields, shown only when SelectedOption is one of the AuthKindOptions ---

    [ObservableProperty]
    private string? _tenant;

    [ObservableProperty]
    private string? _clientId;

    [ObservableProperty]
    private string? _username;

    [ObservableProperty]
    private string? _redirectUri;

    [ObservableProperty]
    private string _scopesText = string.Empty;

    /// <summary>Never round-tripped from disk — only ever written here, then saved. Cleared after saving.</summary>
    [ObservableProperty]
    private string _secretInput = string.Empty;

    [ObservableProperty]
    private bool _hasStoredSecret;

    // --- The token panel. Only ever populated by GetTokenAsync below, for the fixed-kind inline
    // editor above — a collection default named from a saved profile decodes via that profile's own
    // "Get token" in the list to the left instead. ENT-04. ---

    [ObservableProperty]
    private DecodedToken? _token;

    [ObservableProperty]
    private string? _tokenStatusMessage;

    public ObservableCollection<TokenClaimViewModel> Claims { get; } = [];

    public const string InheritOption = "Inherit from collection";
    public const string NoneOption = "No auth";
    public const string ManageProfilesOption = "Manage profiles…";

    public bool IsInline => SelectedOption != NoneOption && AuthKindOptions.Labels.Contains(SelectedOption);

    public AuthKind InlineKind => AuthKindOptions.KindFor(SelectedOption);

    public bool ShowsEntraFields => InlineKind is AuthKind.EntraClientCredentials or AuthKind.EntraAuthorizationCode;

    public bool ShowsUsername => InlineKind == AuthKind.Basic;

    public bool ShowsRedirectUri => InlineKind == AuthKind.EntraAuthorizationCode;

    public bool ShowsSecretField => InlineKind is AuthKind.Bearer or AuthKind.Basic or AuthKind.EntraClientCredentials;

    public string SecretFieldLabel => InlineKind switch
    {
        AuthKind.Bearer => "Token",
        AuthKind.Basic => "Password",
        AuthKind.EntraClientCredentials => "Client secret",
        _ => "Secret",
    };

    public string SecretStatusText => !ShowsSecretField
        ? string.Empty
        : HasStoredSecret
            ? "stored in the credential store"
            : "not set on this machine";

    public bool CanGetToken => ShowsEntraFields;

    /// <summary>"issued 13:15 · expires 14:49 · in 47m".</summary>
    public string TokenTimingLine => Token is null
        ? "no token yet"
        : $"issued {Token.IssuedAt:HH:mm} · expires {Token.ExpiresAt:HH:mm} · {Token.DescribeExpiry()}";

    partial void OnSelectedOptionChanged(string value)
    {
        RaiseVisibility();

        if (value == ManageProfilesOption)
        {
            ManageProfilesRequested?.Invoke();
        }
    }

    /// <summary>Raised when the user picks "Manage profiles…" — the shell opens Settings for it.</summary>
    public event Action? ManageProfilesRequested;

    /// <summary>Loads the request's saved choice and the collection's list of profile names.</summary>
    public async Task LoadAsync(AuthReference? reference, string? collectionFolder, CancellationToken ct = default)
    {
        _collectionFolder = collectionFolder;
        RefreshProfileNames();

        var auth = reference ?? new AuthReference(AuthMode.Inherit);

        switch (auth.Mode)
        {
            case AuthMode.None:
                SelectedOption = NoneOption;
                await LoadInlineAsync(null, ct).ConfigureAwait(false);
                break;

            case AuthMode.Profile when auth.Profile is { Length: > 0 } name:
                if (!Options.Contains(name))
                {
                    // A dangling reference — the profile named here is not (or no longer) in
                    // auth/. Shown anyway, selected, so the picker reflects what is actually saved
                    // on the request rather than silently falling back to something else.
                    Options.Add(name);
                }

                SelectedOption = name;
                ResolutionNote = collectionFolder is null
                    ? $"profile '{name}'"
                    : AuthProfileStore.Load(collectionFolder, name) is null
                        ? $"profile '{name}' is not in auth/ — create it, or choose a different auth"
                        : $"profile '{name}'";

                await LoadInlineAsync(null, ct).ConfigureAwait(false);
                break;

            case AuthMode.Inline:
                var kind = auth.Inline?.Kind ?? AuthKind.None;
                SelectedOption = AuthKindOptions.LabelFor(kind);
                await LoadInlineAsync(auth.Inline, ct).ConfigureAwait(false);
                break;

            default:
                SelectedOption = InheritOption;
                await LoadInlineAsync(null, ct).ConfigureAwait(false);
                break;
        }
    }

    private void RefreshProfileNames()
    {
        var current = SelectedOption;

        Options.Clear();
        Options.Add(InheritOption);
        Options.Add(NoneOption);

        foreach (var label in AuthKindOptions.Labels.Skip(1)) // skip "No auth", already added above
        {
            Options.Add(label);
        }

        if (_collectionFolder is not null)
        {
            foreach (var name in AuthProfileStore.ListNames(_collectionFolder))
            {
                Options.Add(name);
            }
        }

        Options.Add(ManageProfilesOption);

        if (Options.Contains(current))
        {
            SelectedOption = current;
        }
    }

    private async Task LoadInlineAsync(AuthProfile? profile, CancellationToken ct)
    {
        _loadedInline = profile;
        Tenant = profile?.Tenant;
        ClientId = profile?.ClientId;
        Username = profile?.Username;
        RedirectUri = profile?.RedirectUri;
        ScopesText = profile is null ? string.Empty : string.Join(' ', profile.Scopes);
        SecretInput = string.Empty;

        HasStoredSecret = profile?.SecretRef is not null
            && await _secrets.GetAsync(profile.SecretKey, ct).ConfigureAwait(false) is not null;

        RaiseVisibility();
    }

    /// <summary>
    /// The reference to persist onto the request — Inherit, None, Profile, or Inline with the typed
    /// fields, saving a new secret first if one was entered.
    /// </summary>
    public async Task<AuthReference> ToReferenceAsync(CancellationToken ct = default)
    {
        if (SelectedOption is InheritOption or ManageProfilesOption)
        {
            return new AuthReference(AuthMode.Inherit);
        }

        if (SelectedOption == NoneOption)
        {
            return new AuthReference(AuthMode.None);
        }

        if (!IsInline)
        {
            // Not one of the fixed kinds and not Inherit/None: a saved profile name.
            return new AuthReference(AuthMode.Profile, SelectedOption);
        }

        var profile = new AuthProfile
        {
            Kind = InlineKind,
            Tenant = Tenant,
            ClientId = ClientId,
            Username = Username,
            RedirectUri = RedirectUri,
            Scopes = [.. ScopesText.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
            SecretRef = _loadedInline?.SecretRef,
        };

        if (SecretInput.Length > 0)
        {
            profile.SecretRef ??= Guid.NewGuid().ToString("n");
            await _secrets.SetAsync(profile.SecretKey, SecretInput, ct).ConfigureAwait(false);
            SecretInput = string.Empty;
            HasStoredSecret = true;
        }

        _loadedInline = profile;
        return new AuthReference(AuthMode.Inline, Inline: profile);
    }

    /// <summary>
    /// Acquires and decodes a token for the Entra configuration currently typed inline — the same
    /// acquire-then-decode <see cref="AuthProfileEditorViewModel.GetTokenAsync"/> runs for a saved
    /// profile, run here against fields that have not (or not yet) been saved as one. ENT-04.
    /// </summary>
    [RelayCommand]
    public async Task GetTokenAsync()
    {
        if (!CanGetToken)
        {
            return;
        }

        var profile = new AuthProfile
        {
            Kind = InlineKind,
            Tenant = Tenant,
            ClientId = ClientId,
            Username = Username,
            RedirectUri = RedirectUri,
            Scopes = [.. ScopesText.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
            SecretRef = _loadedInline?.SecretRef,
        };

        if (SecretInput.Length > 0)
        {
            profile.SecretRef ??= Guid.NewGuid().ToString("n");
            await _secrets.SetAsync(profile.SecretKey, SecretInput).ConfigureAwait(false);
            SecretInput = string.Empty;
            HasStoredSecret = true;
        }

        _loadedInline = profile;
        TokenStatusMessage = "Signing in…";

        try
        {
            var acquired = await _services.Entra.AcquireAsync(profile, interactiveAllowed: true).ConfigureAwait(false);
            Show(TokenDecoder.TryDecode(acquired.AccessToken));
            TokenStatusMessage = $"Token acquired · {acquired.Account ?? "no account name"}";
        }
        catch (InteractiveAuthRequiredException ex)
        {
            TokenStatusMessage = ex.Message;
        }
    }

    private void Show(DecodedToken? token)
    {
        Token = token;
        Claims.Clear();

        foreach (var claim in TokenClaimViewModel.Build(token, []))
        {
            Claims.Add(claim);
        }

        OnPropertyChanged(nameof(TokenTimingLine));
    }

    private void RaiseVisibility()
    {
        OnPropertyChanged(nameof(IsInline));
        OnPropertyChanged(nameof(InlineKind));
        OnPropertyChanged(nameof(ShowsEntraFields));
        OnPropertyChanged(nameof(ShowsUsername));
        OnPropertyChanged(nameof(ShowsRedirectUri));
        OnPropertyChanged(nameof(ShowsSecretField));
        OnPropertyChanged(nameof(CanGetToken));
        OnPropertyChanged(nameof(SecretFieldLabel));
        OnPropertyChanged(nameof(SecretStatusText));
    }
}
