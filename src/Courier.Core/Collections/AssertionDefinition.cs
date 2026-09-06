namespace Courier.Core.Collections;

/// <summary>
/// A declarative assertion, evaluated without scripting. TEST-03: status, header, JSONPath value,
/// JSON schema, response time.
/// </summary>
/// <param name="Source">
/// Whether the user wrote this or it was derived from the codebase's own tests. The tests pane
/// distinguishes the two, and TEST-04 requires knowing which is which.
/// </param>
public sealed record AssertionDefinition(
    AssertionKind Kind,
    string? Target = null,
    AssertionOperator Operator = AssertionOperator.Equals,
    string? Expected = null,
    AssertionSource Source = AssertionSource.Authored,
    string? Note = null)
{
    /// <summary>For the deserializer. A positional record has no parameterless constructor, and
    /// without one the format can be written but never read back.</summary>
    public AssertionDefinition()
        : this(AssertionKind.Status)
    {
    }

    /// <summary>The expression as shown in the tests pane, in monospace. UI_SPEC 5.9.</summary>
    public string Describe() => Kind switch
    {
        AssertionKind.Status => $"status {Symbol()} {Expected}",
        AssertionKind.Header => $"header[\"{Target}\"] {Symbol()} \"{Expected}\"",
        AssertionKind.JsonPath => $"{Target} {Symbol()} {Expected}",
        AssertionKind.JsonSchema => $"body matches schema {Target}",
        AssertionKind.ResponseTime => $"responseTime {Symbol()} {Expected} ms",
        AssertionKind.BodyContains => $"body contains \"{Expected}\"",
        _ => Kind.ToString(),
    };

    private string Symbol() => Operator switch
    {
        AssertionOperator.Equals => "===",
        AssertionOperator.NotEquals => "!==",
        AssertionOperator.Contains => "contains",
        AssertionOperator.StartsWith => "startsWith",
        AssertionOperator.EndsWith => "endsWith",
        AssertionOperator.Matches => "matches",
        AssertionOperator.LessThan => "<",
        AssertionOperator.GreaterThan => ">",
        AssertionOperator.Exists => "exists",
        _ => "===",
    };
}

public enum AssertionKind
{
    Status,
    Header,
    JsonPath,
    JsonSchema,
    ResponseTime,
    BodyContains,
}

public enum AssertionOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    EndsWith,
    Matches,
    LessThan,
    GreaterThan,
    Exists,
}

public enum AssertionSource
{
    /// <summary>The user wrote it. Glyph: hollow circle, matching the tree.</summary>
    Authored,

    /// <summary>Derived from an integration test in the scanned solution. TEST-04.</summary>
    GeneratedFromTests,

    /// <summary>Derived from a declared response type on the endpoint. SCAN-05.</summary>
    GeneratedFromContract,
}
