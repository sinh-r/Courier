using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Courier.Core.Collections;
using Courier.Core.Http;
using Json.Path;
using Json.Schema;

namespace Courier.Scripting.Assertions;

/// <summary>
/// Evaluates declarative assertions without scripting. TEST-03: status, header, JSONPath value,
/// JSON schema, response time.
/// </summary>
/// <remarks>
/// The point of the declarative path is that most assertions are one of five shapes, and writing
/// JavaScript for "the status should be 200" is friction that stops people asserting anything at
/// all. Results carry expected and actual separately because UI_SPEC 5.9 shows them side by side.
/// </remarks>
public sealed class AssertionEvaluator
{
    public IReadOnlyList<AssertionResult> Evaluate(
        IReadOnlyList<AssertionDefinition> assertions,
        ExchangeResult result)
    {
        if (assertions.Count == 0)
        {
            return [];
        }

        // Parsed once, not per assertion: a collection run with ten JSONPath assertions on a large
        // body should not pay for ten parses.
        var body = LazyBody(result);
        var results = new List<AssertionResult>(assertions.Count);

        foreach (var assertion in assertions)
        {
            results.Add(Evaluate(assertion, result, body));
        }

        return results;
    }

    private AssertionResult Evaluate(
        AssertionDefinition assertion,
        ExchangeResult result,
        Lazy<JsonNode?> body)
    {
        try
        {
            return assertion.Kind switch
            {
                AssertionKind.Status => EvaluateStatus(assertion, result),
                AssertionKind.Header => EvaluateHeader(assertion, result),
                AssertionKind.ResponseTime => EvaluateResponseTime(assertion, result),
                AssertionKind.BodyContains => EvaluateBodyContains(assertion, result),
                AssertionKind.JsonPath => EvaluateJsonPath(assertion, body),
                AssertionKind.JsonSchema => EvaluateJsonSchema(assertion, body),
                _ => Fail(assertion, "unsupported assertion", assertion.Kind.ToString()),
            };
        }
        catch (Exception ex) when (ex is JsonException or PathParseException or ArgumentException)
        {
            // A malformed assertion is the user's to fix, and the message has to say what is wrong
            // with it rather than reporting a generic failure.
            return Fail(assertion, assertion.Expected ?? string.Empty, ex.Message);
        }
    }

