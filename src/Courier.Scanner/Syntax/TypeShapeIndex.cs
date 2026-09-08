using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Courier.Scanner.Syntax;

/// <summary>
/// Every class, record, struct and enum declared across the scanned source, reduced to what
/// <see cref="SyntaxSampleBodyGenerator"/> needs to build a sample body: its members and their
/// validation constraints. SCAN-04.
/// </summary>
/// <remarks>
/// Built from syntax only — no semantic model, no restore — so a request body type declared in a
/// different file from the endpoint that references it still resolves. That is the ordinary case:
/// DTOs live in a <c>Models</c> folder, not next to the controller.
/// </remarks>
internal sealed class TypeShapeIndex
{
    private readonly Dictionary<string, TypeShape> _byName;

    private TypeShapeIndex(Dictionary<string, TypeShape> byName) => _byName = byName;

    /// <summary>
    /// Two types sharing a simple name across different namespaces is the one case this index
    /// cannot tell apart, since it has no semantic model to resolve a fully-qualified name against
    /// a <c>using</c>. Last writer wins, which is a coin flip in that rare case and correct in the
    /// overwhelming majority where a name is unique.
    /// </summary>
    public static TypeShapeIndex Build(IEnumerable<(string Path, string Text)> files)
    {
        var byName = new Dictionary<string, TypeShape>(StringComparer.Ordinal);

        foreach (var (path, text) in files)
        {
            SyntaxNode root;

            try
            {
                root = CSharpSyntaxTree.ParseText(text, path: path).GetRoot();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                continue;
            }

            foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (ReadType(declaration) is { } shape)
                {
                    byName[shape.Name] = shape;
                }
            }

            foreach (var declaration in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                byName[declaration.Identifier.Text] = new TypeShape(
                    declaration.Identifier.Text,
                    [],
                    [.. declaration.Members.Select(m => m.Identifier.Text)]);
            }
        }

