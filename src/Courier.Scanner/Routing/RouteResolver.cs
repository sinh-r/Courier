using System.Text;

namespace Courier.Scanner.Routing;

/// <summary>
/// Resolves route templates, including <c>[controller]</c> and <c>[action]</c> tokens, route
/// prefixes and API versioning. SCAN-02.
/// </summary>
/// <remarks>
/// Route composition in ASP.NET Core has a rule that trips most naive scanners: an action template
/// beginning with <c>/</c> or <c>~/</c> replaces the controller prefix entirely rather than being
/// appended to it. Getting that wrong silently produces endpoints at the wrong URL, which is worse
/// than not finding them, so it is handled explicitly and tested.
/// </remarks>
public static class RouteResolver
{
    /// <param name="controllerRoute">The controller's [Route] template, if any.</param>
    /// <param name="actionRoute">The action's template, if any.</param>
    /// <param name="controllerName">Type name with the "Controller" suffix still attached.</param>
    /// <param name="apiVersion">Value for the {version} token, from [ApiVersion] or a convention.</param>
    public static string Combine(
        string? controllerRoute,
        string? actionRoute,
        string controllerName,
        string actionName,
        string? apiVersion = null,
        string? areaName = null)
    {
        var action = Normalise(actionRoute);

        // An absolute action template ignores the controller prefix. This is the rule that catches
        // people out, and getting it wrong points the request at a URL that does not exist.
        if (action is not null && (action.StartsWith('/') || action.StartsWith("~/", StringComparison.Ordinal)))
        {
            return Expand(action.TrimStart('~').TrimStart('/'), controllerName, actionName, apiVersion, areaName);
        }

        var controller = Normalise(controllerRoute)?.TrimStart('~').TrimStart('/');

        var combined = (controller, action) switch
        {
            (null, null) => string.Empty,
            (null, not null) => action,
            (not null, null) => controller,
            _ => $"{controller.TrimEnd('/')}/{action.TrimStart('/')}",
        };

        return Expand(combined, controllerName, actionName, apiVersion, areaName);
    }

    /// <summary>
    /// Substitutes the token forms ASP.NET Core replaces at startup. The controller token drops the
    /// "Controller" suffix, which is why the raw type name is passed in rather than a cleaned one.
    /// </summary>
    public static string Expand(
        string template,
        string controllerName,
        string actionName,
        string? apiVersion,
        string? areaName)
    {
        if (template.Length == 0)
        {
            return string.Empty;
        }

        var controller = controllerName.EndsWith("Controller", StringComparison.Ordinal)
            ? controllerName[..^"Controller".Length]
            : controllerName;

        var sb = new StringBuilder(template);

        Replace(sb, "[controller]", controller);
        Replace(sb, "{controller}", controller);
        Replace(sb, "[action]", actionName);
        Replace(sb, "{action}", actionName);
        Replace(sb, "[area]", areaName ?? string.Empty);
        Replace(sb, "{area}", areaName ?? string.Empty);

        if (apiVersion is not null)
        {
            // {version:apiVersion} is the conventional form from Asp.Versioning.
            Replace(sb, "{version:apiVersion}", apiVersion);
            Replace(sb, "{version}", apiVersion);
            Replace(sb, "{v:apiVersion}", apiVersion);
        }

        return Tidy(sb.ToString());
    }

    private static void Replace(StringBuilder sb, string token, string value) =>
        sb.Replace(token, value);

    /// <summary>Collapses double slashes and trims the ends, so two templates compose predictably.</summary>
    private static string Tidy(string route)
    {
        var sb = new StringBuilder(route.Length);
        var previousWasSlash = false;

        foreach (var c in route)
        {
            if (c == '/')
            {
                if (previousWasSlash)
                {
                    continue;
                }

                previousWasSlash = true;
            }
            else
            {
                previousWasSlash = false;
            }

            sb.Append(c);
        }

        return sb.ToString().Trim('/');
    }

    private static string? Normalise(string? template) =>
        string.IsNullOrWhiteSpace(template) ? null : template.Trim();

    /// <summary>
    /// The parameter names a template binds from the route, with constraints stripped. Used to
    /// decide which method parameters are route-bound when there is no explicit attribute (SCAN-03).
    /// </summary>
    public static IReadOnlyList<string> ParameterNames(string template)
    {
        var names = new List<string>();
        var depth = 0;
        var start = -1;

        for (var i = 0; i < template.Length; i++)
        {
            switch (template[i])
            {
                case '{':
                    if (depth++ == 0)
                    {
                        start = i + 1;
                    }

                    break;

                case '}':
                    if (--depth == 0 && start >= 0)
                    {
                        var raw = template[start..i];

                        // Strip catch-all markers, constraints, defaults and the optional marker.
                        var name = raw.TrimStart('*');
                        var cut = name.IndexOfAny([':', '=', '?']);
                        if (cut >= 0)
                        {
                            name = name[..cut];
                        }

                        if (name.Length > 0)
                        {
                            names.Add(name);
                        }

                        start = -1;
                    }

                    break;
            }
        }

        return names;
    }
}
