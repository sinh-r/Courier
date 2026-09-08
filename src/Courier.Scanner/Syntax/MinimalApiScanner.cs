using Courier.Core.Collections;
using Courier.Scanner.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Courier.Scanner.Syntax;

/// <summary>
/// Derives endpoints from minimal API registrations — <c>app.MapGet(...)</c> and friends.
/// </summary>
/// <remarks>
/// <para>
/// A scanner that only reads <c>Program.cs</c> misses how minimal APIs are actually written: real
/// codebases register endpoints inside an extension method on <c>IEndpointRouteBuilder</c>, hold
/// route groups in local variables (<c>var v1 = app.MapGroup("api/catalog").HasApiVersion(1, 0);</c>
/// then <c>v1.MapGet(...)</c> later), and pass handlers as method groups rather than inline lambdas.
/// This scans every method body in the file, not just top-level statements, and resolves a group's
/// prefix by walking back through its declaration rather than assuming it was written inline.
/// </para>
/// <para>
/// Syntax-only, same as <see cref="ControllerSyntaxScanner"/>: no restore, no build. A route that is
/// not a literal, a handler that cannot be found in the file, or a group prefix built from an
/// unrecognised expression is reported as unresolved rather than guessed at or dropped.
/// </para>
/// </remarks>
public sealed class MinimalApiScanner
{
    private static readonly (string Method, string Verb)[] VerbMethods =
    [
        ("MapGet", "GET"), ("MapPost", "POST"), ("MapPut", "PUT"), ("MapPatch", "PATCH"),
        ("MapDelete", "DELETE"), ("MapHead", "HEAD"), ("MapOptions", "OPTIONS"),
    ];

    /// <summary>Parses one file. Never throws on malformed code — that is the point of this tier.</summary>
    public IReadOnlyList<object> ScanFile(string path, string text)
    {
        var results = new List<object>();
        var tree = CSharpSyntaxTree.ParseText(text, path: path);
        var root = tree.GetCompilationUnitRoot();

        var methodsByName = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .GroupBy(m => m.Identifier.Text, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var localFunctionsByName = root.DescendantNodes()
            .OfType<LocalFunctionStatementSyntax>()
            .GroupBy(m => m.Identifier.Text, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                continue;
            }

            var methodName = member.Name.Identifier.Text;

            if (methodName == "MapMethods")
            {
                ScanMapMethods(invocation, member, path, methodsByName, localFunctionsByName, results);
                continue;
            }

            var verb = VerbMethods.FirstOrDefault(v => v.Method == methodName).Verb;
            if (verb is not null)
            {
                ScanMapVerb(invocation, member, verb, path, methodsByName, localFunctionsByName, results);
            }
        }

        return results;
    }

    private static void ScanMapVerb(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax member,
        string verb,
        string path,
        IReadOnlyDictionary<string, MethodDeclarationSyntax> methodsByName,
        IReadOnlyDictionary<string, LocalFunctionStatementSyntax> localFunctionsByName,
        List<object> results)
    {
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count < 2)
        {
            // Not a registration this scanner understands — e.g. an overload with no handler.
            return;
        }

        var declaringType = DeclaringTypeFor(invocation, path);
        var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var handlerExpr = arguments[1].Expression;
        var actionName = ActionNameFor(handlerExpr);

        var routeLiteral = SyntaxHelpers.StringValue(arguments[0].Expression);
        if (routeLiteral is null)
        {
            results.Add(new UnresolvedEndpoint(
                declaringType,
                actionName,
                path,
                line,
                $"The route template is '{arguments[0].Expression}', which is not a literal. "
                + "Load the solution so Courier can resolve the constant, or inline the string."));
            return;
        }

        var locals = BuildLocalPrefixes(invocation);
        var template = RouteResolver.JoinSegments(ResolvePrefix(member.Expression, locals), routeLiteral);

        if (!TryResolveParameters(handlerExpr, template, methodsByName, localFunctionsByName, out var parameters, out var notes, out var bodyTypeName, out var failureReason))
        {
            results.Add(new UnresolvedEndpoint(declaringType, actionName, path, line, failureReason!));
            return;
        }

