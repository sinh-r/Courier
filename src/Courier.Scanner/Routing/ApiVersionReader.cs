using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Courier.Scanner.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Courier.Scanner.Routing;

/// <summary>
/// An API version as Asp.Versioning models it: an optional date group, an optional major.minor
/// pair, and an optional status. At least one of the group or the major version is present.
/// </summary>
public sealed record ParsedApiVersion(DateOnly? Group, int? Major, int? Minor, string? Status);

/// <summary>
/// Reads the version declared by <c>[ApiVersion(...)]</c> or <c>HasApiVersion(...)</c> and formats
/// it the way Asp.Versioning substitutes it into a URL. SCAN-02.
/// </summary>
/// <remarks>
/// <para>
/// The same version can be written half a dozen ways — <c>"1.0"</c>, <c>1.0</c>, <c>(1, 0)</c>,
/// <c>new ApiVersion(1, 0)</c> — and all of them are one version at runtime. Reading only the first
/// argument as text turned <c>(1, 5)</c> into v1 and left <c>1.0</c> unread, so every form is parsed
/// into one model first and formatted once.
/// </para>
/// <para>
/// Formatting follows the API explorer's default <c>SubstitutionFormat</c>, <c>VVV</c>: the minor
/// version only when it is not zero, then the status. <c>1.0</c> becomes <c>1</c>, <c>1.5</c> stays
/// <c>1.5</c>, <c>1.0-beta</c> becomes <c>1-beta</c>. That is the URL Swagger UI shows out of the
/// box, and the route constraint accepts it for any of the spellings above.
/// </para>
/// </remarks>
public static partial class ApiVersionReader
{
    /// <summary>The formatted version from the first <c>[ApiVersion]</c> in the lists, if it can be read.</summary>
    public static string? FromAttributes(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var attribute in lists.SelectMany(l => l.Attributes))
        {
            var name = SyntaxHelpers.AttributeName(attribute);
            if (name is not ("ApiVersion" or "ApiVersionAttribute"))
            {
                continue;
            }

            // Property setters such as Deprecated = true are not part of the version.
            var positional = (attribute.ArgumentList?.Arguments ?? default)
                .Where(a => a.NameEquals is null)
                .Select(a => a.Expression)
                .ToList();

            return Parse(positional) is { } version ? Format(version) : null;
        }

        return null;
    }

    /// <summary>The formatted version from a <c>HasApiVersion(...)</c> call's arguments, if it can be read.</summary>
    public static string? FromArguments(ArgumentListSyntax arguments) =>
        Parse([.. arguments.Arguments.Select(a => a.Expression)]) is { } version ? Format(version) : null;

    /// <summary>
    /// Parses the constructor shapes Asp.Versioning accepts: <c>(string)</c>, <c>(double)</c>,
    /// <c>(double, status)</c>, <c>(major, minor)</c>, <c>(major, minor, status)</c>, and a single
    /// <c>new ApiVersion(...)</c>. Anything else — a constant, a <c>DateOnly</c> — returns null rather
    /// than a guess.
    /// </summary>
    public static ParsedApiVersion? Parse(IReadOnlyList<ExpressionSyntax> arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        var first = arguments[0];

        if (arguments.Count == 1 && first is BaseObjectCreationExpressionSyntax creation && IsApiVersionCreation(creation))
        {
            return creation.ArgumentList is { } inner
                ? Parse([.. inner.Arguments.Select(a => a.Expression)])
                : null;
        }

        if (arguments.Count == 1 && first is LiteralExpressionSyntax { Token.Value: string text })
        {
            return ParseText(text);
        }

        if (first is not LiteralExpressionSyntax { Token.Value: { } number } || !IsNumber(number))
        {
            return null;
        }

        var rest = arguments.Skip(1).ToList();

        // (int major, int minor[, string status]) — the int overloads of ApiVersion and HasApiVersion.
        if (number is int major && rest.Count > 0 && rest[0] is LiteralExpressionSyntax { Token.Value: int minor })
        {
            return rest.Count switch
            {
                1 => new ParsedApiVersion(null, major, minor, null),
                2 when StatusOf(rest[1]) is { } status => new ParsedApiVersion(null, major, minor, status),
                _ => null,
            };
        }

        // (double version[, string status]). An int here converts to the double overload.
        if (SplitDouble(Convert.ToDouble(number, CultureInfo.InvariantCulture)) is not (int majorPart, int minorPart))
        {
            return null;
        }

        return rest.Count switch
        {
            0 => new ParsedApiVersion(null, majorPart, minorPart, null),
            1 when StatusOf(rest[0]) is { } status => new ParsedApiVersion(null, majorPart, minorPart, status),
            _ => null,
        };
    }

    /// <summary>
    /// Parses the text form: <c>1</c>, <c>1.0</c>, <c>1.0-beta</c>, <c>2015-05-01</c>,
    /// <c>2015-05-01.3.0</c>, <c>2015-05-01-rc</c>.
    /// </summary>
    public static ParsedApiVersion? ParseText(string text)
    {
        var match = VersionText().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        DateOnly? group = null;
        if (match.Groups["group"].Success)
        {
            if (!DateOnly.TryParseExact(match.Groups["group"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return null;
            }

            group = date;
        }

        int? major = match.Groups["major"].Success ? int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture) : null;
        int? minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture) : null;
        var status = match.Groups["status"].Success ? match.Groups["status"].Value : null;

        return new ParsedApiVersion(group, major, minor, status);
    }

    /// <summary>Formats with the <c>VVV</c> rule: minor only when non-zero, then the status.</summary>
    public static string Format(ParsedApiVersion version)
    {
        var sb = new StringBuilder();

        if (version.Group is { } group)
        {
            sb.Append(group.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (version.Major is { } major)
        {
            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            sb.Append(major.ToString(CultureInfo.InvariantCulture));

            if (version.Minor is { } minor and not 0)
            {
                sb.Append('.').Append(minor.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (!string.IsNullOrEmpty(version.Status))
        {
            sb.Append('-').Append(version.Status);
        }

        return sb.ToString();
    }

    private static bool IsApiVersionCreation(BaseObjectCreationExpressionSyntax creation) => creation switch
    {
        ImplicitObjectCreationExpressionSyntax => true,
        ObjectCreationExpressionSyntax { Type: var type } => type switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text == "ApiVersion",
            _ => type.ToString() == "ApiVersion",
        },
        _ => false,
    };

    private static bool IsNumber(object value) => value is int or double or float or decimal;

    private static string? StatusOf(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: string status } && status.Length > 0 ? status : null;

    /// <summary>
    /// Splits a double the way Asp.Versioning's double constructor does: the integer part is the
    /// major version, and the digits after the point, read as an integer, are the minor version.
    /// Null for anything that is not a plain non-negative decimal, such as <c>1E-05</c>.
    /// </summary>
    private static (int Major, int Minor)? SplitDouble(double value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        var point = text.IndexOf('.', StringComparison.Ordinal);
        var majorText = point < 0 ? text : text[..point];
        var minorText = point < 0 ? "0" : text[(point + 1)..];

        return int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
               && int.TryParse(minorText, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? (major, minor)
            : null;
    }

    [GeneratedRegex(@"^(?:(?<group>\d{4}-\d{2}-\d{2})(?:\.(?<major>\d+)(?:\.(?<minor>\d+))?)?|(?<major>\d+)(?:\.(?<minor>\d+))?)(?:-(?<status>[A-Za-z0-9]+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionText();
}
