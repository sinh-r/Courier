using Courier.Core.Collections;
using Courier.Scanner.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Courier.Scanner.Syntax;

/// <summary>
/// Derives endpoints by parsing <c>.cs</c> files directly. SCAN-01, SCAN-08.
/// </summary>
/// <remarks>
/// <para>
/// No restore, no build, no NuGet resolution. TECH_SPEC 3.4 is right about why this tier ships
/// first: "Semantic-only would break on most real codebases, which do not restore cleanly on first
/// contact." A scanner that needs a green build before it says anything is a scanner nobody gets to
/// try.
/// </para>
/// <para>
/// The cost is that types cannot be followed across files, so request bodies come out as a named
/// placeholder rather than a shaped sample. That is recorded per endpoint as a partial-resolution
/// note rather than left as a surprise.
/// </para>
/// </remarks>
public sealed class ControllerSyntaxScanner
{
    private static readonly string[] MethodAttributes =
    [
        "HttpGet", "HttpPost", "HttpPut", "HttpPatch", "HttpDelete", "HttpHead", "HttpOptions",
    ];

    /// <summary>Parses one file. Never throws on malformed code — that is the point of this tier.</summary>
    public IReadOnlyList<object> ScanFile(string path, string text)
    {
        var results = new List<object>();
        var tree = CSharpSyntaxTree.ParseText(text, path: path);
        var root = tree.GetCompilationUnitRoot();

        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!LooksLikeController(type))
            {
                continue;
            }

