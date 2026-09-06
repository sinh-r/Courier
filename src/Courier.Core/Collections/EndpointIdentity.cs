using System.Security.Cryptography;
using System.Text;

namespace Courier.Core.Collections;

/// <summary>
/// The stable identity that joins a generated endpoint to the user's edits of it. SCAN-09.
/// </summary>
/// <remarks>
/// <para>
/// TECH_SPEC 3.6: a hash of (verb, route template, controller type name), stable across body
/// changes and reordering, which is what makes rename detection possible in the diff.
/// </para>
/// <para>
/// Getting this wrong is the failure mode that makes the whole feature hostile — a user's saved
/// payload silently reattaching to the wrong endpoint, or detaching from the right one. It is
/// therefore deliberately narrow: only three inputs, all normalised, and nothing that changes when
/// a developer edits a method body. Changing what goes into this hash orphans every existing
/// overlay, so treat it as a file format change and version it.
/// </para>
/// </remarks>
public static class EndpointIdentity
{
    /// <param name="verb">HTTP method. Case-insensitive.</param>
    /// <param name="routeTemplate">
    /// The route template with tokens intact, e.g. <c>api/v{version}/orders/{id}</c>. Parameter
    /// names matter; their constraints and defaults do not, and are stripped.
    /// </param>
    /// <param name="declaringType">
    /// Fully qualified controller type name, or the endpoint group for a minimal API. This is what
    /// keeps two different controllers exposing the same route apart.
    /// </param>
    public static string Compute(string verb, string routeTemplate, string declaringType)
    {
        var canonical = string.Join(
            '\n',
            verb.Trim().ToUpperInvariant(),
            NormaliseRoute(routeTemplate),
            declaringType.Trim());

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        // 128 bits is far more than enough to keep a few thousand endpoints apart, and a 32-char id
        // stays readable in a YAML file a human has to diff.
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Reduces a route template to the form that identity depends on: leading and trailing slashes
    /// removed, casing folded, and route constraints, defaults and optional markers stripped from
    /// parameters so <c>{id:int}</c>, <c>{id?}</c> and <c>{id=1}</c> are one endpoint, not three.
    /// </summary>
    public static string NormaliseRoute(string routeTemplate)
    {
        if (string.IsNullOrWhiteSpace(routeTemplate))
        {
            return string.Empty;
        }

        var trimmed = routeTemplate.Trim().Trim('/');
        var sb = new StringBuilder(trimmed.Length);
        var depth = 0;
        var suppressing = false;

        foreach (var c in trimmed)
        {
            switch (c)
            {
                case '{':
                    depth++;
                    suppressing = false;
                    sb.Append(c);
                    break;

                case '}':
                    depth--;
                    suppressing = false;
                    sb.Append(c);
                    break;

                // Inside a parameter, everything from the first ':' or '=' or '?' is a constraint
                // or a default. It does not change which endpoint this is.
                case ':' or '=' or '?' when depth > 0:
                    suppressing = true;
                    break;

                // A catch-all marker is a binding detail, not an identity. {path}, {*path} and
                // {**path} address the same endpoint, and a developer tightening a route from one
                // to another must not orphan the user's saved payload.
                case '*' when depth > 0:
                    break;

                default:
                    if (!suppressing)
                    {
                        sb.Append(char.ToLowerInvariant(c));
                    }

                    break;
            }
        }

        return sb.ToString();
    }
}
