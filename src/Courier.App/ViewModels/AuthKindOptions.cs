using Courier.Core.Auth;

namespace Courier.App.ViewModels;

/// <summary>
/// The auth kinds the app offers as named presets, and their combo-box labels. ENT-02, ENT-06 name
/// a larger set (API key, AWS SigV4, device code, Windows sign-in, NTLM); those providers exist and
/// work, but nothing here is validated against a real tenant yet, so the editor offers the two
/// Entra grants that can be tested — client credentials in <c>courier run</c>, authorization code
/// interactively — plus Bearer and Basic. See Docs/NEEDS_LIVE_VALIDATION.md.
/// </summary>
public static class AuthKindOptions
{
    public static readonly IReadOnlyList<(string Label, AuthKind Kind)> All =
    [
        ("No auth", AuthKind.None),
        ("Bearer token", AuthKind.Bearer),
        ("Basic", AuthKind.Basic),
        ("Azure Entra · Client credentials", AuthKind.EntraClientCredentials),
        ("Azure Entra · Authorization code (PKCE)", AuthKind.EntraAuthorizationCode),
    ];

    public static readonly string[] Labels = [.. All.Select(o => o.Label)];

    public static string LabelFor(AuthKind kind) =>
        All.FirstOrDefault(o => o.Kind == kind).Label ?? All[0].Label;

    public static AuthKind KindFor(string? label) =>
        All.FirstOrDefault(o => o.Label == label).Kind;
}