    private static AssertionResult EvaluateStatus(AssertionDefinition assertion, ExchangeResult result)
    {
        var actual = result.Response?.Status ?? 0;
        var expected = assertion.Expected ?? "200";

        // "2xx" is a legitimate expectation and more useful than pinning 201 vs 200.
        if (expected.EndsWith("xx", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(expected[..1], out var family))
        {
            return Result(assertion, actual / 100 == family, expected, actual.ToString());
        }

        return Result(
            assertion,
            int.TryParse(expected, out var code) && Compare(actual, code, assertion.Operator),
            expected,
            actual.ToString());
    }

    private static AssertionResult EvaluateHeader(AssertionDefinition assertion, ExchangeResult result)
    {
        var actual = result.Response?.Headers
            .FirstOrDefault(h => string.Equals(h.Key, assertion.Target, StringComparison.OrdinalIgnoreCase))
            .Value;

        if (assertion.Operator == AssertionOperator.Exists)
        {
            return Result(assertion, actual is not null, "present", actual is null ? "absent" : "present");
        }

        return Result(
            assertion,
            actual is not null && CompareText(actual, assertion.Expected ?? string.Empty, assertion.Operator),
            assertion.Expected ?? string.Empty,
            actual ?? "(absent)");
    }

    private static AssertionResult EvaluateResponseTime(AssertionDefinition assertion, ExchangeResult result)
    {
        var actual = result.Elapsed.TotalMilliseconds;
        var expected = double.TryParse(assertion.Expected, out var ms) ? ms : 0;

        var passed = assertion.Operator switch
        {
            AssertionOperator.GreaterThan => actual > expected,
            AssertionOperator.Equals => Math.Abs(actual - expected) < 1,
            _ => actual < expected,
        };

        return Result(assertion, passed, $"{expected:0} ms", $"{actual:0} ms");
    }

    private static AssertionResult EvaluateBodyContains(AssertionDefinition assertion, ExchangeResult result)
    {
        var text = result.Response?.Body is { } bytes ? Encoding.UTF8.GetString(bytes) : string.Empty;
        var expected = assertion.Expected ?? string.Empty;

        return Result(
            assertion,
            text.Contains(expected, StringComparison.Ordinal),
            expected,
            text.Length > 60 ? $"{text[..60]}…" : text);
    }

    private static AssertionResult EvaluateJsonPath(AssertionDefinition assertion, Lazy<JsonNode?> body)
    {
        if (body.Value is null)
        {
            return Fail(assertion, assertion.Expected ?? string.Empty, "the body is not JSON");
        }

        var path = JsonPath.Parse(assertion.Target ?? "$");
        var matches = path.Evaluate(body.Value).Matches;

        if (matches.Count == 0)
        {
            return assertion.Operator == AssertionOperator.Exists
                ? Result(assertion, false, "present", "no match")
                : Fail(assertion, assertion.Expected ?? string.Empty, $"{assertion.Target} matched nothing");
        }

        var actual = matches[0].Value;

        if (assertion.Operator == AssertionOperator.Exists)
        {
            return Result(assertion, true, "present", Render(actual));
        }

        return Result(
            assertion,
            CompareJson(actual, assertion.Expected, assertion.Operator),
            assertion.Expected ?? string.Empty,
            Render(actual));
    }

    private static AssertionResult EvaluateJsonSchema(AssertionDefinition assertion, Lazy<JsonNode?> body)
    {
        if (body.Value is null)
        {
            return Fail(assertion, "valid against the schema", "the body is not JSON");
        }

        var schema = JsonSchema.FromText(assertion.Expected ?? assertion.Target ?? "{}");

        // JsonSchema.Net evaluates a JsonElement, so the parsed node is projected back through one.
        using var document = JsonDocument.Parse(body.Value.ToJsonString());
        var evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (evaluation.IsValid)
        {
            return Result(assertion, true, "valid against the schema", "valid");
        }

        // Name the first failing location. A wall of schema errors is unreadable in a 24px row.
        var first = (evaluation.Details ?? [])
            .FirstOrDefault(d => d is { IsValid: false, Errors.Count: > 0 });

        var detail = first?.Errors?.Values.FirstOrDefault() ?? "did not match";
        var where = first?.InstanceLocation.ToString();

        return Result(
            assertion,
            false,
            "valid against the schema",
            string.IsNullOrEmpty(where) ? detail : $"{where}: {detail}");
    }

    private static bool Compare(int actual, int expected, AssertionOperator op) => op switch
    {
        AssertionOperator.NotEquals => actual != expected,
        AssertionOperator.LessThan => actual < expected,
        AssertionOperator.GreaterThan => actual > expected,
        _ => actual == expected,
    };

    private static bool CompareText(string actual, string expected, AssertionOperator op) => op switch
    {
        AssertionOperator.NotEquals => !string.Equals(actual, expected, StringComparison.Ordinal),
        AssertionOperator.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
        AssertionOperator.StartsWith => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
        AssertionOperator.EndsWith => actual.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
        AssertionOperator.Matches => System.Text.RegularExpressions.Regex.IsMatch(actual, expected),
        _ => string.Equals(actual, expected, StringComparison.Ordinal),
    };

    /// <summary>
    /// Compares a JSON value to the expected text. Numbers compare numerically so that
    /// <c>190.20</c> and <c>190.2</c> agree — the mock's failing assertion is exactly this shape.
    /// </summary>
    private static bool CompareJson(JsonNode? actual, string? expected, AssertionOperator op)
    {
        var rendered = Render(actual);

        if (actual is JsonValue value
            && value.TryGetValue<double>(out var number)
            && double.TryParse(expected, out var expectedNumber))
        {
            return op switch
            {
                AssertionOperator.NotEquals => Math.Abs(number - expectedNumber) > 1e-9,
                AssertionOperator.LessThan => number < expectedNumber,
                AssertionOperator.GreaterThan => number > expectedNumber,
                _ => Math.Abs(number - expectedNumber) < 1e-9,
            };
        }

        return CompareText(rendered, expected ?? string.Empty, op);
    }

    private static string Render(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonArray array => $"[{array.Count}]",
        JsonObject obj => $"{{{obj.Count}}}",
        _ => node.ToJsonString(),
    };

    private static Lazy<JsonNode?> LazyBody(ExchangeResult result) => new(() =>
    {
        if (result.Response?.Body is not { Length: > 0 } bytes)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    });

    private static AssertionResult Result(AssertionDefinition assertion, bool passed, string expected, string actual) =>
        new(assertion, passed, expected, actual);

    private static AssertionResult Fail(AssertionDefinition assertion, string expected, string actual) =>
        new(assertion, false, expected, actual);
}

/// <param name="Expected">Shown in the tests pane beside <paramref name="Actual"/>. UI_SPEC 5.9.</param>
public sealed record AssertionResult(
    AssertionDefinition Assertion,
    bool Passed,
    string Expected,
    string Actual)
{
    /// <summary>The expression as the pane renders it, in monospace.</summary>
    public string Expression => Assertion.Describe();

    /// <summary>Whether the user wrote this or it came from the codebase. TEST-04, UI_SPEC 5.9.</summary>
    public AssertionSource Source => Assertion.Source;
}
