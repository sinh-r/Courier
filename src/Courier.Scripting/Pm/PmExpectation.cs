using System.Collections;
using System.Globalization;

namespace Courier.Scripting.Pm;

/// <summary>
/// The chai-style chain behind <c>pm.expect(...)</c>. TEST-02.
/// </summary>
/// <remarks>
/// Only the members that actually appear in Postman collections in the wild. The grammar words —
/// <c>to</c>, <c>be</c>, <c>have</c>, <c>an</c> — are no-ops that return the chain, which is what
/// lets <c>pm.expect(x).to.be.an("array")</c> read as written.
/// </remarks>
public sealed class PmExpectation
{
    private readonly object? _value;
    private readonly bool _negated;

    public PmExpectation(object? value, bool negated = false)
    {
        _value = value;
        _negated = negated;
    }

    // Grammar. These carry no meaning and exist so the chain reads as English.
    public PmExpectation to => this;

    public PmExpectation be => this;

    public PmExpectation been => this;

    public PmExpectation is_ => this;

    public PmExpectation that => this;

    public PmExpectation which => this;

    public PmExpectation and => this;

    public PmExpectation has => this;

    public PmExpectation have => this;

    public PmExpectation with => this;

    public PmExpectation at => this;

    public PmExpectation of => this;

    /// <summary>Inverts the next assertion. <c>pm.expect(x).to.not.equal(y)</c>.</summary>
    public PmExpectation not => new(_value, !_negated);

    public PmExpectation a(string typeName)
    {
        Check(MatchesType(typeName), $"be a {typeName}", Describe(_value));
        return this;
    }

    public PmExpectation an(string typeName) => a(typeName);

    public void equal(object? expected) =>
        Check(AreEqual(_value, expected), $"equal {Describe(expected)}", Describe(_value));

    public void eql(object? expected) => equal(expected);

    public void equals(object? expected) => equal(expected);

    public void above(double bound) =>
        Check(AsNumber() > bound, $"be above {bound}", Describe(_value));

    public void below(double bound) =>
        Check(AsNumber() < bound, $"be below {bound}", Describe(_value));

    public void least(double bound) =>
        Check(AsNumber() >= bound, $"be at least {bound}", Describe(_value));

    public void most(double bound) =>
        Check(AsNumber() <= bound, $"be at most {bound}", Describe(_value));

    public void within(double low, double high) =>
        Check(AsNumber() >= low && AsNumber() <= high, $"be within {low} and {high}", Describe(_value));

    public void empty() =>
        Check(Length() == 0, "be empty", $"length {Length()}");

    public void @true() => Check(_value is true, "be true", Describe(_value));

    public void @false() => Check(_value is false, "be false", Describe(_value));

    public void @null() => Check(_value is null, "be null", Describe(_value));

    public void undefined() => Check(_value is null, "be undefined", Describe(_value));

    public void exist() => Check(_value is not null, "exist", "null");

    public void ok() => Check(IsTruthy(_value), "be truthy", Describe(_value));

    public void include(object? expected) =>
        Check(Includes(expected), $"include {Describe(expected)}", Describe(_value));

    public void contain(object? expected) => include(expected);

    public void match(string pattern) =>
        Check(
            _value is string s && System.Text.RegularExpressions.Regex.IsMatch(s, pattern),
            $"match /{pattern}/",
            Describe(_value));

    public void property(string name)
    {
        var present = _value is IDictionary dictionary && dictionary.Contains(name);
        Check(present, $"have property '{name}'", Describe(_value));
    }

    public PmExpectation lengthOf(double expected)
    {
        Check(Length() == (int)expected, $"have length {expected}", $"length {Length()}");
        return this;
    }

    public PmExpectation length => this;

    /// <summary>
    /// The one place a failure is raised. Everything above funnels through here so the message
    /// always reads "expected X to Y, and it was Z" — which is what the tests pane shows side by
    /// side (UI_SPEC 5.9).
    /// </summary>
    private void Check(bool condition, string expectation, string actual)
    {
        if (condition == _negated)
        {
            var negation = _negated ? "not " : string.Empty;
            throw new PmAssertionException($"expected {negation}to {expectation}, and it was {actual}");
        }
    }

    private bool MatchesType(string typeName) => typeName.ToLowerInvariant() switch
    {
        "string" => _value is string,
        "number" => _value is int or long or double or float or decimal,
        "boolean" or "bool" => _value is bool,
        "array" => _value is IEnumerable and not string and not IDictionary,
        "object" => _value is IDictionary,
        "null" => _value is null,
        "undefined" => _value is null,
        _ => false,
    };

    private double AsNumber() => _value switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        decimal m => (double)m,
        string s when double.TryParse(s, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => double.NaN,
    };

    private int Length() => _value switch
    {
        string s => s.Length,
        ICollection collection => collection.Count,
        IEnumerable enumerable => enumerable.Cast<object?>().Count(),
        _ => -1,
    };

    private bool Includes(object? expected) => _value switch
    {
        string s => s.Contains(expected?.ToString() ?? string.Empty, StringComparison.Ordinal),
        IDictionary dictionary => dictionary.Contains(expected!),
        IEnumerable enumerable => enumerable.Cast<object?>().Any(item => AreEqual(item, expected)),
        _ => false,
    };

    /// <summary>
    /// Loose equality across the numeric types Jint hands back. A script comparing a JSON number to
    /// a literal must not fail because one arrived as a long and the other as a double.
    /// </summary>
    private static bool AreEqual(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (IsNumeric(left) && IsNumeric(right))
        {
            return Math.Abs(System.Convert.ToDouble(left, CultureInfo.InvariantCulture)
                            - System.Convert.ToDouble(right, CultureInfo.InvariantCulture)) < 1e-9;
        }

        return left.Equals(right) || string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
    }

    private static bool IsNumeric(object value) => value is int or long or double or float or decimal or short or byte;

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        _ when IsNumeric(value) => System.Convert.ToDouble(value, CultureInfo.InvariantCulture) != 0,
        _ => true,
    };

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        IDictionary => "an object",
        IEnumerable e and not string => $"an array of {e.Cast<object?>().Count()}",
        _ => value.ToString() ?? "null",
    };
}
