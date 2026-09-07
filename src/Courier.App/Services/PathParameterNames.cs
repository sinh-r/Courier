using Courier.Core.Variables;
using Courier.Scanner.Routing;

namespace Courier.App.Services;

/// <summary>
/// Route-token names in a URL, for the Path tab.
/// </summary>
/// <remarks>
/// <see cref="RouteResolver.ParameterNames"/> treats <c>{{</c> as nested braces — on
/// <c>{{baseUrl}}/articles/{slug}</c> it returns <c>baseUrl</c> as well as <c>slug</c>, since that
/// method was written for route templates that never contain a <c>{{variable}}</c> reference in the
/// first place. This masks every <c>{{...}}</c> span before scanning, so a URL that mixes both
/// mechanisms — which every scanned request does, via <c>{{baseUrl}}</c> — reports only the route
/// tokens.
/// </remarks>
public static class PathParameterNames
{
    public static IReadOnlyList<string> From(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return [];
        }

        return RouteResolver.ParameterNames(Mask(url));
    }

    private static string Mask(string url)
    {
        var references = VariableResolver.FindReferences(url);
        if (references.Count == 0)
        {
            return url;
        }

        var chars = url.ToCharArray();
        foreach (var reference in references)
        {
            for (var i = reference.Start; i < reference.Start + reference.Length; i++)
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
