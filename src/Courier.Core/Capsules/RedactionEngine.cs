using System.Text;
using System.Text.Json;
using Courier.Core.Privacy;

namespace Courier.Core.Capsules;

/// <summary>
/// Replaces credentials with placeholders that rebind to the importer's own environment. CAP-03.
/// </summary>
/// <remarks>
/// <para>
/// Three tiers, and they get different treatment on the export screen because they need different
/// things from the user:
/// </para>
/// <list type="bullet">
///   <item><b>Included as-is.</b> Neutral. Nothing to decide.</item>
///   <item><b>Auto-replaced.</b> Trust colours. Courier is confident, and shows its working so the
///   user can check it.</item>
///   <item><b>Flagged for decision.</b> The most visual weight, because it needs a human. CAP-04
///   requires a decision, not a default — Courier will not guess about personal data.</item>
/// </list>
/// <para>
/// The bias is toward false positives throughout. REQUIREMENTS 9 accepts them explicitly, and the
/// asymmetry is stark: a wrongly-flagged field costs one click, a missed one exfiltrates a
/// credential into a bug tracker.
/// </para>
/// </remarks>
public sealed class RedactionEngine
{
    /// <summary>Known values Courier is certain are secret, mapped to the placeholder they earn.</summary>
    private readonly Dictionary<string, string> _knownSecrets = new(StringComparer.Ordinal);

    /// <summary>Field-name rules the user configured, beyond the built-in set. CAP-03.</summary>
    public List<string> AdditionalSecretFieldNames { get; } = [];

