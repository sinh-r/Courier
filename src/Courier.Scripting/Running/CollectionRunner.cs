using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Courier.Core.Collections;
using Courier.Core.Http;
using Courier.Core.Privacy;
using Courier.Core.Variables;
using Courier.Scripting.Assertions;

namespace Courier.Scripting.Running;

/// <summary>
/// Runs a collection in order with per-request assertions, optionally over a data set. TEST-07.
/// </summary>
/// <remarks>
/// This is what STOR-06 exposes to CI, so its output has to be machine-readable and its exit
/// behaviour predictable. It keeps going after a failure by default: a run that stops at the first
/// red tells you one thing, and a run that finishes tells you whether the failure was isolated.
/// </remarks>
public sealed class CollectionRunner
{
    private readonly RequestExecutor _executor;
    private readonly VariableResolver _variables;
    private readonly ScriptRunner _scripts;
    private readonly AssertionEvaluator _assertions = new();

    public CollectionRunner(RequestExecutor executor, VariableResolver variables, ScriptRunner? scripts = null)
    {
        _executor = executor;
        _variables = variables;
        _scripts = scripts ?? new ScriptRunner(Sandbox.ScriptLimits.ForRunner);
    }

    /// <summary>Raised per request so a CLI can print progress rather than sitting silent.</summary>
    public event Action<RequestRunResult>? RequestCompleted;

    public async Task<RunReport> RunAsync(RunPlan plan, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var iterations = new List<IterationResult>();

        // No data set is one iteration with no data. Keeping the shapes identical means the report
        // and the exit code do not have two code paths.
        var dataRows = plan.Data.Count > 0
            ? plan.Data
            : [new Dictionary<string, string>(StringComparer.Ordinal)];

        for (var index = 0; index < dataRows.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            iterations.Add(await RunIterationAsync(plan, dataRows[index], index, ct).ConfigureAwait(false));

            if (plan.StopOnFailure && iterations[^1].Failed > 0)
            {
                break;
            }
        }

        return new RunReport(
            plan.CollectionName,
            plan.EnvironmentName,
            started,
            stopwatch.Elapsed,
            iterations);
    }

    private async Task<IterationResult> RunIterationAsync(
        RunPlan plan,
        IReadOnlyDictionary<string, string> data,
        int index,
        CancellationToken ct)
    {
        var results = new List<RequestRunResult>();

        // Variables written by one request's script must be visible to the next. That is the whole
        // point of running a collection rather than a set of requests.
        var context = new ScriptContext
        {
            Environment = new Dictionary<string, string>(plan.EnvironmentVariables, StringComparer.Ordinal),
            CollectionVariables = new Dictionary<string, string>(plan.CollectionVariables, StringComparer.Ordinal),
            Variables = new Dictionary<string, string>(data, StringComparer.Ordinal),
        };

        foreach (var request in plan.Requests)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunRequestAsync(request, plan, context, ct).ConfigureAwait(false));

            RequestCompleted?.Invoke(results[^1]);

            if (plan.StopOnFailure && !results[^1].Passed)
            {
                break;
            }
        }

        return new IterationResult(index, data, results);
    }

    private async Task<RequestRunResult> RunRequestAsync(
        RequestDefinition request,
        RunPlan plan,
        ScriptContext context,
        CancellationToken ct)
    {
        var scriptRuns = new List<ScriptRun>();

        var preRequest = _scripts.RunPreRequest(request.Scripts?.PreRequest, context);
        scriptRuns.Add(preRequest);

        if (!preRequest.Succeeded)
        {
            // A pre-request script that failed may have left the request half-configured. Sending
            // it anyway is how a run produces a confusing 400 instead of a clear script error.
            return new RequestRunResult(request.Name, request.Method, request.Url, null, [], scriptRuns, preRequest.Error);
        }

        var scopes = new VariableScopes
        {
            Request = context.Variables.AsReadOnly(),
            Collection = context.CollectionVariables.AsReadOnly(),
            Global = context.Environment.AsReadOnly(),
        };

        var url = await _variables.SubstituteAsync(request.Url, scopes, ct).ConfigureAwait(false);

        foreach (var (name, value) in request.PathParams)
        {
            url = url with { Text = url.Text.Replace($"{{{name}}}", value, StringComparison.Ordinal) };
        }

        if (!Uri.TryCreate(url.Text, UriKind.Absolute, out var uri))
        {
            return new RequestRunResult(
                request.Name, request.Method, url.Text, null, [], scriptRuns,
                url.Unbound.Count > 0
                    ? $"The URL still has unresolved variables: {string.Join(", ", url.Unbound)}"
                    : $"'{url.Text}' is not an absolute URL.");
        }

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var header in request.Headers.Where(h => h.Enabled))
        {
            var value = await _variables.SubstituteAsync(header.Value, scopes, ct).ConfigureAwait(false);
            headers.Add(new KeyValuePair<string, string>(header.Name, value.Text));
        }

        byte[]? body = null;
        if (request.Body?.Text is { Length: > 0 } text)
        {
            var substituted = await _variables.SubstituteAsync(text, scopes, ct).ConfigureAwait(false);
            body = Encoding.UTF8.GetBytes(substituted.Text);
        }

        var prepared = new PreparedRequest
        {
            Method = request.Method,
            Url = uri,
            Headers = headers,
            BodyBytes = body,
            ContentType = request.Body?.ResolveContentType(),
            Settings = request.Settings.InheritFrom(plan.DefaultSettings),
            EnvironmentName = plan.EnvironmentName,
            InjectTraceParent = plan.InjectTraceParent,
        };

        var result = await _executor.SendAsync(prepared, ct).ConfigureAwait(false);

        var postResponse = _scripts.RunPostResponse(request.Scripts?.PostResponse, context, result);
        scriptRuns.Add(postResponse);

        var assertions = _assertions.Evaluate(request.Assertions, result);

        return new RequestRunResult(
            request.Name,
            request.Method,
            uri.ToString(),
            result,
            assertions,
            scriptRuns,
            null);
    }
}

