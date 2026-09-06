namespace Courier.Core.Auth;

/// <summary>
/// Produces the credential material for one request. Never persists anything.
/// </summary>
public interface IAuthProvider
{
    AuthKind Kind { get; }

    /// <summary>
    /// Applies the profile to a request. Implementations mutate the header list and the handler
    /// profile rather than the request message, so the capsule exporter can see exactly what was
    /// added and redact it.
    /// </summary>
    ValueTask<AuthResult> ApplyAsync(AuthProfile profile, AuthContext context, CancellationToken ct = default);
}

/// <param name="TargetUri">Needed by SigV4, which signs the canonical request.</param>
/// <param name="Method">Needed by SigV4.</param>
/// <param name="Body">Needed by SigV4 for the payload hash.</param>
public sealed record AuthContext(Uri TargetUri, string Method, byte[]? Body);

/// <summary>
/// What the provider contributed, kept apart from the request so it can be redacted precisely.
/// </summary>
/// <param name="Headers">Headers to add. Values here are secret by definition.</param>
/// <param name="QueryParameters">For API keys that must travel in the query string.</param>
/// <param name="Token">The raw token, when there is one, for the decoded token panel. ENT-04.</param>
/// <param name="ExpiresOn">Drives "expires in 47m" and the silent refresh. ENT-05.</param>
/// <param name="Interactive">
/// True when the provider had to prompt. ENT-05 requires never blocking a send on an interactive
/// prompt without warning, so the caller checks this before the first send of a session.
/// </param>
public sealed record AuthResult(
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    IReadOnlyList<KeyValuePair<string, string>> QueryParameters,
    string? Token = null,
    DateTimeOffset? ExpiresOn = null,
    bool Interactive = false,
    string? Identity = null,
    IReadOnlyList<string>? GrantedScopes = null)
{
    public static readonly AuthResult None = new([], []);

    /// <summary>Every secret value contributed, for registration with the log redactor. SEC-07.</summary>
    public IEnumerable<string> SecretValues()
    {
        foreach (var header in Headers)
        {
            yield return header.Value;
        }

        foreach (var query in QueryParameters)
        {
            yield return query.Value;
        }

        if (Token is not null)
        {
            yield return Token;
        }
    }
}

/// <summary>
/// Raised when a send would need an interactive prompt the user has not been warned about. ENT-05.
/// </summary>
public sealed class InteractiveAuthRequiredException(string profileName, string reason)
    : Exception($"'{profileName}' needs you to sign in before this request can be sent. {reason}")
{
    public string ProfileName { get; } = profileName;
}