    /// <summary>
    /// Registers a value Courier knows is a credential because it read it from the credential store
    /// or an auth provider produced it. This is what catches a short API key that no pattern would.
    /// </summary>
    public void RegisterKnownSecret(string? value, string placeholder)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length >= 4)
        {
            _knownSecrets[value] = placeholder;
        }
    }

    /// <summary>
    /// Produces the review the export screen shows and the redacted payload it would write.
    /// Nothing is written until the user has seen this. CAP-04.
    /// </summary>
    public RedactionReview Review(CapsuleRequest request, CapsuleResponse? response)
    {
        var included = new List<IncludedItem>();
        var replaced = new List<ReplacedItem>();
        var flagged = new List<FlaggedItem>();

        included.Add(new IncludedItem("Request", "method, URL, headers, body"));

        if (response is not null)
        {
            included.Add(new IncludedItem(
                "Response",
                $"status, headers, body ({FormatSize(response.ContentLength)})"));
        }

        ScanHeaders(request.Headers, "request", replaced, flagged);

        if (response is not null)
        {
            ScanHeaders(response.Headers, "response", replaced, flagged);
        }

        ScanUrl(request.Url, replaced, flagged);
        ScanBody(request.Body, request.ContentType, "body", replaced, flagged);
        ScanBody(response?.Body, response?.ContentType, "response.body", replaced, flagged);

        return new RedactionReview(included, replaced, flagged);
    }

    /// <summary>
    /// Applies the review's decisions, returning a copy safe to write. The originals are untouched,
    /// so cancelling an export cannot corrupt what is on screen.
    /// </summary>
    public (CapsuleRequest Request, CapsuleResponse? Response, RedactionReport Report) Apply(
        CapsuleRequest request,
        CapsuleResponse? response,
        RedactionReview review,
        IReadOnlyDictionary<string, bool> flaggedDecisions)
    {
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var item in review.Replaced)
        {
            substitutions[item.OriginalValue] = item.Placeholder;
        }

        foreach (var item in review.Flagged)
        {
            // Absent from the dictionary means the user has not decided. Redact, because the
            // conservative default is the one that cannot leak.
            if (!flaggedDecisions.TryGetValue(item.Location, out var keep) || !keep)
            {
                substitutions[item.OriginalValue] = "{{redacted}}";
            }
        }

        var redactedRequest = new CapsuleRequest
        {
            Method = request.Method,
            Url = Substitute(request.Url, substitutions),
            ContentType = request.ContentType,
            BodyIsBase64 = request.BodyIsBase64,
            Body = request.BodyIsBase64 ? request.Body : Substitute(request.Body, substitutions),
            Headers = [.. request.Headers.Select(h => h with { Value = Substitute(h.Value, substitutions) })],
        };

        CapsuleResponse? redactedResponse = null;
        if (response is not null)
        {
            redactedResponse = new CapsuleResponse
            {
                Status = response.Status,
                ReasonPhrase = response.ReasonPhrase,
                ContentType = response.ContentType,
                ContentLength = response.ContentLength,
                ElapsedMilliseconds = response.ElapsedMilliseconds,
                TransportFailure = response.TransportFailure,
                BodyIsBase64 = response.BodyIsBase64,
                Body = response.BodyIsBase64 ? response.Body : Substitute(response.Body, substitutions),
                Headers = [.. response.Headers.Select(h => h with { Value = Substitute(h.Value, substitutions) })],
            };
        }

        var entries = new List<RedactionEntry>();

        foreach (var item in review.Replaced)
        {
            entries.Add(new RedactionEntry(item.Location, item.Placeholder, item.Reason, WasAutomatic: true));
        }

        foreach (var item in review.Flagged)
        {
            var kept = flaggedDecisions.TryGetValue(item.Location, out var keep) && keep;
            entries.Add(new RedactionEntry(
                item.Location,
                kept ? "(kept by the exporter)" : "{{redacted}}",
                item.Reason,
                WasAutomatic: false));
        }

        return (redactedRequest, redactedResponse, new RedactionReport(entries));
    }

    private void ScanHeaders(
        List<CapsuleHeader> headers,
        string prefix,
        List<ReplacedItem> replaced,
        List<FlaggedItem> flagged)
    {
        foreach (var header in headers)
        {
            var location = $"{prefix}.{header.Name}";

            if (_knownSecrets.TryGetValue(header.Value, out var known))
            {
                replaced.Add(new ReplacedItem(header.Name, header.Value, known, "Courier issued this credential"));
                continue;
            }

            var classification = Classify(header.Value, header.Name);

            if (classification.IsSecret)
            {
                replaced.Add(new ReplacedItem(
                    header.Name,
                    header.Value,
                    PlaceholderFor(header.Name),
                    classification.Reason ?? "looks like a credential"));
            }
            else if (classification.NeedsDecision)
            {
                flagged.Add(new FlaggedItem(location, header.Value, classification.Reason ?? "looks like personal data"));
            }
        }
    }

    private void ScanUrl(string url, List<ReplacedItem> replaced, List<FlaggedItem> flagged)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query))
        {
            return;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            if (split.Length != 2)
            {
                continue;
            }

            var name = Uri.UnescapeDataString(split[0]);
            var value = Uri.UnescapeDataString(split[1]);
            var classification = Classify(value, name);

            if (classification.IsSecret)
            {
                replaced.Add(new ReplacedItem(
                    $"query.{name}",
                    value,
                    PlaceholderFor(name),
                    classification.Reason ?? "looks like a credential"));
            }
            else if (classification.NeedsDecision)
            {
                flagged.Add(new FlaggedItem($"query.{name}", value, classification.Reason ?? "looks like personal data"));
            }
        }
    }

    /// <summary>
    /// Walks a JSON body field by field so the report can name <c>body.customer.pan</c> rather than
    /// saying "something in the body". Non-JSON bodies are scanned as text by pattern only.
    /// </summary>
    private void ScanBody(
        string? body,
        string? contentType,
        string prefix,
        List<ReplacedItem> replaced,
        List<FlaggedItem> flagged)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                WalkJson(document.RootElement, prefix, replaced, flagged);
                return;
            }
            catch (JsonException)
            {
                // Fall through to the text scan; a body that claims to be JSON and is not still
                // deserves a look.
            }
        }

        foreach (var (value, reason) in FindPatternsInText(body))
        {
            replaced.Add(new ReplacedItem(prefix, value, "{{redacted}}", reason));
        }
    }

    private void WalkJson(
        JsonElement element,
        string path,
        List<ReplacedItem> replaced,
        List<FlaggedItem> flagged)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    WalkJson(property.Value, $"{path}.{property.Name}", replaced, flagged);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    WalkJson(item, $"{path}[{index++}]", replaced, flagged);
                }

                break;

            case JsonValueKind.String:
                var value = element.GetString();
                if (string.IsNullOrEmpty(value))
                {
                    return;
                }

                var fieldName = path[(path.LastIndexOf('.') + 1)..];

                if (_knownSecrets.TryGetValue(value, out var known))
                {
                    replaced.Add(new ReplacedItem(path, value, known, "Courier issued this credential"));
                    return;
                }

                var classification = Classify(value, fieldName);

                if (classification.IsSecret)
                {
                    replaced.Add(new ReplacedItem(
                        path,
                        value,
                        PlaceholderFor(fieldName),
                        classification.Reason ?? "looks like a credential"));
                }
                else if (classification.NeedsDecision)
                {
                    flagged.Add(new FlaggedItem(path, value, classification.Reason ?? "looks like personal data"));
                }

                break;
        }
    }

    private IEnumerable<(string Value, string Reason)> FindPatternsInText(string text)
    {
        // Line-by-line so a placeholder replaces the credential and not the whole payload.
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 8)
            {
                continue;
            }

            var classification = SecretPatterns.Classify(trimmed);
            if (classification.IsSecret)
            {
                yield return (trimmed, classification.Reason ?? "looks like a credential");
            }
        }
    }

    private SecretClassification Classify(string value, string? fieldName)
    {
        if (fieldName is not null
            && AdditionalSecretFieldNames.Contains(fieldName, StringComparer.OrdinalIgnoreCase))
        {
            return new SecretClassification(SecretKind.Secret, $"'{fieldName}' matches your redaction rules");
        }

        return SecretPatterns.Classify(value, fieldName);
    }

    /// <summary>
    /// A placeholder the importer's environment can rebind. CAP-03: the point is not to erase the
    /// value but to leave a slot the recipient's own credential slides into.
    /// </summary>
    private static string PlaceholderFor(string fieldName)
    {
        var normalized = fieldName.Replace("-", string.Empty).Replace("_", string.Empty);

        return normalized.ToLowerInvariant() switch
        {
            "authorization" => "{{auth.token}}",
            "proxyauthorization" => "{{auth.proxy}}",
            "cookie" or "setcookie" => "{{session.cookie}}",
            _ => $"{{{{secret.{CamelCase(normalized)}}}}}",
        };
    }

    private static string CamelCase(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    private static string? Substitute(string? text, Dictionary<string, string> substitutions)
    {
        if (string.IsNullOrEmpty(text) || substitutions.Count == 0)
        {
            return text;
        }

        var result = new StringBuilder(text);

        // Longest first, so a token that contains another value is replaced whole.
        foreach (var (original, placeholder) in substitutions.OrderByDescending(s => s.Key.Length))
        {
            result.Replace(original, placeholder);
        }

        return result.ToString();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}

/// <summary>
/// The mandatory pre-export review. CAP-04: everything included, everything replaced, and
/// everything flagged as possible personal data requiring a decision.
/// </summary>
public sealed record RedactionReview(
    IReadOnlyList<IncludedItem> Included,
    IReadOnlyList<ReplacedItem> Replaced,
    IReadOnlyList<FlaggedItem> Flagged)
{
    public bool NeedsDecision => Flagged.Count > 0;
}

public sealed record IncludedItem(string Name, string Detail);

/// <param name="OriginalValue">Never written to the capsule. Held only to perform the substitution.</param>
public sealed record ReplacedItem(string Location, string OriginalValue, string Placeholder, string Reason)
{
    /// <summary>An abbreviated original, so the review can show what was found without exposing it.</summary>
    public string Preview => OriginalValue.Length <= 12
        ? new string('•', OriginalValue.Length)
        : $"{OriginalValue[..6]}…";
}

public sealed record FlaggedItem(string Location, string OriginalValue, string Reason)
{
    public string Preview => OriginalValue.Length <= 20 ? OriginalValue : $"{OriginalValue[..14]}…";
}

/// <summary>Written into the capsule as redactions.json. The recipient sees exactly what changed.</summary>
public sealed record RedactionReport(IReadOnlyList<RedactionEntry> Entries);

public sealed record RedactionEntry(string Location, string Placeholder, string Reason, bool WasAutomatic);
