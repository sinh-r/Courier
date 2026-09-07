using System.Text;
using Courier.Core.Collections;
using Courier.Core.Variables;

namespace Courier.Core.Http;

/// <summary>
/// Turns a <see cref="RequestDefinition"/> into a <see cref="PreparedRequest"/>, or explains in
/// plain English why it could not. CORE-01, CORE-04.
/// </summary>
/// <remarks>
/// <para>
/// Shared between <c>courier run</c> (via <c>CollectionRunner</c>) and the desktop app's Send, which
/// previously had its own copy that built an empty <see cref="VariableScopes"/>, never substituted
/// path parameters, and returned silently on failure. One implementation now, so a fix here fixes
/// both callers and the GUI cannot drift from the CLI's send behaviour again.
/// </para>
/// <para>
/// An unfilled path parameter is refused rather than substituted or left as a token: substituting a
/// blank produces a wrong URL like <c>/articles//comments</c>, and leaving <c>{slug}</c> in place
/// sends a literal, percent-encoded token to a URL that will 404. Neither is what the user meant, so
/// this is the one case a "send anyway" is not offered — unlike an unresolved <c>{{variable}}</c>,
/// which is reported the same way but the caller still decides whether to proceed.
/// </para>
/// </remarks>
public static class RequestPreparer
{
    public static async Task<PreparationResult> PrepareAsync(
        RequestDefinition request,
        VariableScopes scopes,
        VariableResolver variables,
        IReadOnlyList<HeaderValue>? inheritedHeaders = null,
        RequestSettings? collectionSettings = null,
        bool injectTraceParent = false,
        string? environmentName = null,
        CancellationToken ct = default)
    {
        var urlResult = await variables.SubstituteAsync(request.Url, scopes, ct).ConfigureAwait(false);
        var urlText = urlResult.Text;

        var unfilled = new List<string>();
        foreach (var (name, value) in request.PathParams)
        {
            if (value.Length == 0)
            {
                unfilled.Add(name);
                continue;
            }

            urlText = urlText.Replace($"{{{name}}}", value, StringComparison.Ordinal);
        }

        if (urlResult.Unbound.Count > 0)
        {
            return PreparationResult.Fail(
                $"The URL still has unresolved variables: {string.Join(", ", urlResult.Unbound)}");
        }

        if (unfilled.Count > 0)
        {
            return PreparationResult.Fail(
                $"Fill in the path parameter{(unfilled.Count == 1 ? string.Empty : "s")}: "
                + string.Join(", ", unfilled));
        }

        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri))
        {
            return PreparationResult.Fail($"'{urlText}' is not an absolute URL.");
        }

        var enabledQuery = request.Query.Where(q => q.Enabled && q.Name.Length > 0).ToList();
        if (enabledQuery.Count > 0)
        {
            var pairs = new List<string>();
            if (!string.IsNullOrEmpty(uri.Query))
            {
                pairs.Add(uri.Query.TrimStart('?'));
            }

            foreach (var query in enabledQuery)
            {
                var value = await variables.SubstituteAsync(query.Value, scopes, ct).ConfigureAwait(false);
                pairs.Add($"{Uri.EscapeDataString(query.Name)}={Uri.EscapeDataString(value.Text)}");
            }

            uri = new UriBuilder(uri) { Query = string.Join("&", pairs) }.Uri;
        }

        // Layered by name, case-insensitively: inherited headers (app defaults, then collection,
        // already combined by the caller) fill in first, and the request's own headers win.
        var mergedHeaders = new Dictionary<string, HeaderValue>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in inheritedHeaders ?? [])
        {
            mergedHeaders[header.Name] = header;
        }

        foreach (var header in request.Headers.Where(h => h.Enabled && h.Name.Length > 0))
        {
            mergedHeaders[header.Name] = header;
        }

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var header in mergedHeaders.Values)
        {
            var value = await variables.SubstituteAsync(header.Value, scopes, ct).ConfigureAwait(false);
            headers.Add(new KeyValuePair<string, string>(header.Name, value.Text));
        }

        byte[]? bodyBytes = null;
        if (request.Body?.Text is { Length: > 0 } text)
        {
            var body = await variables.SubstituteAsync(text, scopes, ct).ConfigureAwait(false);
            bodyBytes = Encoding.UTF8.GetBytes(body.Text);
        }

        var prepared = new PreparedRequest
        {
            Method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method,
            Url = uri,
            Headers = headers,
            BodyBytes = bodyBytes,
            ContentType = request.Body?.ResolveContentType(),
            Settings = request.Settings.InheritFrom(collectionSettings ?? RequestSettings.Defaults),
            EnvironmentName = environmentName,
            InjectTraceParent = injectTraceParent,
        };

        return PreparationResult.Ok(prepared);
    }
}

/// <param name="Error">
/// Plain, active, specific: what is wrong and where. UI_SPEC 3.7 — never "an error occurred".
/// </param>
public sealed record PreparationResult(PreparedRequest? Request, string? Error)
{
    public bool Succeeded => Request is not null;

    public static PreparationResult Ok(PreparedRequest request) => new(request, null);

    public static PreparationResult Fail(string error) => new(null, error);
}