/// <param name="Data">Rows from a CSV or JSON data file. One iteration each. TEST-07.</param>
public sealed record RunPlan
{
    public required string CollectionName { get; init; }

    public required IReadOnlyList<RequestDefinition> Requests { get; init; }

    public string? EnvironmentName { get; init; }

    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> CollectionVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Data { get; init; } = [];

    public RequestSettings DefaultSettings { get; init; } = RequestSettings.Defaults;

    public bool InjectTraceParent { get; init; }

    /// <summary>Off by default: finishing tells you whether a failure was isolated.</summary>
    public bool StopOnFailure { get; init; }
}

public sealed record IterationResult(
    int Index,
    IReadOnlyDictionary<string, string> Data,
    IReadOnlyList<RequestRunResult> Requests)
{
    public int Passed => Requests.Count(r => r.Passed);

    public int Failed => Requests.Count(r => !r.Passed);
}

/// <param name="Error">Set when the request could not be sent at all, as opposed to failing.</param>
public sealed record RequestRunResult(
    string Name,
    string Method,
    string Url,
    ExchangeResult? Exchange,
    IReadOnlyList<AssertionResult> Assertions,
    IReadOnlyList<ScriptRun> Scripts,
    string? Error)
{
    public bool Passed =>
        Error is null
        && Exchange?.Succeeded == true
        && Assertions.All(a => a.Passed)
        && Scripts.All(s => s.Succeeded && s.Tests.All(t => t.Passed));

    public int Status => Exchange?.Response?.Status ?? 0;

    public TimeSpan Elapsed => Exchange?.Elapsed ?? TimeSpan.Zero;

    /// <summary>Every assertion, whether declarative or from a pm.test, in one list.</summary>
    public IEnumerable<(string Expression, bool Passed, string? Detail)> AllAssertions()
    {
        foreach (var assertion in Assertions)
        {
            yield return (assertion.Expression, assertion.Passed,
                assertion.Passed ? null : $"expected {assertion.Expected}, got {assertion.Actual}");
        }

        foreach (var test in Scripts.SelectMany(s => s.Tests))
        {
            yield return (test.Name, test.Passed, test.Error);
        }
    }
}

/// <summary>
/// The machine-readable report STOR-06 requires. Two formats, because CI systems disagree: JSON for
/// anything that parses it directly, JUnit XML for the test-report panel every CI tool already has.
/// </summary>
public sealed record RunReport(
    string CollectionName,
    string? EnvironmentName,
    DateTimeOffset StartedUtc,
    TimeSpan Elapsed,
    IReadOnlyList<IterationResult> Iterations)
{
    public int Total => Iterations.Sum(i => i.Requests.Count);

    public int Passed => Iterations.Sum(i => i.Passed);

    public int Failed => Iterations.Sum(i => i.Failed);

    /// <summary>Zero when everything passed. This is the CLI's exit code.</summary>
    public int ExitCode => Failed == 0 ? 0 : 1;

    public string ToJson() => JsonSerializer.Serialize(
        new
        {
            collection = CollectionName,
            environment = EnvironmentName,
            startedUtc = StartedUtc,
            elapsedMs = (long)Elapsed.TotalMilliseconds,
            total = Total,
            passed = Passed,
            failed = Failed,
            iterations = Iterations.Select(i => new
            {
                index = i.Index,
                data = i.Data,
                requests = i.Requests.Select(r => new
                {
                    name = r.Name,
                    method = r.Method,
                    url = r.Url,
                    status = r.Status,
                    elapsedMs = (long)r.Elapsed.TotalMilliseconds,
                    passed = r.Passed,
                    error = r.Error,
                    assertions = r.AllAssertions().Select(a => new
                    {
                        expression = a.Expression,
                        passed = a.Passed,
                        detail = a.Detail,
                    }),
                }),
            }),
        },
        new JsonSerializerOptions { WriteIndented = true });

    /// <summary>JUnit XML, so every CI system's existing test panel renders the run.</summary>
    public string ToJUnitXml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.Append("<testsuites name=\"").Append(Escape(CollectionName))
          .Append("\" tests=\"").Append(Total)
          .Append("\" failures=\"").Append(Failed)
          .Append("\" time=\"").Append(Seconds(Elapsed))
          .AppendLine("\">");

        foreach (var iteration in Iterations)
        {
            sb.Append("  <testsuite name=\"iteration ").Append(iteration.Index)
              .Append("\" tests=\"").Append(iteration.Requests.Count)
              .Append("\" failures=\"").Append(iteration.Failed)
              .AppendLine("\">");

            foreach (var request in iteration.Requests)
            {
                sb.Append("    <testcase classname=\"").Append(Escape(CollectionName))
                  .Append("\" name=\"").Append(Escape($"{request.Method} {request.Name}"))
                  .Append("\" time=\"").Append(Seconds(request.Elapsed))
                  .Append('"');

                if (request.Passed)
                {
                    sb.AppendLine(" />");
                    continue;
                }

                sb.AppendLine(">");

                var reason = request.Error
                    ?? string.Join(
                        "; ",
                        request.AllAssertions().Where(a => !a.Passed).Select(a => $"{a.Expression}: {a.Detail}"));

                sb.Append("      <failure message=\"").Append(Escape(reason)).AppendLine("\" />");
                sb.AppendLine("    </testcase>");
            }

            sb.AppendLine("  </testsuite>");
        }

        sb.AppendLine("</testsuites>");
        return sb.ToString();
    }

    private static string Seconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
