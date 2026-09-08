namespace Courier.Core.Collections;

/// <summary>
/// Splits a query string off a URL, and recomposes one from a request's <see cref="QueryParameter"/>
/// rows. Used for display and for import — never for sending. CORE-04 requires the URL bar to keep
/// <c>{{variable}}</c> references legible, so neither direction here percent-encodes or substitutes
/// a variable; <see cref="Http.RequestPreparer"/> owns both of those, once, at send time.
/// </summary>
public static class QueryString
{
    /// <summary>
    /// Splits the query string off <paramref name="url"/>, in the order the parameters appeared. A
    /// URL with no <c>?</c> returns it unchanged with an empty parameter list.
    /// </summary>
    public static (string Url, List<QueryParameter> Parameters) Split(string url)
    {
        var mark = url.IndexOf('?');
        if (mark < 0)
        {
            return (url, []);
        }

        var query = url[(mark + 1)..];
        var baseUrl = url[..mark];
        var parameters = new List<QueryParameter>();

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? string.Empty : pair[(equals + 1)..];

            parameters.Add(new QueryParameter(Uri.UnescapeDataString(name), Uri.UnescapeDataString(value)));
        }

        return (baseUrl, parameters);
    }

    /// <summary>
    /// Appends the enabled, named parameters to <paramref name="url"/>'s query string. A disabled or
    /// unnamed row is left out of the URL, but the caller keeps it in the grid — unchecking a
    /// parameter is not destructive. Any query string already on <paramref name="url"/> is kept and
    /// added to, the same way <c>RequestPreparer</c> layers the grid on top of a hand-typed query
    /// rather than overwriting it.
    /// </summary>
    public static string Compose(string url, IReadOnlyList<QueryParameter> parameters)
    {
        var enabled = parameters.Where(p => p.Enabled && p.Name.Length > 0).ToList();
        if (enabled.Count == 0)
        {
            return url;
        }

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return url + separator + string.Join('&', enabled.Select(p => p.Value.Length == 0 ? p.Name : $"{p.Name}={p.Value}"));
    }
}
