using System.Buffers.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Courier.Core.Auth;

/// <summary>
/// Decodes a JWT for the token panel: audience, scopes, roles, issuer, issued-at, expiry. ENT-04.
/// </summary>
/// <remarks>
/// <b>Decode only.</b> TECH_SPEC 3.2 is explicit and correct: never validate signatures and never
/// present a validity verdict. Courier is not the resource server, does not have the signing keys,
/// and a false "valid" is worse than no verdict — it sends someone hunting a server bug that is
/// really an expired token, or the reverse. The panel reports what the token says about itself and
/// stops there.
/// </remarks>
public static class TokenDecoder
{
    public static DecodedToken? TryDecode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var trimmed = token.Trim();
        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].Trim();
        }

        try
        {
            var jwt = new JsonWebToken(trimmed);
            var claims = new List<TokenClaim>();

            foreach (var claim in jwt.Claims)
            {
                claims.Add(new TokenClaim(claim.Type, claim.Value));
            }

            return new DecodedToken(
                Audience: First(jwt, "aud"),
                Issuer: First(jwt, "iss"),
                Subject: First(jwt, "sub"),
                UserPrincipalName: First(jwt, "upn") ?? First(jwt, "preferred_username") ?? First(jwt, "email"),
                AppId: First(jwt, "appid") ?? First(jwt, "azp"),
                Scopes: SplitSpaceDelimited(First(jwt, "scp") ?? First(jwt, "scope")),
                Roles: [.. jwt.Claims.Where(c => c.Type is "roles" or "role").Select(c => c.Value)],
                IssuedAt: ToTimestamp(First(jwt, "iat")),
                NotBefore: ToTimestamp(First(jwt, "nbf")),
                ExpiresAt: ToTimestamp(First(jwt, "exp")),
                Claims: claims,
                RawHeader: SafeDecodeSegment(trimmed, 0),
                RawPayload: SafeDecodeSegment(trimmed, 1));
        }
        catch (ArgumentException)
        {
            // Not a JWT. An opaque bearer token is perfectly legitimate; the panel says so rather
            // than reporting an error.
            return null;
        }
    }

    private static string? First(JsonWebToken jwt, string type) =>
        jwt.Claims.FirstOrDefault(c => c.Type == type)?.Value;

    private static IReadOnlyList<string> SplitSpaceDelimited(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static DateTimeOffset? ToTimestamp(string? value) =>
        long.TryParse(value, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private static string? SafeDecodeSegment(string token, int index)
    {
        var parts = token.Split('.');
        if (parts.Length <= index)
        {
            return null;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(parts[index]);
            using var document = JsonDocument.Parse(bytes);
            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// What a token says about itself. Presented as a plain table — the panel that turns a 401 from a
/// mystery into a five-second diagnosis (UI_SPEC 5.6).
/// </summary>
public sealed record DecodedToken(
    string? Audience,
    string? Issuer,
    string? Subject,
    string? UserPrincipalName,
    string? AppId,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Roles,
    DateTimeOffset? IssuedAt,
    DateTimeOffset? NotBefore,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<TokenClaim> Claims,
    string? RawHeader,
    string? RawPayload)
{
    public TimeSpan? TimeUntilExpiry => ExpiresAt is null ? null : ExpiresAt.Value - DateTimeOffset.UtcNow;

    /// <summary>
    /// The token's own claim about its lifetime. Deliberately not called "IsValid": expiry is the
    /// one thing a client can read off a token without the signing key.
    /// </summary>
    public bool IsExpiredByItsOwnClaim => ExpiresAt is not null && ExpiresAt <= DateTimeOffset.UtcNow;

    /// <summary>
    /// Scopes the request needs that the token does not carry, so the panel can warn
    /// "Orders.Write not granted — writes will 403" before the send rather than after.
    /// </summary>
    public IReadOnlyList<string> MissingScopes(IEnumerable<string> required) =>
        [.. required.Where(r => !Scopes.Contains(r, StringComparer.OrdinalIgnoreCase))];

    public string DescribeExpiry() => TimeUntilExpiry switch
    {
        null => "no expiry claim",
        { TotalSeconds: <= 0 } => "expired",
        { TotalMinutes: < 60 } t => $"in {(int)t.TotalMinutes}m",
        { TotalHours: < 24 } t => $"in {(int)t.TotalHours}h",
        { } t => $"in {(int)t.TotalDays}d",
    };
}

public sealed record TokenClaim(string Type, string Value);