        var chain = ReadFluentChain(invocation);

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
            Authorization = chain.Authorization,
            RequiredScopes = SyntaxHelpers.ScopesFrom(chain.Authorization),
            Responses = chain.Responses,
            BodyTypeName = bodyTypeName,
            PartialResolutionNotes = notes,
            Summary = chain.Summary,
        });
    }

    /// <summary><c>MapMethods("/x", ["GET", "POST"], handler)</c> — one endpoint per verb.</summary>
    private static void ScanMapMethods(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax member,
        string path,
        IReadOnlyDictionary<string, MethodDeclarationSyntax> methodsByName,
        IReadOnlyDictionary<string, LocalFunctionStatementSyntax> localFunctionsByName,
        List<object> results)
    {
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count < 3)
        {
            return;
        }

        var declaringType = DeclaringTypeFor(invocation, path);
        var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var handlerExpr = arguments[2].Expression;
        var actionName = ActionNameFor(handlerExpr);

        var routeLiteral = SyntaxHelpers.StringValue(arguments[0].Expression);
        if (routeLiteral is null)
        {
            results.Add(new UnresolvedEndpoint(
                declaringType, actionName, path, line,
                $"The route template is '{arguments[0].Expression}', which is not a literal."));
            return;
        }

        var verbs = arguments[1].Expression.DescendantNodesAndSelf()
            .OfType<LiteralExpressionSyntax>()
            .Select(l => l.Token.Value as string)
            .Where(v => v is not null)
            .Select(v => v!.ToUpperInvariant())
            .Distinct()
            .ToList();

        if (verbs.Count == 0)
        {
            results.Add(new UnresolvedEndpoint(
                declaringType, actionName, path, line,
                "The HTTP methods for MapMethods could not be read as literal strings."));
            return;
        }

        var locals = BuildLocalPrefixes(invocation);
        var template = RouteResolver.JoinSegments(ResolvePrefix(member.Expression, locals), routeLiteral);

        if (!TryResolveParameters(handlerExpr, template, methodsByName, localFunctionsByName, out var parameters, out var notes, out var bodyTypeName, out var failureReason))
        {
            results.Add(new UnresolvedEndpoint(declaringType, actionName, path, line, failureReason!));
            return;
        }

        var chain = ReadFluentChain(invocation);

        foreach (var verb in verbs)
        {
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
                Authorization = chain.Authorization,
                RequiredScopes = SyntaxHelpers.ScopesFrom(chain.Authorization),
                Responses = chain.Responses,
                BodyTypeName = bodyTypeName,
                PartialResolutionNotes = notes,
                Summary = chain.Summary,
            });
        }
    }

    /// <summary>
    /// Every local declared before this point in the containing method (or, for top-level
    /// statements, the whole file), mapped to the route prefix its initializer resolves to. A
    /// variable that is not actually a route group resolves to an empty prefix, which is harmless
    /// unless something later tries to use it as one.
    /// </summary>
    private static Dictionary<string, string> BuildLocalPrefixes(SyntaxNode node)
    {
        var locals = new Dictionary<string, string>(StringComparer.Ordinal);
        SyntaxNode scope = node.Ancestors().OfType<BlockSyntax>().FirstOrDefault()
            ?? node.SyntaxTree.GetRoot();

        foreach (var declarator in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer?.Value is not { } initializer)
            {
                continue;
            }

            locals[declarator.Identifier.Text] = ResolvePrefix(initializer, locals);
        }

        return locals;
    }

    /// <summary>
    /// Resolves the route prefix an expression contributes: a local variable's stored prefix, a
    /// <c>MapGroup("x")</c> call combined with its own receiver's prefix, or — for any other call in
    /// the chain (<c>HasApiVersion</c>, <c>WithOpenApi</c>, <c>NewVersionedApi</c>, ...) — whatever
    /// its receiver resolves to, since those calls do not change the path.
    /// </summary>
    private static string ResolvePrefix(ExpressionSyntax expression, IReadOnlyDictionary<string, string> locals)
    {
        switch (expression)
        {
            case IdentifierNameSyntax identifier:
                return locals.TryGetValue(identifier.Identifier.Text, out var prefix) ? prefix : string.Empty;

            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax innerMember } inner:
                var receiverPrefix = ResolvePrefix(innerMember.Expression, locals);

                if (innerMember.Name.Identifier.Text == "MapGroup"
                    && inner.ArgumentList.Arguments.Count > 0
                    && SyntaxHelpers.StringValue(inner.ArgumentList.Arguments[0].Expression) is { } literal)
                {
                    return RouteResolver.JoinSegments(receiverPrefix, literal);
                }

                return receiverPrefix;

            default:
                return string.Empty;
        }
    }

    private static bool TryResolveParameters(
        ExpressionSyntax handler,
        string routeTemplate,
        IReadOnlyDictionary<string, MethodDeclarationSyntax> methodsByName,
        IReadOnlyDictionary<string, LocalFunctionStatementSyntax> localFunctionsByName,
        out List<ScannedParameter> parameters,
        out List<string> notes,
        out string? bodyTypeName,
        out string? failureReason)
    {
        parameters = [];
        notes = [];
        bodyTypeName = null;
        failureReason = null;

        var parameterList = handler switch
        {
            ParenthesizedLambdaExpressionSyntax lambda => lambda.ParameterList.Parameters,
            IdentifierNameSyntax id when methodsByName.TryGetValue(id.Identifier.Text, out var m) => m.ParameterList.Parameters,
            IdentifierNameSyntax id when localFunctionsByName.TryGetValue(id.Identifier.Text, out var lf) => lf.ParameterList.Parameters,
            MemberAccessExpressionSyntax member when methodsByName.TryGetValue(member.Name.Identifier.Text, out var m2) => m2.ParameterList.Parameters,
            _ => (SeparatedSyntaxList<ParameterSyntax>?)null,
        };

        if (parameterList is null)
        {
            failureReason = $"The handler '{ActionNameFor(handler)}' could not be resolved in this file.";
            return false;
        }

        parameters = SyntaxHelpers.ReadParameters(parameterList.Value, routeTemplate, notes, out bodyTypeName);
        return true;
    }

    /// <summary>
    /// Walks the fluent chain wrapping a <c>Map*</c> call — <c>.WithTags(...)</c>,
    /// <c>.RequireAuthorization(...)</c>, <c>.Produces&lt;T&gt;(code)</c> and so on — collecting what
    /// each one declares. There is no attribute to read here; the chain is where a minimal API says
    /// what a controller action would say with <c>[Authorize]</c> or <c>[ProducesResponseType]</c>.
    /// </summary>
    private static FluentChainResult ReadFluentChain(InvocationExpressionSyntax mapCall)
    {
        var policies = new List<string>();
        var roles = new List<string>();
        var requiresAuth = false;
        var allowAnonymous = false;
        string? summary = null;
        var responses = new List<DeclaredResponse>();

        ExpressionSyntax current = mapCall;

        while (current.Parent is MemberAccessExpressionSyntax outerMember
               && ReferenceEquals(outerMember.Expression, current)
               && outerMember.Parent is InvocationExpressionSyntax outerInvocation)
        {
            var name = outerMember.Name.Identifier.Text;
            var args = outerInvocation.ArgumentList.Arguments;

            switch (name)
            {
                case "RequireAuthorization":
                    requiresAuth = true;
                    foreach (var arg in args)
                    {
                        if (SyntaxHelpers.StringValue(arg.Expression) is { } policy)
                        {
                            policies.Add(policy);
                        }
                    }

                    break;

                case "AllowAnonymous":
                    allowAnonymous = true;
                    break;

                case "WithSummary":
                    summary ??= args.Count > 0 ? SyntaxHelpers.StringValue(args[0].Expression) : null;
                    break;

                case "Produces" or "ProducesResponseType":
                    int? status = null;
                    string? typeName = null;

                    foreach (var arg in args)
                    {
                        switch (arg.Expression)
                        {
                            case LiteralExpressionSyntax { Token.Value: int code }:
                                status = code;
                                break;

                            case TypeOfExpressionSyntax typeOf:
                                typeName = typeOf.Type.ToString();
                                break;
                        }
                    }

                    if (typeName is null && outerMember.Name is GenericNameSyntax generic)
                    {
                        typeName = generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString();
                    }

                    responses.Add(new DeclaredResponse(status ?? 200, typeName));
                    break;
            }

            current = outerInvocation;
        }

        var authorization = allowAnonymous
            ? EndpointAuthorization.Anonymous
            : new EndpointAuthorization(false, requiresAuth, policies, roles);

        return new FluentChainResult(authorization, responses, summary);
    }

    private static string ActionNameFor(ExpressionSyntax handler) => handler switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        _ => "(lambda)",
    };

    private static string DeclaringTypeFor(SyntaxNode node, string path) =>
        node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
        ?? Path.GetFileNameWithoutExtension(path);

    private readonly record struct FluentChainResult(
        EndpointAuthorization Authorization,
        List<DeclaredResponse> Responses,
        string? Summary);
}
