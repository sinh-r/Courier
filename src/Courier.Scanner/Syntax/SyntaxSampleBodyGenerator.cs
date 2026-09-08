using System.Text.Json;

namespace Courier.Scanner.Syntax;

/// <summary>
/// Renders a sample request body from a <see cref="TypeShapeIndex"/>. SCAN-04, syntax tier: no
/// semantic model, no restore, works on a folder that does not compile.
/// </summary>
/// <remarks>
/// Mirrors <see cref="Semantic.DtoSampleGenerator"/>'s rules — nullability, validation attributes,
/// enums as their first member, cycle-breaking by emitting <c>null</c> for the repeated type — so a
/// later semantic pass upgrades the same field without changing the shape of what it produces.
/// </remarks>
internal static class SyntaxSampleBodyGenerator
{
    private const int MaxDepth = 6;

    /// <summary>Returns indented JSON, or null when the type could not be resolved or has nothing
    /// bindable on it.</summary>
    public static string? Generate(TypeShapeIndex index, string typeName)
    {
        var value = Build(index, typeName, 0, []);
        return value is null ? null : JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object? Build(TypeShapeIndex index, string typeName, int depth, HashSet<string> inProgress)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        var bare = SampleValues.Bare(typeName);

        if (TryCollectionElement(bare, out var element))
        {
            var item = Build(index, element!, depth + 1, inProgress);
            return item is null ? Array.Empty<object>() : new[] { item };
        }

        if (TryDictionaryValue(bare, out var valueType))
        {
            var item = Build(index, valueType!, depth + 1, inProgress);
            return item is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["key"] = item };
        }

        if (SampleValues.IsSimple(bare))
        {
            return ScalarValue(bare, null);
        }

        var shape = index.Resolve(bare);
        if (shape is null)
        {
            // Not a scalar and not a type this index has a shape for: an external NuGet DTO, most
            // often. The caller reports this rather than guessing at an empty object.
            return null;
        }

        if (shape.IsEnum)
        {
            // The first declared member, so the value is one the endpoint will accept.
            return shape.EnumMembers.Count > 0 ? shape.EnumMembers[0] : "0";
        }

        // A cycle — a self-referencing DTO, most often a tree or a parent link. Emitting null keeps
        // the sample finite rather than recursing until MaxDepth truncates every branch of it.
        if (!inProgress.Add(shape.Name))
        {
            return null;
        }

        try
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var member in shape.Members)
            {
                properties[member.WireName] = BuildMember(index, member, depth, inProgress);
            }

            return properties.Count == 0 ? null : properties;
        }
        finally
        {
            inProgress.Remove(shape.Name);
        }
    }

    private static object? BuildMember(TypeShapeIndex index, TypeShapeMember member, int depth, HashSet<string> inProgress)
    {
        var bare = SampleValues.Bare(member.TypeName);
        var isNullable = member.TypeName.TrimEnd().EndsWith('?');

        // A nullable, non-required property is emitted as null rather than invented. It keeps the
        // top-level sample minimal, which is what a user actually wants to send first.
        if (!member.Constraints.IsRequired && isNullable && depth > 0)
        {
            return null;
        }

        return SampleValues.IsSimple(bare)
            ? ScalarValue(bare, member.Constraints)
            : Build(index, member.TypeName, depth + 1, inProgress);
    }

    /// <summary>A scalar sample shaped by the property's own constraints. Keeps JSON types honest —
    /// a number or a boolean must not be quoted in the sample body.</summary>
    private static object? ScalarValue(string bare, ValueConstraints? constraints)
    {
        var sample = SampleValues.For(bare, constraints);
        if (sample is null)
        {
            return null;
        }

        return bare switch
        {
            "int" or "Int32" or "long" or "Int64" or "short" or "Int16" or "byte" or "Byte" =>
                long.TryParse(sample, out var i) ? i : sample,
            "double" or "Double" or "float" or "Single" or "decimal" or "Decimal" =>
                double.TryParse(sample, out var d) ? d : sample,
            "bool" or "Boolean" => sample == "true",
            _ => sample,
        };
    }

    private static readonly string[] CollectionPrefixes =
    [
        "List<", "IEnumerable<", "ICollection<", "IList<", "IReadOnlyList<", "IReadOnlyCollection<", "HashSet<", "ISet<",
    ];

    private static bool TryCollectionElement(string typeName, out string? element)
    {
        foreach (var prefix in CollectionPrefixes)
        {
            if (typeName.StartsWith(prefix, StringComparison.Ordinal) && typeName.EndsWith('>'))
            {
                element = typeName[prefix.Length..^1].Trim();
                return true;
            }
        }

        if (typeName.EndsWith("[]", StringComparison.Ordinal))
        {
            element = typeName[..^2].Trim();
            return true;
        }

        element = null;
        return false;
    }

    private static readonly string[] DictionaryPrefixes = ["Dictionary<", "IDictionary<", "IReadOnlyDictionary<"];

    private static bool TryDictionaryValue(string typeName, out string? valueType)
    {
        foreach (var prefix in DictionaryPrefixes)
        {
            if (typeName.StartsWith(prefix, StringComparison.Ordinal)
                && typeName.EndsWith('>')
                && TopLevelComma(typeName[prefix.Length..^1]) is { } comma)
            {
                valueType = typeName[prefix.Length..^1][(comma + 1)..].Trim();
                return true;
            }
        }

        valueType = null;
        return false;
    }

    /// <summary>The comma separating a dictionary's key and value type arguments, skipping any
    /// comma nested inside the value type's own generic arguments.</summary>
    private static int? TopLevelComma(string text)
    {
        var depth = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    return i;
            }
        }

        return null;
    }
}
