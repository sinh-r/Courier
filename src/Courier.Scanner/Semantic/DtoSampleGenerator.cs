using System.Text;
using System.Text.Json;
using Courier.Scanner.Syntax;
using Microsoft.CodeAnalysis;

namespace Courier.Scanner.Semantic;

/// <summary>
/// Walks a DTO type graph to produce a sample request body. SCAN-04.
/// </summary>
/// <remarks>
/// <para>
/// "Honouring validation attributes, enums and nullability so samples are valid by construction."
/// That phrase is doing real work: the sample has to survive the endpoint's own model validation,
/// or the first thing a new user sees from a generated collection is a 400 they did not cause.
/// </para>
/// <para>
/// Recursion is bounded and cycles are broken by emitting null for the repeated type. A self-
/// referencing DTO is common (a tree, a parent link) and must not hang the scan.
/// </para>
/// </remarks>
public sealed class DtoSampleGenerator
{
    private const int MaxDepth = 6;
    private const int SampleCollectionLength = 1;

    private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);

    /// <summary>Returns indented JSON, or null when the type has nothing bindable on it.</summary>
    public string? Generate(ITypeSymbol type)
    {
        _inProgress.Clear();

        var value = Build(type, 0);
        if (value is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    }

    private object? Build(ITypeSymbol type, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        var unwrapped = Unwrap(type);

        if (unwrapped.TypeKind == TypeKind.Enum)
        {
            // The first declared member, so the value is one the endpoint will accept.
            var member = unwrapped.GetMembers()
                .OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.HasConstantValue);

            return member is null ? (object)0 : member.Name;
        }

        if (TryScalar(unwrapped, out var scalar))
        {
            return scalar;
        }

        if (TryCollection(unwrapped, out var element))
        {
            var item = Build(element!, depth + 1);
            return item is null ? Array.Empty<object>() : Enumerable.Repeat(item, SampleCollectionLength).ToArray();
        }

        if (TryDictionary(unwrapped, out var valueType))
        {
            var item = Build(valueType!, depth + 1);
            return item is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["key"] = item };
        }

        // A cycle. Emitting null rather than recursing keeps the sample finite and honest.
        var key = unwrapped.ToDisplayString();
        if (!_inProgress.Add(key))
        {
            return null;
        }

        try
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var property in BindableProperties(unwrapped))
            {
                var constraints = ReadConstraints(property);

                // A nullable, non-required property is emitted as null rather than invented. It
                // keeps the sample minimal, which is what a user actually wants to send first.
                if (!constraints.IsRequired && IsNullable(property.Type) && depth > 0)
                {
                    properties[Name(property)] = null;
                    continue;
                }

                properties[Name(property)] = TryScalar(Unwrap(property.Type), out var value)
                    ? Constrained(property.Type, constraints) ?? value
                    : Build(property.Type, depth + 1);
            }

            return properties.Count == 0 ? null : properties;
        }
        finally
        {
            _inProgress.Remove(key);
        }
    }

    /// <summary>
    /// Public, settable, non-ignored properties. Init-only counts: a record DTO is the common shape
    /// and every one of its properties is init-only.
    /// </summary>
    private static IEnumerable<IPropertySymbol> BindableProperties(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.DeclaredAccessibility != Accessibility.Public
                    || property.IsStatic
                    || property.IsIndexer
                    || property.SetMethod is null
                    || !seen.Add(property.Name))
                {
                    continue;
                }

                if (HasAttribute(property, "JsonIgnoreAttribute"))
                {
                    continue;
                }

                yield return property;
            }
        }
    }

    /// <summary>Honours [JsonPropertyName], so the sample matches the wire shape and not the C# one.</summary>
    private static string Name(IPropertySymbol property)
    {
        var attribute = property.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name == "JsonPropertyNameAttribute");

        if (attribute?.ConstructorArguments.FirstOrDefault().Value is string explicitName)
        {
            return explicitName;
        }

        return char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
    }

    /// <summary>Reads the validation attributes that make a sample valid by construction.</summary>
    private static ValueConstraints ReadConstraints(IPropertySymbol property)
    {
        double? min = null, max = null;
        int? minLength = null, maxLength = null;
        string? pattern = null, format = null;
        var required = property.IsRequired;

        foreach (var attribute in property.GetAttributes())
        {
            var name = attribute.AttributeClass?.Name;
            var arguments = attribute.ConstructorArguments;

            switch (name)
            {
                case "RequiredAttribute":
                    required = true;
                    break;

                case "RangeAttribute" when arguments.Length >= 2:
                    min = ToDouble(arguments[0].Value);
                    max = ToDouble(arguments[1].Value);
                    break;

                case "StringLengthAttribute" when arguments.Length >= 1:
                    maxLength = arguments[0].Value as int?;
                    break;

                case "MinLengthAttribute" when arguments.Length >= 1:
                    minLength = arguments[0].Value as int?;
                    break;

                case "MaxLengthAttribute" when arguments.Length >= 1:
                    maxLength = arguments[0].Value as int?;
                    break;

                case "RegularExpressionAttribute" when arguments.Length >= 1:
                    pattern = arguments[0].Value as string;
                    break;

                case "EmailAddressAttribute":
                    format = "email";
                    break;

                case "UrlAttribute":
                    format = "url";
                    break;

                case "PhoneAttribute":
                    format = "phone";
                    break;
            }

            foreach (var named in attribute.NamedArguments)
            {
                switch (named.Key)
                {
                    case "MinimumLength":
                        minLength = named.Value.Value as int?;
                        break;
                }
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

    /// <summary>A scalar sample shaped by the property's own constraints.</summary>
    private static object? Constrained(ITypeSymbol type, ValueConstraints constraints)
    {
        var sample = SampleValues.For(Unwrap(type).Name, constraints);
        if (sample is null)
        {
            return null;
        }

        // Keep JSON types honest: a number must not be quoted in the sample body.
        return Unwrap(type).SpecialType switch
        {
            SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16
                => long.TryParse(sample, out var i) ? i : sample,
            SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal
                => double.TryParse(sample, out var d) ? d : sample,
            SpecialType.System_Boolean => sample == "true",
            _ => sample,
        };
    }

    private static bool TryScalar(ITypeSymbol type, out object? value)
    {
        value = type.SpecialType switch
        {
            SpecialType.System_String => "string",
            SpecialType.System_Boolean => true,
            SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Int16
                or SpecialType.System_Byte => 1L,
            SpecialType.System_Double or SpecialType.System_Single or SpecialType.System_Decimal => 1.0,
            SpecialType.System_Char => "a",
            SpecialType.System_DateTime => "2026-01-01T00:00:00Z",
            _ => type.Name switch
            {
                "Guid" => "00000000-0000-0000-0000-000000000000",
                "DateTimeOffset" => "2026-01-01T00:00:00Z",
                "DateOnly" => "2026-01-01",
                "TimeOnly" => "09:00:00",
                "TimeSpan" => "00:05:00",
                "Uri" => "https://example.internal/",
                _ => null,
            },
        };

        return value is not null;
    }

    private static bool TryCollection(ITypeSymbol type, out ITypeSymbol? element)
    {
        element = null;

        if (type is IArrayTypeSymbol array)
        {
            element = array.ElementType;
            return true;
        }

        if (type is not INamedTypeSymbol { IsGenericType: true } named)
        {
            return false;
        }

        if (named.TypeArguments.Length == 1
            && named.AllInterfaces.Concat([named]).Any(i => i.Name is "IEnumerable" or "ICollection" or "IList" or "IReadOnlyList"))
        {
            element = named.TypeArguments[0];
            return true;
        }

        return false;
    }

    private static bool TryDictionary(ITypeSymbol type, out ITypeSymbol? valueType)
    {
        valueType = null;

        if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 2 } named
            && named.AllInterfaces.Concat([named]).Any(i => i.Name is "IDictionary" or "IReadOnlyDictionary"))
        {
            valueType = named.TypeArguments[1];
            return true;
        }

        return false;
    }

    private static ITypeSymbol Unwrap(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    private static bool IsNullable(ITypeSymbol type) =>
        type.NullableAnnotation == NullableAnnotation.Annotated
        || type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    private static bool HasAttribute(ISymbol symbol, string attributeName) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == attributeName);

    private static double? ToDouble(object? value) => value switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        string s when double.TryParse(s, out var parsed) => parsed,
        _ => null,
    };
}
