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

    /// <summary>Tenant id or domain, e.g. contoso.onmicrosoft.com.</summary>
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
    /// The credential store key for this profile's secret — client secret, password or AWS secret
    /// key, depending on the kind. The value never appears in the file this profile is written to.
    /// </summary>
    public SecretKey SecretKey => new(Abstractions.SecretKey.AuthScope, Name);

    /// <summary>True when the profile needs a secret the user has not supplied yet.</summary>
    public bool RequiresSecret => Kind is AuthKind.EntraClientCredentials or AuthKind.Basic
        or AuthKind.ApiKey or AuthKind.Bearer or AuthKind.AwsSigV4 or AuthKind.OAuth2ClientCredentials;
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
