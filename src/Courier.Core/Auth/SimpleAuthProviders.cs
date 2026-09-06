using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Courier.Core.Abstractions;

namespace Courier.Core.Auth;

/// <summary>Bearer token from the credential store. ENT-06.</summary>
public sealed class BearerAuthProvider(ISecretStore secrets) : IAuthProvider
{
    public AuthKind Kind => AuthKind.Bearer;

    public async ValueTask<AuthResult> ApplyAsync(AuthProfile profile, AuthContext context, CancellationToken ct = default)
    {
        var token = await secrets.GetAsync(profile.SecretKey, ct).ConfigureAwait(false);
        if (token is null)
        {
            throw new InteractiveAuthRequiredException(profile.Name, "No token is stored for this profile.");
        }

        return new AuthResult(
            [new KeyValuePair<string, string>("Authorization", $"Bearer {token}")],
            [],
            Token: token);
    }
}

/// <summary>HTTP basic. ENT-06.</summary>
public sealed class BasicAuthProvider(ISecretStore secrets) : IAuthProvider
{
    public AuthKind Kind => AuthKind.Basic;

    public async ValueTask<AuthResult> ApplyAsync(AuthProfile profile, AuthContext context, CancellationToken ct = default)
    {
        var password = await secrets.GetAsync(profile.SecretKey, ct).ConfigureAwait(false) ?? string.Empty;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{profile.Username}:{password}"));

        return new AuthResult(
            [new KeyValuePair<string, string>("Authorization", $"Basic {encoded}")],
            [],
            Identity: profile.Username);
    }
}

/// <summary>API key in a header or the query string. ENT-06.</summary>
public sealed class ApiKeyAuthProvider(ISecretStore secrets) : IAuthProvider
{
    public AuthKind Kind => AuthKind.ApiKey;

    public async ValueTask<AuthResult> ApplyAsync(AuthProfile profile, AuthContext context, CancellationToken ct = default)
    {
        var key = await secrets.GetAsync(profile.SecretKey, ct).ConfigureAwait(false);
        if (key is null)
        {
            throw new InteractiveAuthRequiredException(profile.Name, "No API key is stored for this profile.");
        }

        var name = profile.ApiKeyHeader ?? "X-Api-Key";
        var pair = new KeyValuePair<string, string>(name, key);

        return profile.ApiKeyLocation.Equals("query", StringComparison.OrdinalIgnoreCase)
            ? new AuthResult([], [pair])
            : new AuthResult([pair], []);
    }
}

/// <summary>
/// AWS Signature Version 4. ENT-06.
/// </summary>
/// <remarks>
/// Implemented here rather than taken as a dependency: the AWS SDK would pull a large surface for
/// one signing algorithm, and the algorithm is stable and fully specified.
/// </remarks>
public sealed class AwsSigV4AuthProvider(ISecretStore secrets) : IAuthProvider
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    public AuthKind Kind => AuthKind.AwsSigV4;

    public async ValueTask<AuthResult> ApplyAsync(AuthProfile profile, AuthContext context, CancellationToken ct = default)
    {
        var secretKey = await secrets.GetAsync(profile.SecretKey, ct).ConfigureAwait(false);
        if (secretKey is null || profile.AwsAccessKeyId is null)
        {
            throw new InteractiveAuthRequiredException(
                profile.Name,
                "This profile needs an access key id and a secret access key.");
        }

        var region = profile.AwsRegion ?? "us-east-1";
        var service = profile.AwsService ?? "execute-api";
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var payloadHash = Hex(SHA256.HashData(context.Body ?? []));
        var host = context.TargetUri.IsDefaultPort
            ? context.TargetUri.Host
            : $"{context.TargetUri.Host}:{context.TargetUri.Port}";

        var canonicalRequest = string.Join(
            '\n',
            context.Method.ToUpperInvariant(),
            CanonicalPath(context.TargetUri),
            CanonicalQuery(context.TargetUri),
            $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n",
            "host;x-amz-content-sha256;x-amz-date",
            payloadHash);

        var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
        var stringToSign = string.Join(
            '\n',
            Algorithm,
            amzDate,
            credentialScope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = SigningKey(secretKey, dateStamp, region, service);
        var signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        var authorization =
            $"{Algorithm} Credential={profile.AwsAccessKeyId}/{credentialScope}, "
            + "SignedHeaders=host;x-amz-content-sha256;x-amz-date, "
            + $"Signature={signature}";

        return new AuthResult(
            [
                new KeyValuePair<string, string>("Authorization", authorization),
                new KeyValuePair<string, string>("x-amz-date", amzDate),
                new KeyValuePair<string, string>("x-amz-content-sha256", payloadHash),
            ],
            [],
            Identity: profile.AwsAccessKeyId);
    }

    private static byte[] SigningKey(string secret, string dateStamp, string region, string service)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes($"AWS4{secret}"), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, "aws4_request"u8.ToArray());
    }

    private static string CanonicalPath(Uri uri) =>
        string.IsNullOrEmpty(uri.AbsolutePath)
            ? "/"
            : string.Join('/', uri.AbsolutePath.Split('/').Select(Uri.EscapeDataString));

    private static string CanonicalQuery(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query))
        {
            return string.Empty;
        }

        var pairs = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var split = p.Split('=', 2);
                return (Name: Uri.EscapeDataString(Uri.UnescapeDataString(split[0])),
                        Value: split.Length > 1 ? Uri.EscapeDataString(Uri.UnescapeDataString(split[1])) : string.Empty);
            })
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);

        return string.Join('&', pairs.Select(p => $"{p.Name}={p.Value}"));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

/// <summary>
/// NTLM or Kerberos with the current Windows identity. ENT-07.
/// </summary>
/// <remarks>
/// Contributes no header. Integrated auth is a handler concern, so this provider's job is to say
/// so — the executor sets <c>UseIntegratedAuth</c> on the handler profile and the handler does the
/// challenge-response. There is no credential to enter, which is the entire point.
/// </remarks>
public sealed class IntegratedAuthProvider(IIntegratedAuthProvider platform) : IAuthProvider
{
    public AuthKind Kind => AuthKind.IntegratedWindows;

    public ValueTask<AuthResult> ApplyAsync(AuthProfile profile, AuthContext context, CancellationToken ct = default)
    {
        if (!platform.IsAvailable)
        {
            throw new InteractiveAuthRequiredException(
                profile.Name,
                "Integrated Windows authentication is not available on this machine.");
        }

        return ValueTask.FromResult(new AuthResult([], [], Identity: platform.CurrentIdentity));
    }
}
