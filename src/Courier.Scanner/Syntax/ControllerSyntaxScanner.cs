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

        return HasAttribute(type.AttributeLists, "ApiController");
    }

    private static void ScanController(ClassDeclarationSyntax type, string path, List<object> results)
    {
        var controllerName = type.Identifier.Text;
        var declaringType = QualifiedName(type);
        var controllerRoute = AttributeArgument(type.AttributeLists, "Route", 0);
        var apiVersion = AttributeArgument(type.AttributeLists, "ApiVersion", 0);
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
                var parameters = ReadParameters(method, template, notes);

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
                    RequiredScopes = ScopesFrom(authorization),
                    Responses = ReadDeclaredResponses(method.AttributeLists),
                    SampleBody = null,
                    PartialResolutionNotes = notes,
                    Summary = ReadSummary(method),
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
            var name = AttributeName(attribute);
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

            var literal = StringValue(argument);
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

    /// <summary>Binds parameters by source: route, query, header, body, form. SCAN-03.</summary>
    private static List<ScannedParameter> ReadParameters(
        MethodDeclarationSyntax method,
        string routeTemplate,
        List<string> notes)
    {
        var routeNames = RouteResolver.ParameterNames(routeTemplate);
        var parameters = new List<ScannedParameter>();

        foreach (var parameter in method.ParameterList.Parameters)
        {
            var name = parameter.Identifier.Text;
            var typeName = parameter.Type?.ToString() ?? "object";
            var explicitSource = ExplicitSource(parameter.AttributeLists);

            var source = explicitSource ?? Infer(name, typeName, routeNames);

            if (source == ParameterSource.Services)
            {
                continue;
            }

            if (source == ParameterSource.Body)
            {
                // The syntax tier cannot follow the type to shape a sample. SCAN-04 needs the
                // semantic tier; saying so is better than emitting an empty object.
                notes.Add(
                    $"The request body is a {typeName}. Load the solution to generate a sample body "
                    + "from its properties.");

                continue;
            }

            parameters.Add(new ScannedParameter(
                RouteNameFor(parameter, name),
                source,
                typeName,
                IsRequired(parameter, typeName),
                SampleValues.For(typeName)));
        }

        return parameters;
    }

    private static string RouteNameFor(ParameterSyntax parameter, string fallback) =>
        FirstStringArgumentOf(parameter.AttributeLists, "FromRoute")
        ?? FirstStringArgumentOf(parameter.AttributeLists, "FromQuery")
        ?? FirstStringArgumentOf(parameter.AttributeLists, "FromHeader")
        ?? fallback;

    private static ParameterSource? ExplicitSource(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            switch (AttributeName(attribute).Replace("Attribute", string.Empty))
            {
                case "FromRoute": return ParameterSource.Route;
                case "FromQuery": return ParameterSource.Query;
                case "FromHeader": return ParameterSource.Header;
                case "FromBody": return ParameterSource.Body;
                case "FromForm": return ParameterSource.Form;
                case "FromServices": return ParameterSource.Services;
                case "FromKeyedServices": return ParameterSource.Services;
            }
        }

        return null;
    }

    /// <summary>
    /// ASP.NET Core's default binding: a name matching a route token binds from the route, a simple
    /// type binds from the query, and a complex type binds from the body.
    /// </summary>
    private static ParameterSource Infer(string name, string typeName, IReadOnlyList<string> routeNames)
    {
        if (routeNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return ParameterSource.Route;
        }

        if (typeName is "CancellationToken" or "HttpContext" or "HttpRequest" or "HttpResponse")
        {
            return ParameterSource.Services;
        }

        return SampleValues.IsSimple(typeName) ? ParameterSource.Query : ParameterSource.Body;
    }

    private static bool IsRequired(ParameterSyntax parameter, string typeName) =>
        parameter.Default is null && !typeName.EndsWith('?');

    private static EndpointAuthorization ReadAuthorization(SyntaxList<AttributeListSyntax> lists)
    {
        var policies = new List<string>();
        var roles = new List<string>();
        var requires = false;
        var anonymous = false;

        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            switch (AttributeName(attribute).Replace("Attribute", string.Empty))
            {
                case "AllowAnonymous":
                    anonymous = true;
                    break;

                case "Authorize":
                    requires = true;

                    foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
                    {
                        var value = StringValue(argument.Expression);
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
                        if (StringValue(argument.Expression) is { } scope)
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

    /// <summary>Policy names that look like scopes are surfaced as scopes. SCAN-06.</summary>
    private static IReadOnlyList<string> ScopesFrom(EndpointAuthorization authorization) =>
        [.. authorization.Policies.Where(p => p.Contains('.', StringComparison.Ordinal) || p.Contains(':', StringComparison.Ordinal))];

    private static IReadOnlyList<DeclaredResponse> ReadDeclaredResponses(SyntaxList<AttributeListSyntax> lists)
    {
        var responses = new List<DeclaredResponse>();

        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            var name = AttributeName(attribute).Replace("Attribute", string.Empty);
            if (name is not ("ProducesResponseType" or "Produces"))
            {
                continue;
            }

            int? status = null;
            string? typeName = null;

            foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
            {
                switch (argument.Expression)
                {
                    case LiteralExpressionSyntax { Token.Value: int code }:
                        status = code;
                        break;

                    case MemberAccessExpressionSyntax member
                        when member.Expression.ToString().Contains("StatusCodes", StringComparison.Ordinal):
                        status = StatusCodeNames.Parse(member.Name.Identifier.Text);
                        break;

                    case TypeOfExpressionSyntax typeOf:
                        typeName = typeOf.Type.ToString();
                        break;
                }
            }

            // ProducesResponseType<T>(200) — the generic form.
            if (typeName is null && attribute.Name is GenericNameSyntax generic)
            {
                typeName = generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString();
            }

            if (status is { } resolved)
            {
                responses.Add(new DeclaredResponse(resolved, typeName));
            }
        }

        return responses;
    }

    private static string? ReadSummary(MethodDeclarationSyntax method)
    {
        var trivia = method.GetLeadingTrivia().ToFullString();
        var start = trivia.IndexOf("<summary>", StringComparison.Ordinal);
        var end = trivia.IndexOf("</summary>", StringComparison.Ordinal);

        if (start < 0 || end <= start)
        {
            return null;
        }

        var body = trivia[(start + "<summary>".Length)..end];
        var cleaned = string.Join(
            ' ',
            body.Split('\n')
                .Select(l => l.Trim().TrimStart('/').Trim())
                .Where(l => l.Length > 0));

        return cleaned.Length == 0 ? null : cleaned;
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

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes)
            .Any(a => AttributeName(a) == name || AttributeName(a) == name + "Attribute");

    private static string AttributeName(AttributeSyntax attribute) => attribute.Name switch
    {
        GenericNameSyntax generic => generic.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        var other => other.ToString(),
    };

    private static string? AttributeArgument(SyntaxList<AttributeListSyntax> lists, string attributeName, int index)
    {
        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            var name = AttributeName(attribute);
            if (name != attributeName && name != attributeName + "Attribute")
            {
                continue;
            }

            var arguments = attribute.ArgumentList?.Arguments;
            if (arguments is { Count: > 0 } && index < arguments.Value.Count)
            {
                return StringValue(arguments.Value[index].Expression);
            }
        }

        return null;
    }

    private static string? FirstStringArgument(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.Count > 0
            ? StringValue(attribute.ArgumentList.Arguments[0].Expression)
            : null;

    private static string? FirstStringArgumentOf(SyntaxList<AttributeListSyntax> lists, string attributeName) =>
        AttributeArgument(lists, attributeName, 0);

    /// <summary>
    /// Reads a compile-time string. Deliberately literal-only: following a constant across files
    /// is the semantic tier's job, and guessing here would produce a confidently wrong route.
    /// </summary>
    private static string? StringValue(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax { Token.Value: string value } => value,
        LiteralExpressionSyntax { Token.Value: int number } => number.ToString(),
        InterpolatedStringExpressionSyntax interpolated when interpolated.Contents.Count == 1
            && interpolated.Contents[0] is InterpolatedStringTextSyntax text => text.TextToken.ValueText,
        _ => null,
    };
}
