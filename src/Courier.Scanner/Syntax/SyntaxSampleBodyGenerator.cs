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
    /// <param name="context">
    /// The namespace of whatever declared <paramref name="typeName"/> — the controller or minimal
    /// API handler's own namespace. Passed straight to <see cref="TypeShapeIndex.Resolve"/>, which
    /// is what tells apart e.g. <c>Articles/Create.cs</c>'s <c>Create.Command</c> from an unrelated
    /// feature's own nested type of the same name.
    /// </param>
    public static string? Generate(TypeShapeIndex index, string typeName, string? context = null)
    {
        var value = Build(index, typeName, 0, [], context);
        return value is null ? null : JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    }

    private static object? Build(TypeShapeIndex index, string typeName, int depth, HashSet<string> inProgress, string? context)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        var bare = SampleValues.Bare(typeName);

        if (TryCollectionElement(bare, out var element))
        {
            var item = Build(index, element!, depth + 1, inProgress, context);
            return item is null ? Array.Empty<object>() : new[] { item };
        }

        if (TryDictionaryValue(bare, out var valueType))
        {
            var item = Build(index, valueType!, depth + 1, inProgress, context);
            return item is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["key"] = item };
        }

        if (SampleValues.IsSimple(bare))
        {
            return ScalarValue(bare, null);
        }

        // The original typeName, not the pre-bared form: Resolve needs the dots intact to try an
        // exact namespace/nesting match before it falls back to the ambiguous bare simple name.
        var shape = index.Resolve(typeName, context);
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
                properties[member.WireName] = BuildMember(index, member, depth, inProgress, context);
            }

            return properties.Count == 0 ? null : properties;
        }
        finally
        {
            inProgress.Remove(shape.Name);
        }
    }

    private static object? BuildMember(TypeShapeIndex index, TypeShapeMember member, int depth, HashSet<string> inProgress, string? context)
    {
        var bare = SampleValues.Bare(member.TypeName);

        // Every property gets a real value, nested or not, required or not: a body *example* has
        // to show what to send. Nulling out anything merely nullable-with-no-[Required] used to
        // gut nested DTOs wholesale for the (extremely common) case of a team validating elsewhere
        // — FluentValidation, a pipeline behavior — rather than with data-annotation attributes,
        // since plain nullable-reference-type properties then carry no IsRequired signal at all.
        // A genuine cycle (a self-referencing DTO) still comes out null on its own, via the
        // inProgress guard in Build below — this was never what actually prevented infinite
        // recursion for that case.
        //
        // IsSimple also answers true for an array/list of a simple type (string[], List<int>, ...)
        // — correct for its original purpose (ASP.NET Core binds that from the query string, not
        // the body), but ScalarValue/SampleValues.For has no case for "string[]" and would return
        // null for it. Only take the constraint-aware scalar path for an actual scalar; anything
        // collection- or dictionary-shaped goes through Build, which already knows how to sample one.
        if (SampleValues.IsSimple(bare) && !TryCollectionElement(bare, out _) && !TryDictionaryValue(bare, out _))
        {
            return ScalarValue(bare, member.Constraints);
        }

        return Build(index, member.TypeName, depth + 1, inProgress, context);
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