            ScanController(type, path, results);
        }

        return results;
    }

    /// <summary>
    /// A class is a controller if it says so by name, base type or attribute. Deliberately
    /// generous: a false positive produces an endpoint the user can delete, a false negative
    /// produces an endpoint that silently does not exist.
    /// </summary>
    private static bool LooksLikeController(ClassDeclarationSyntax type)
    {
        if (type.Identifier.Text.EndsWith("Controller", StringComparison.Ordinal))
        {
            return true;
        }

        if (type.BaseList?.Types.Any(
                b => b.Type.ToString() is "ControllerBase" or "Controller"
                     || b.Type.ToString().EndsWith("ControllerBase", StringComparison.Ordinal)) == true)
        {
            return true;
        }

        return SyntaxHelpers.HasAttribute(type.AttributeLists, "ApiController");
    }

    private static void ScanController(ClassDeclarationSyntax type, string path, List<object> results)
    {
        var controllerName = type.Identifier.Text;
        var declaringType = QualifiedName(type);
        var controllerRoute = SyntaxHelpers.AttributeArgument(type.AttributeLists, "Route", 0);
        var apiVersion = SyntaxHelpers.AttributeArgument(type.AttributeLists, "ApiVersion", 0);
        var controllerAuth = ReadAuthorization(type.AttributeLists);

        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(SyntaxKind.PublicKeyword))
            {
                continue;
            }

            var verbs = ReadVerbs(method.AttributeLists);
            if (verbs.Count == 0)
            {
                continue;
            }

            var actionName = method.Identifier.Text;
            var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var methodAuth = ReadAuthorization(method.AttributeLists);
            var authorization = Merge(controllerAuth, methodAuth);

            foreach (var (verb, actionRoute, unresolvedExpression) in verbs)
            {
                // A template the syntax tier cannot read is reported, never guessed at. Guessing
                // here means silently colliding with another action on the same prefix.
                if (unresolvedExpression is not null)
                {
                    results.Add(new UnresolvedEndpoint(
                        declaringType,
                        actionName,
                        path,
                        line,
                        $"The route template is '{unresolvedExpression}', which is not a literal. "
                        + "Load the solution so Courier can resolve the constant, or inline the "
                        + "string in the attribute."));

                    continue;
                }

                var template = RouteResolver.Combine(
                    controllerRoute,
                    actionRoute,
                    controllerName,
                    actionName,
                    apiVersion);

                if (template.Length == 0 && controllerRoute is null)
                {
                    results.Add(new UnresolvedEndpoint(
                        declaringType,
                        actionName,
                        path,
                        line,
                        "No route could be derived. The controller has no [Route] and the action's "
                        + "attribute carries no template, so the URL depends on conventional routing "
                        + "configured in Program.cs."));

                    continue;
                }

                var notes = new List<string>();
                var parameters = SyntaxHelpers.ReadParameters(method.ParameterList.Parameters, template, notes);

                results.Add(new ScannedEndpoint(
                    EndpointIdentity.Compute(verb, template, declaringType),
                    verb,
                    template,
                    declaringType,
                    actionName,
                    path,
                    line)
                {
                    Parameters = parameters,
                    Authorization = authorization,
                    RequiredScopes = SyntaxHelpers.ScopesFrom(authorization),
                    Responses = SyntaxHelpers.ReadDeclaredResponses(method.AttributeLists),
                    SampleBody = null,
                    PartialResolutionNotes = notes,
                    Summary = SyntaxHelpers.ReadSummary(method),
                });
            }
        }
    }

    /// <summary>
    /// Every HTTP verb attribute on the method, with its own template.
    /// </summary>
    /// <remarks>
    /// The <c>UnresolvedExpression</c> field is the important one. An attribute whose template is a
    /// constant from another file — <c>[HttpGet(RouteConstants.SearchPath)]</c> — cannot be read by
    /// the syntax tier. Treating that as "no template" is the subtle failure REQUIREMENTS 9 warns
    /// about: the endpoint silently takes the controller prefix, collides with whichever other
    /// action already sits there, and one of the two disappears from the collection entirely.
    /// </remarks>
    private static List<VerbAttribute> ReadVerbs(SyntaxList<AttributeListSyntax> lists)
    {
        var verbs = new List<VerbAttribute>();

        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            var name = SyntaxHelpers.AttributeName(attribute);
            var match = MethodAttributes.FirstOrDefault(a => name == a || name == a + "Attribute");

            if (match is not null)
            {
                verbs.Add(Read(match["Http".Length..].ToUpperInvariant(), attribute));
            }
            else if (name is "Route" or "RouteAttribute" && verbs.Count == 0)
            {
                // [Route] with no verb attribute matches all verbs. GET is the useful default and
                // the note says so rather than inventing seven endpoints.
                verbs.Add(Read("GET", attribute));
            }
        }

        return verbs;

        static VerbAttribute Read(string verb, AttributeSyntax attribute)
        {
            var argument = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression;

            if (argument is null)
            {
                return new VerbAttribute(verb, null, null);
            }

            var literal = SyntaxHelpers.StringValue(argument);
            return literal is not null
                ? new VerbAttribute(verb, literal, null)
                : new VerbAttribute(verb, null, argument.ToString());
        }
    }

    /// <param name="UnresolvedExpression">
    /// The source text of a template the syntax tier could not read, or null when there was
    /// nothing to read or it read cleanly.
    /// </param>
    private readonly record struct VerbAttribute(string Verb, string? Template, string? UnresolvedExpression);

    private static EndpointAuthorization ReadAuthorization(SyntaxList<AttributeListSyntax> lists)
    {
        var policies = new List<string>();
        var roles = new List<string>();
        var requires = false;
        var anonymous = false;

        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            switch (SyntaxHelpers.AttributeName(attribute).Replace("Attribute", string.Empty))
            {
                case "AllowAnonymous":
                    anonymous = true;
                    break;

                case "Authorize":
                    requires = true;

                    foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
                    {
                        var value = SyntaxHelpers.StringValue(argument.Expression);
                        if (value is null)
                        {
                            continue;
                        }

                        var target = argument.NameEquals?.Name.Identifier.Text
                            ?? argument.NameColon?.Name.Identifier.Text
                            ?? "Policy";

                        if (target == "Roles")
                        {
                            roles.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        }
                        else
                        {
                            policies.Add(value);
                        }
                    }

                    break;

                case "RequiredScope":
                    // Microsoft.Identity.Web's attribute. The scopes it names are exactly what
                    // SCAN-06 wants surfaced before the send.
                    foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
                    {
                        if (SyntaxHelpers.StringValue(argument.Expression) is { } scope)
                        {
                            policies.Add(scope);
                        }
                    }

                    requires = true;
                    break;
            }
        }

        return anonymous
            ? EndpointAuthorization.Anonymous
            : new EndpointAuthorization(false, requires, policies, roles);
    }

    private static EndpointAuthorization Merge(EndpointAuthorization controller, EndpointAuthorization action)
    {
        // [AllowAnonymous] on the action wins over [Authorize] on the controller, matching runtime.
        if (action.AllowsAnonymous)
        {
            return EndpointAuthorization.Anonymous;
        }

        return new EndpointAuthorization(
            false,
            controller.RequiresAuthorization || action.RequiresAuthorization,
            [.. controller.Policies, .. action.Policies],
            [.. controller.Roles, .. action.Roles]);
    }

    private static string QualifiedName(ClassDeclarationSyntax type)
    {
        var names = new List<string> { type.Identifier.Text };

        foreach (var ancestor in type.Ancestors())
        {
            switch (ancestor)
            {
                case ClassDeclarationSyntax outer:
                    names.Insert(0, outer.Identifier.Text);
                    break;

                case BaseNamespaceDeclarationSyntax ns:
                    names.Insert(0, ns.Name.ToString());
                    break;
            }
        }

        return string.Join('.', names);
    }
}
