using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Courier.Core.Privacy;

namespace Courier.Core.Auth;

/// <summary>
/// Reuses tokens the developer already has: the Azure CLI and Visual Studio caches. ENT-03.
/// </summary>
/// <remarks>
/// This is the difference between "sign in again" and "it just worked" for a developer who ran
/// <c>az login</c> an hour ago. It is tried before any interactive path, and it fails quietly —
/// a machine without the Azure CLI is the normal case, not an error worth a dialog.
/// </remarks>
public sealed class DeveloperToolTokenSource
{
    private const string Initiator = nameof(DeveloperToolTokenSource);

    private readonly EgressGate _gate;

    public DeveloperToolTokenSource(EgressGate gate) => _gate = gate;

    /// <summary>
    /// Attempts each developer-tool cache in turn. Returns null when none has a usable token,
    /// which is a normal outcome the caller falls through on.
    /// </summary>
    public async Task<DeveloperToolToken?> TryAcquireAsync(
        AuthProfile profile,
        CancellationToken ct = default)
    {
        if (!profile.ReuseDeveloperToolTokens || profile.Scopes.Count == 0)
        {
            return null;
        }

        var context = new TokenRequestContext([.. profile.Scopes], tenantId: profile.Tenant);

        // Azure.Identity opens its own sockets unless given a transport, and SEC-06's statement has
        // to account for every one of them.
        var options = new TokenCredentialOptions
        {
            Transport = new HttpClientTransport(
                _gate.CreateClient(EgressPurpose.AuthAuthority, Initiator)),
        };

        foreach (var (source, credential) in Candidates(profile, options))
        {
            try
            {
                var token = await credential.GetTokenAsync(context, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(token.Token))
                {
                    return new DeveloperToolToken(source, token.Token, token.ExpiresOn);
                }
            }
            catch (CredentialUnavailableException)
            {
                // The tool is not installed, or has no cached sign-in. Try the next one.
            }
            catch (AuthenticationFailedException)
            {
                // A stale cache. Not worth surfacing; the interactive path will handle it.
            }
        }

        return null;
    }

    private static IEnumerable<(string Source, TokenCredential Credential)> Candidates(
        AuthProfile profile,
        TokenCredentialOptions options)
    {
        yield return ("Azure CLI", new AzureCliCredential(new AzureCliCredentialOptions
        {
            TenantId = profile.Tenant,
        }));

        yield return ("Visual Studio", new VisualStudioCredential(new VisualStudioCredentialOptions
        {
            TenantId = profile.Tenant,
        }));

        yield return ("Azure PowerShell", new AzurePowerShellCredential(new AzurePowerShellCredentialOptions
        {
            TenantId = profile.Tenant,
        }));
    }
}

/// <param name="Source">Shown in the auth panel, because "where did this token come from" matters.</param>
public sealed record DeveloperToolToken(string Source, string AccessToken, DateTimeOffset ExpiresOn);
