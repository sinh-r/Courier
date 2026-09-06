using Courier.Core.Collections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Courier.Scanner.Syntax;

/// <summary>
/// Syntax-tree primitives shared by <see cref="ControllerSyntaxScanner"/> and
/// <see cref="MinimalApiScanner"/>: reading a compile-time string, binding a parameter list by
/// source, and reading declared response types. Attribute-routed controllers and minimal APIs both
/// end up with a route template, a parameter list and a handler, and once you have those three
/// things the rest of SCAN-03/SCAN-05/SCAN-06 does not care which style produced them.
/// </summary>
internal static class SyntaxHelpers
{
    /// <summary>Binds parameters by source: route, query, header, body, form. SCAN-03.</summary>
    public static List<ScannedParameter> ReadParameters(
        SeparatedSyntaxList<ParameterSyntax> parameterList,
        string routeTemplate,
        List<string> notes)
    {
        var routeNames = Routing.RouteResolver.ParameterNames(routeTemplate);
        var parameters = new List<ScannedParameter>();

        foreach (var parameter in parameterList)
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

    public static ParameterSource? ExplicitSource(SyntaxList<AttributeListSyntax> lists)
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
    public static ParameterSource Infer(string name, string typeName, IReadOnlyList<string> routeNames)
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

    public static bool IsRequired(ParameterSyntax parameter, string typeName) =>
        parameter.Default is null && !typeName.EndsWith('?');

    /// <summary>Policy names that look like scopes are surfaced as scopes. SCAN-06.</summary>
    public static IReadOnlyList<string> ScopesFrom(EndpointAuthorization authorization) =>
        [.. authorization.Policies.Where(p => p.Contains('.', StringComparison.Ordinal) || p.Contains(':', StringComparison.Ordinal))];

    public static IReadOnlyList<DeclaredResponse> ReadDeclaredResponses(SyntaxList<AttributeListSyntax> lists)
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

    /// <summary>Reads the leading <c>&lt;summary&gt;</c> XML doc comment on any node that has one.</summary>
    public static string? ReadSummary(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia().ToFullString();
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

    public static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes)
            .Any(a => AttributeName(a) == name || AttributeName(a) == name + "Attribute");

    public static string AttributeName(AttributeSyntax attribute) => attribute.Name switch
    {
        GenericNameSyntax generic => generic.Identifier.Text,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
        var other => other.ToString(),
    };

    public static string? AttributeArgument(SyntaxList<AttributeListSyntax> lists, string attributeName, int index)
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

    public static string? FirstStringArgumentOf(SyntaxList<AttributeListSyntax> lists, string attributeName) =>
        AttributeArgument(lists, attributeName, 0);

    /// <summary>
    /// Reads a compile-time string. Deliberately literal-only: following a constant across files
    /// is the semantic tier's job, and guessing here would produce a confidently wrong route.
    /// </summary>
    public static string? StringValue(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax { Token.Value: string value } => value,
        LiteralExpressionSyntax { Token.Value: int number } => number.ToString(),
        InterpolatedStringExpressionSyntax interpolated when interpolated.Contents.Count == 1
            && interpolated.Contents[0] is InterpolatedStringTextSyntax text => text.TextToken.ValueText,
        _ => null,
    };
}
