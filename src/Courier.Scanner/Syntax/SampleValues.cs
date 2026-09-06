namespace Courier.Scanner.Syntax;

/// <summary>
/// Sample values that are valid by construction. SCAN-04.
/// </summary>
/// <remarks>
/// The requirement asks for samples that honour validation attributes, enums and nullability so
/// they are "valid by construction". The reason that matters: a generated collection whose sample
/// bodies fail validation trains the user to distrust everything the scanner produced, and the
/// first thing they do is delete it.
/// </remarks>
public static class SampleValues
{
    private static readonly HashSet<string> SimpleTypes = new(StringComparer.Ordinal)
    {
        "string", "String", "int", "Int32", "long", "Int64", "short", "Int16", "byte", "Byte",
        "bool", "Boolean", "double", "Double", "float", "Single", "decimal", "Decimal",
        "Guid", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "Uri", "char", "Char",
    };

    /// <summary>
    /// True for a type ASP.NET Core binds from the query string rather than the body. Nullable and
    /// array forms count, which is what makes <c>int[]?</c> a query parameter rather than a body.
    /// </summary>
    public static bool IsSimple(string typeName)
    {
        var bare = Bare(typeName);

        if (SimpleTypes.Contains(bare))
        {
            return true;
        }

        // A collection of simple types still binds from the query.
        foreach (var (open, close) in new[] { ("List<", ">"), ("IEnumerable<", ">"), ("ICollection<", ">"), ("IList<", ">") })
        {
            if (bare.StartsWith(open, StringComparison.Ordinal) && bare.EndsWith(close, StringComparison.Ordinal))
            {
                return IsSimple(bare[open.Length..^close.Length]);
            }
        }

        return bare.EndsWith("[]", StringComparison.Ordinal) && IsSimple(bare[..^2]);
    }

    /// <summary>A representative value for a simple type. Null when the type is not simple.</summary>
    public static string? For(string typeName, ValueConstraints? constraints = null)
    {
        var bare = Bare(typeName);

        return bare switch
        {
            "string" or "String" => Text(constraints),
            "int" or "Int32" or "long" or "Int64" or "short" or "Int16" => Number(constraints),
            "byte" or "Byte" => "1",
            "bool" or "Boolean" => "true",
            "double" or "Double" or "float" or "Single" or "decimal" or "Decimal" => Decimal(constraints),
            "Guid" => "00000000-0000-0000-0000-000000000000",
            "DateTime" or "DateTimeOffset" => "2026-01-01T00:00:00Z",
            "DateOnly" => "2026-01-01",
            "TimeOnly" => "09:00:00",
            "TimeSpan" => "00:05:00",
            "Uri" => "https://example.internal/",
            "char" or "Char" => "a",
            _ => null,
        };
    }

    /// <summary>
    /// A string sample that satisfies the constraints declared on the property. A regular
    /// expression is deliberately not solved for — the value falls back to a placeholder that names
    /// the pattern, because a wrong guess reads as a real value and a placeholder does not.
    /// </summary>
    private static string Text(ValueConstraints? constraints)
    {
        if (constraints?.AllowedValues is { Count: > 0 } allowed)
        {
            return allowed[0];
        }

        if (constraints?.RegexPattern is { } pattern)
        {
            return $"<matches {pattern}>";
        }

        var sample = constraints?.Format switch
        {
            "email" => "user@example.internal",
            "url" => "https://example.internal/",
            "phone" => "+15555550123",
            _ => "string",
        };

        var min = constraints?.MinLength ?? 0;
        if (sample.Length < min)
        {
            sample = sample.PadRight(min, 'x');
        }

        var max = constraints?.MaxLength;
        if (max is { } limit && sample.Length > limit)
        {
            sample = sample[..limit];
        }

        return sample;
    }

    private static string Number(ValueConstraints? constraints)
    {
        // Inside [Range] rather than at its edge: a boundary value is the most likely to trip an
        // exclusive comparison somewhere and produce a confusing first 400.
        if (constraints?.Minimum is { } min)
        {
            return constraints.Maximum is { } max
                ? ((long)((min + max) / 2)).ToString()
                : ((long)min + 1).ToString();
        }

        return constraints?.Maximum is { } only ? ((long)only - 1).ToString() : "1";
    }

    private static string Decimal(ValueConstraints? constraints) =>
        constraints?.Minimum is { } min
            ? (constraints.Maximum is { } max ? ((min + max) / 2).ToString("0.##") : (min + 1).ToString("0.##"))
            : "1.0";

    /// <summary>Strips nullability and namespace qualification.</summary>
    public static string Bare(string typeName)
    {
        var bare = typeName.Trim().TrimEnd('?');

        var lastDot = bare.LastIndexOf('.');
        if (lastDot >= 0 && !bare.Contains('<', StringComparison.Ordinal))
        {
            bare = bare[(lastDot + 1)..];
        }

        // Nullable<T> in its long form.
        if (bare.StartsWith("Nullable<", StringComparison.Ordinal) && bare.EndsWith('>'))
        {
            bare = bare["Nullable<".Length..^1];
        }

        return bare;
    }
}

/// <summary>
/// Validation constraints read from attributes, so a generated sample passes the endpoint's own
/// model validation. SCAN-04.
/// </summary>
public sealed record ValueConstraints
{
    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public int? MinLength { get; init; }

    public int? MaxLength { get; init; }

    public string? RegexPattern { get; init; }

    /// <summary>"email", "url", "phone" — from the corresponding data-type attributes.</summary>
    public string? Format { get; init; }

    /// <summary>Enum members, so the sample is one the endpoint will actually accept.</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    public bool IsRequired { get; init; }
}
