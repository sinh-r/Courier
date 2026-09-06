using Courier.Core.Http;
using Courier.Scripting.Pm;
using Courier.Scripting.Sandbox;

namespace Courier.Scripting;

/// <summary>
/// Runs a request's pre-request and post-response scripts. TEST-01, TEST-02.
/// </summary>
/// <remarks>
/// One sandbox per script, never shared. A collection run executes hundreds of these and a shared
/// engine would let one request's script leave state behind for the next — which is both a
/// correctness problem and, when a token is the state, a privacy one.
/// </remarks>
public sealed class ScriptRunner
{
    private readonly ScriptLimits _limits;

    public ScriptRunner(ScriptLimits? limits = null) => _limits = limits ?? ScriptLimits.Default;

    /// <summary>
    /// Runs the pre-request script. Variable writes flow back through the supplied dictionaries,
    /// which is how the "capture a token, use it in the next request" pattern works.
    /// </summary>
    public ScriptRun RunPreRequest(string? script, ScriptContext context) => Run(script, context, null);

    /// <summary>Runs the post-response script, with <c>pm.response</c> populated.</summary>
    public ScriptRun RunPostResponse(string? script, ScriptContext context, ExchangeResult result) =>
        Run(script, context, result);

    private ScriptRun Run(string? script, ScriptContext context, ExchangeResult? result)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return ScriptRun.NotRun;
        }

        using var sandbox = new ScriptSandbox(_limits);

        var pm = new PmApi(
            sandbox.Engine,
            result,
            context.Environment,
            context.Variables,
            context.Globals,
            context.CollectionVariables,
            sandbox.UnsupportedCalls);

        sandbox.Expose("pm", pm);

        // Postman's older global. Scripts in the wild still use it, and aliasing costs nothing.
        sandbox.Expose("postman", pm);

        var outcome = sandbox.Run(script);

        return new ScriptRun(
            outcome.Succeeded,
            outcome.Error,
            outcome.Console,
            outcome.Unsupported,
            [.. pm.Results]);
    }
}

/// <summary>
/// The variable scopes a script can read and write. Passed as live dictionaries on purpose: a
/// script that sets an environment variable has to affect the next request in the run.
/// </summary>
public sealed record ScriptContext
{
    public IDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IDictionary<string, string> Globals { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IDictionary<string, string> CollectionVariables { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <param name="Tests">Assertions the script declared with <c>pm.test</c>, in order.</param>
public sealed record ScriptRun(
    bool Succeeded,
    string? Error,
    IReadOnlyList<string> Console,
    IReadOnlyList<string> UnsupportedCalls,
    IReadOnlyList<PmTestResult> Tests)
{
    public static readonly ScriptRun NotRun = new(true, null, [], [], []);

    public int Passed => Tests.Count(t => t.Passed);

    public int Failed => Tests.Count(t => !t.Passed);
}