        return new TypeShapeIndex(byName);
    }

    public TypeShape? Resolve(string typeName) => _byName.GetValueOrDefault(SampleValues.Bare(typeName));

    private static TypeShape? ReadType(TypeDeclarationSyntax declaration)
    {
        var members = new List<TypeShapeMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Positional record/class parameters: `record Foo(string Bar, int Baz = 1)`.
        if (declaration.ParameterList is { } primaryConstructor)
        {
            foreach (var parameter in primaryConstructor.Parameters)
            {
                if (seen.Add(parameter.Identifier.Text))
                {
                    members.Add(ReadMember(
                        parameter.Identifier.Text,
                        parameter.Type?.ToString() ?? "object",
                        parameter.AttributeLists,
                        isRequiredKeyword: false));
                }
            }
        }

        // Auto-properties with a settable or init-only accessor: `public string Bar { get; init; }`.
        // A computed, expression-bodied property (`=>`) has no accessor list and is correctly
        // excluded — there is nothing for a caller to bind into.
        foreach (var property in declaration.Members.OfType<PropertyDeclarationSyntax>())
        {
            if (!IsBindable(property) || !seen.Add(property.Identifier.Text))
            {
                continue;
            }

            members.Add(ReadMember(
                property.Identifier.Text,
                property.Type.ToString(),
                property.AttributeLists,
                isRequiredKeyword: property.Modifiers.Any(SyntaxKind.RequiredKeyword)));
        }

        return members.Count == 0 ? null : new TypeShape(declaration.Identifier.Text, members, []);
    }

    /// <summary>Public, non-static, settable, not explicitly ignored on the wire.</summary>
    private static bool IsBindable(PropertyDeclarationSyntax property) =>
        property.Modifiers.Any(SyntaxKind.PublicKeyword)
        && !property.Modifiers.Any(SyntaxKind.StaticKeyword)
        && property.AccessorList is { } accessors
        && accessors.Accessors.Any(a => a.Kind() is SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration)
        && !SyntaxHelpers.HasAttribute(property.AttributeLists, "JsonIgnore");

    private static TypeShapeMember ReadMember(
        string name,
        string typeName,
        SyntaxList<AttributeListSyntax> attributes,
        bool isRequiredKeyword)
    {
        var wireName = SyntaxHelpers.FirstStringArgumentOf(attributes, "JsonPropertyName")
            ?? char.ToLowerInvariant(name[0]) + name[1..];

        return new TypeShapeMember(name, typeName, wireName, ReadConstraints(attributes, isRequiredKeyword));
    }

    /// <summary>Reads the validation attributes that make a generated sample valid by construction.
    /// Mirrors <see cref="Semantic.DtoSampleGenerator"/>'s reading of the same attributes from
    /// semantic <c>AttributeData</c> — this reads their syntax instead, since there is no compiled
    /// symbol here to ask.</summary>
    private static ValueConstraints ReadConstraints(SyntaxList<AttributeListSyntax> lists, bool isRequiredKeyword)
    {
        double? min = null;
        double? max = null;
        int? minLength = null;
        int? maxLength = null;
        string? pattern = null;
        string? format = null;
        var required = isRequiredKeyword || SyntaxHelpers.HasAttribute(lists, "Required");

        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            var name = SyntaxHelpers.AttributeName(attribute).Replace("Attribute", string.Empty);
            var arguments = attribute.ArgumentList?.Arguments ?? default;

            switch (name)
            {
                case "Range" when arguments.Count >= 2:
                    min = ReadNumber(arguments[0].Expression);
                    max = ReadNumber(arguments[1].Expression);
                    break;

                case "StringLength" when arguments.Count >= 1:
                    maxLength = ReadInt(arguments[0].Expression);
                    minLength = arguments
                        .Where(a => a.NameEquals?.Name.Identifier.Text == "MinimumLength")
                        .Select(a => ReadInt(a.Expression))
                        .FirstOrDefault(minLength);
                    break;

                case "MinLength" when arguments.Count >= 1:
                    minLength = ReadInt(arguments[0].Expression);
                    break;

                case "MaxLength" when arguments.Count >= 1:
                    maxLength = ReadInt(arguments[0].Expression);
                    break;

                case "RegularExpression" when arguments.Count >= 1:
                    pattern = SyntaxHelpers.StringValue(arguments[0].Expression);
                    break;

                case "EmailAddress":
                    format = "email";
                    break;

                case "Url":
                    format = "url";
                    break;

                case "Phone":
                    format = "phone";
                    break;
            }
        }

        return new ValueConstraints
        {
            Minimum = min,
            Maximum = max,
            MinLength = minLength,
            MaxLength = maxLength,
            RegexPattern = pattern,
            Format = format,
            IsRequired = required,
        };
    }

    private static double? ReadNumber(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax { Token.Value: int i } => i,
        LiteralExpressionSyntax { Token.Value: long l } => l,
        LiteralExpressionSyntax { Token.Value: double d } => d,
        LiteralExpressionSyntax { Token.Value: float f } => f,
        LiteralExpressionSyntax { Token.Value: decimal m } => (double)m,
        PrefixUnaryExpressionSyntax negated when negated.IsKind(SyntaxKind.UnaryMinusExpression) =>
            ReadNumber(negated.Operand) is { } value ? -value : null,
        _ => null,
    };

    private static int? ReadInt(ExpressionSyntax expression) => ReadNumber(expression) is { } value ? (int)value : null;
}

/// <summary>One type's shape, reduced to what a sample body needs. <see cref="EnumMembers"/> is
/// non-empty only for an enum; <see cref="Members"/> only for a class, record or struct.</summary>
internal sealed record TypeShape(string Name, IReadOnlyList<TypeShapeMember> Members, IReadOnlyList<string> EnumMembers)
{
    public bool IsEnum => EnumMembers.Count > 0;
}

/// <param name="TypeName">As written in source — may be an unresolved generic, a nullable marker, or
/// a type this index has no shape for (a NuGet DTO, most often).</param>
/// <param name="WireName">The JSON property name: <c>[JsonPropertyName]</c> when present, else the
/// camel-cased C# name, matching System.Text.Json's default naming.</param>
internal sealed record TypeShapeMember(string Name, string TypeName, string WireName, ValueConstraints Constraints);
