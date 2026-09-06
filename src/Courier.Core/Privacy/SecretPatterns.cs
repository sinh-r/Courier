using System.Text.RegularExpressions;

namespace Courier.Core.Privacy;

/// <summary>
/// Detects secret-shaped and person-shaped values. One implementation serves three requirements
/// that must agree with each other: the capsule redaction engine (CAP-03), the warning when a
/// secret-shaped value is typed into a shared environment slot (SEC-03), and log redaction (SEC-07).
/// </summary>
/// <remarks>
/// The bias is deliberately toward false positives. REQUIREMENTS 9 accepts them explicitly: a
/// wrongly-flagged value costs the user one click, a missed one exfiltrates a credential.
/// </remarks>
public static partial class SecretPatterns
{
    /// <summary>Field names whose value is treated as secret regardless of shape.</summary>
    public static readonly IReadOnlySet<string> SecretFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie",
        "x-api-key", "api-key", "apikey", "x-functions-key", "ocp-apim-subscription-key",
        "password", "passwd", "pwd", "secret", "clientsecret", "client_secret",
        "token", "accesstoken", "access_token", "refreshtoken", "refresh_token", "idtoken", "id_token",
        "privatekey", "private_key", "connectionstring", "connection_string",
        "sas", "sastoken", "signature", "credential", "credentials", "auth", "bearer",
    };

    /// <summary>Field names that suggest personal data. Flagged for a human decision, never auto-redacted.</summary>
    public static readonly IReadOnlySet<string> PersonalFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "email", "emailaddress", "e_mail", "mail",
        "phone", "phonenumber", "mobile", "msisdn",
        "firstname", "lastname", "fullname", "surname", "givenname",
        "dob", "dateofbirth", "birthdate",
        "ssn", "nino", "aadhaar", "aadhar", "pan", "passport", "nationalid", "taxid",
        "address", "addressline1", "addressline2", "postcode", "zipcode", "zip",
        "ip", "ipaddress", "clientip", "upn", "username", "userid",
    };

    /// <summary>
    /// The rule sets with separators removed, so a lookup of "X-Api-Key", "x_api_key" and "apiKey"
    /// all land on the same rule. The public sets stay readable; matching happens against these.
    /// </summary>
    private static readonly IReadOnlySet<string> NormalizedSecretNames = Normalize(SecretFieldNames);

    private static readonly IReadOnlySet<string> NormalizedPersonalNames = Normalize(PersonalFieldNames);

    private static IReadOnlySet<string> Normalize(IReadOnlySet<string> names) =>
        names.Select(n => n.Replace("_", string.Empty).Replace("-", string.Empty))
             .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtRegex { get; }

    [GeneratedRegex(@"(?i)\b(?:Server|Data Source|Initial Catalog|User ID|Password|AccountKey|Endpoint)\s*=\s*[^;""\s]+(?:;\s*[A-Za-z ]+\s*=\s*[^;""\s]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringRegex { get; }

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex { get; }

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex { get; }

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKeyRegex { get; }

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenRegex { get; }

    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SlackTokenRegex { get; }

    /// <summary>High-entropy blob: long, no spaces, mixed classes. The catch-all, and the noisiest.</summary>
    [GeneratedRegex(@"^(?=.*[A-Z])(?=.*[a-z])(?=.*\d)[A-Za-z0-9+/=_~.-]{32,}$", RegexOptions.CultureInvariant)]
    private static partial Regex HighEntropyRegex { get; }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex { get; }

    /// <summary>
    /// Classifies a value, optionally in the context of the field name that holds it. The field
    /// name is checked first because <c>"password": "letmein"</c> is a secret whose shape says
    /// nothing at all.
    /// </summary>
    public static SecretClassification Classify(string? value, string? fieldName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SecretClassification.None;
        }

        var leaf = LeafName(fieldName);

        if (leaf is not null && NormalizedSecretNames.Contains(leaf))
        {
            return new SecretClassification(SecretKind.Secret, $"field name '{leaf}' names a credential");
        }

        if (JwtRegex.IsMatch(value))
        {
            return new SecretClassification(SecretKind.Secret, "looks like a JSON web token");
        }

        if (BearerRegex.IsMatch(value))
        {
            return new SecretClassification(SecretKind.Secret, "looks like a bearer token");
        }

        if (ConnectionStringRegex.IsMatch(value))
        {
            return new SecretClassification(SecretKind.Secret, "looks like a connection string");
        }

        if (PrivateKeyRegex.IsMatch(value))
        {
            return new SecretClassification(SecretKind.Secret, "contains a private key");
        }

        if (AwsAccessKeyRegex.IsMatch(value))
        {
            return new SecretClassification(SecretKind.Secret, "looks like an AWS access key id");
        }

        if (GitHubTokenRegex.IsMatch(value))
        {
            return new SecretClassification(SecretKind.Secret, "looks like a GitHub token");
        }

        if (SlackTokenRegex.IsMatch(value))
        {
            return new SecretClassification(SecretKind.Secret, "looks like a Slack token");
        }

        if (leaf is not null && NormalizedPersonalNames.Contains(leaf))
        {
            return new SecretClassification(SecretKind.PossiblePersonalData, "looks like personal data");
        }

        if (EmailRegex.IsMatch(value))
        {
            return new SecretClassification(SecretKind.PossiblePersonalData, "looks like personal data");
        }

        if (HighEntropyRegex.IsMatch(value))
        {
            return new SecretClassification(SecretKind.Secret, "high-entropy value, so it may be a credential");
        }

        return SecretClassification.None;
    }

    /// <summary>
    /// Replaces every recognised secret in free text with a fixed marker. Used by the log
    /// redactor, where there is no field name to consult and no chance to ask the user.
    /// </summary>
    public static string Scrub(string text, string marker = "[redacted]")
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var scrubbed = JwtRegex.Replace(text, marker);
        scrubbed = BearerRegex.Replace(scrubbed, $"Bearer {marker}");
        scrubbed = ConnectionStringRegex.Replace(scrubbed, marker);
        scrubbed = AwsAccessKeyRegex.Replace(scrubbed, marker);
        scrubbed = GitHubTokenRegex.Replace(scrubbed, marker);
        scrubbed = SlackTokenRegex.Replace(scrubbed, marker);
        scrubbed = PrivateKeyRegex.Replace(scrubbed, marker);
        return scrubbed;
    }

    /// <summary>Takes the last segment of a dotted path, so "body.customer.pan" tests as "pan".</summary>
    private static string? LeafName(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        var lastDot = fieldName.LastIndexOf('.');
        var leaf = lastDot >= 0 ? fieldName[(lastDot + 1)..] : fieldName;
        return leaf.Replace("_", string.Empty).Replace("-", string.Empty);
    }
}

public enum SecretKind
{
    /// <summary>Nothing detected.</summary>
    None,

    /// <summary>Auto-replaced on export, hidden in logs, warned about in a shared environment slot.</summary>
    Secret,

    /// <summary>Flagged for a human decision. Never redacted automatically. CAP-04.</summary>
    PossiblePersonalData,
}

/// <param name="Reason">Shown to the user verbatim, so it reads as a sentence fragment.</param>
public readonly record struct SecretClassification(SecretKind Kind, string? Reason)
{
    public static readonly SecretClassification None = new(SecretKind.None, null);

    public bool IsSecret => Kind == SecretKind.Secret;

    public bool NeedsDecision => Kind == SecretKind.PossiblePersonalData;
}
