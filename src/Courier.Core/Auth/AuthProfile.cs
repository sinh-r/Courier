using Courier.Core.Abstractions;

namespace Courier.Core.Auth;

/// <summary>
/// A named authentication configuration. Committed to the collection folder — and therefore
/// containing no secret. Secrets referenced here live in the credential store. P2, STOR-04.
/// </summary>
/// <remarks>
/// ENT-02 asks for named presets rather than hand-assembled OAuth2, and this is the shape of that:
/// the user picks a <see cref="AuthKind"/> and fills in the four or five fields it actually needs,
/// instead of being handed a generic grant-type form and left to work it out.
/// </remarks>
public sealed class AuthProfile
{
    public string Name { get; set; } = "Default";

    public AuthKind Kind { get; set; } = AuthKind.None;

    public string? Description { get; set; }

    // --- Entra and generic OIDC ---

    /// <summary>Entra tenant ID — a GUID, e.g. 72f988bf-86f1-41af-91ab-2d7cd011db47.</summary>
    public string? Tenant { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Authority for a non-Entra OIDC provider. Registered with the egress policy on save.</summary>
    public string? Authority { get; set; }

    public List<string> Scopes { get; set; } = [];

    public string? RedirectUri { get; set; }

    // --- Simple schemes ---

    public string? Username { get; set; }

    /// <summary>Header name for an API key, e.g. X-Api-Key. The value is a secret reference.</summary>
    public string? ApiKeyHeader { get; set; }

    /// <summary>"header" or "query". Some gateways only accept one.</summary>
    public string ApiKeyLocation { get; set; } = "header";

    // --- AWS SigV4 ---

    public string? AwsRegion { get; set; }

    public string? AwsService { get; set; }

    public string? AwsAccessKeyId { get; set; }

    // --- Behaviour ---

    /// <summary>Refresh this many seconds before expiry, silently. ENT-05.</summary>
    public int RefreshLeewaySeconds { get; set; } = 300;

    /// <summary>Reuse a token from the Azure CLI or Visual Studio caches if one is present. ENT-03.</summary>
    public bool ReuseDeveloperToolTokens { get; set; } = true;

    /// <summary>
    /// Opaque id minted the first time a secret is saved for this profile, and kept from then on.
    /// Renaming or moving the profile must not orphan its secret in the credential store, which a
    /// name-keyed lookup would do the moment <see cref="Name"/> changed.
    /// </summary>
    public string? SecretRef { get; set; }

    /// <summary>
    /// The credential store key for this profile's secret — client secret, password or AWS secret
    /// key, depending on the kind. The value never appears in the file this profile is written to.
    /// </summary>
    public SecretKey SecretKey => new(Abstractions.SecretKey.AuthScope, SecretRef ?? Name);

    /// <summary>True when the profile needs a secret the user has not supplied yet.</summary>
    public bool RequiresSecret => Kind is AuthKind.EntraClientCredentials or AuthKind.Basic
        or AuthKind.ApiKey or AuthKind.Bearer or AuthKind.AwsSigV4 or AuthKind.OAuth2ClientCredentials;

    /// <summary>
    /// Why <see cref="Tenant"/> cannot be used to sign in, or null when it can. Only Entra kinds
    /// without an explicit <see cref="Authority"/> are checked. A blank tenant falls back to
    /// "organizations", which client credentials does not accept: an app-only token is always for
    /// one specific tenant.
    /// </summary>
    public string? TenantProblem()
    {
        if (Authority is not null
            || Kind is not (AuthKind.EntraWindowsSignIn or AuthKind.EntraClientCredentials
                or AuthKind.EntraDeviceCode or AuthKind.EntraAuthorizationCode))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(Tenant))
        {
            return Kind == AuthKind.EntraClientCredentials
                ? "Client credentials needs a Tenant ID (the directory's GUID, from the app registration's Overview page)."
                : null;
        }

        return Guid.TryParse(Tenant.Trim(), out _)
            ? null
            : $"Tenant ID must be a GUID, e.g. 72f988bf-86f1-41af-91ab-2d7cd011db47 — \"{Tenant.Trim()}\" is not one.";
    }

    /// <summary>
    /// Deep-enough copy for <see cref="Collections.RequestDefinition.Clone"/>: this is a mutable
    /// class embedded in an immutable <see cref="Collections.AuthReference"/> record, so the record
    /// copy constructor alone would leave two requests sharing one profile instance.
    /// </summary>
    public AuthProfile Clone() => new()
    {
        Name = Name,
        Kind = Kind,
        Description = Description,
        Tenant = Tenant,
        ClientId = ClientId,
        Authority = Authority,
        Scopes = [.. Scopes],
        RedirectUri = RedirectUri,
        Username = Username,
        ApiKeyHeader = ApiKeyHeader,
        ApiKeyLocation = ApiKeyLocation,
        AwsRegion = AwsRegion,
        AwsService = AwsService,
        AwsAccessKeyId = AwsAccessKeyId,
        RefreshLeewaySeconds = RefreshLeewaySeconds,
        ReuseDeveloperToolTokens = ReuseDeveloperToolTokens,
        SecretRef = SecretRef,
    };
}

/// <summary>
/// Named presets rather than a generic OAuth2 form. ENT-02, ENT-06.
/// </summary>
public enum AuthKind
{
    None,

    /// <summary>Silent SSO from the machine's existing Windows sign-in, through WAM. ENT-01.</summary>
    EntraWindowsSignIn,

    /// <summary>Client id and secret. The secret goes to Credential Manager. ENT-02.</summary>
    EntraClientCredentials,

    /// <summary>For a machine with no browser, or a remote session. ENT-02.</summary>
    EntraDeviceCode,

    /// <summary>Interactive sign-in with PKCE. ENT-02.</summary>
    EntraAuthorizationCode,

    /// <summary>Generic OIDC provider, not Entra. ENT-06.</summary>
    OAuth2AuthorizationCode,

    OAuth2ClientCredentials,

    Bearer,

    Basic,

    ApiKey,

    AwsSigV4,

    /// <summary>NTLM or Kerberos with the current Windows identity. ENT-07.</summary>
    IntegratedWindows,

    /// <summary>Inherit whatever the collection specifies.</summary>
    Inherit,
}
