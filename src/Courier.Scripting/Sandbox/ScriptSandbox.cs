using Jint;
using Jint.Runtime;

namespace Courier.Scripting.Sandbox;

/// <summary>
/// The JavaScript runtime. TEST-01: sandboxed, with no filesystem or process access by default.
/// </summary>
/// <remarks>
/// <para>
/// TECH_SPEC 3.5 puts the threat plainly: "A collection file is untrusted input the moment someone
/// shares one." A capsule or a collection folder arrives from a colleague, and its pre-request
/// script runs on the recipient's machine with the recipient's credentials in scope. So the engine
/// is constrained rather than trusted.
/// </para>
/// <para>
/// Jint over ClearScript/V8 for the reason TECH_SPEC gives: pure C#, no native dependencies, and a
/// clean single-file publish. Escalate only if profiling shows script execution is a real
/// bottleneck in collection runs, which for scripts that run once per request it will not be.
/// </para>
/// </remarks>
public sealed class ScriptSandbox : IDisposable
{
    private readonly Engine _engine;
    private bool _disposed;

    public ScriptSandbox(ScriptLimits? limits = null)
    {
        Limits = limits ?? ScriptLimits.Default;

        _engine = new Engine(options =>
        {
            options.TimeoutInterval(Limits.Timeout);
            options.LimitMemory(Limits.MemoryBytes);
            options.MaxStatements(Limits.MaxStatements);
            options.LimitRecursion(Limits.MaxRecursionDepth);
            options.Strict();

            // CLR interop stays off. Jint disables it by default and calling AllowClr() would turn
            // it on — at which point a shared collection's script can reach System.IO.File with the
            // recipient's credentials in scope, and TEST-01's sandbox is decorative.
            options.DisableStringCompilation();
        });

        RemoveHostAccess();
    }

    public ScriptLimits Limits { get; }

    /// <summary>Anything the script wrote with <c>console.log</c>, for the tests pane.</summary>
    public List<string> Console { get; } = [];

    /// <summary>
    /// pm.* members the script called that Courier does not implement. TEST-02 requires reporting
    /// these by name rather than failing silently, so the user knows exactly what to rewrite.
    /// </summary>
    public List<string> UnsupportedCalls { get; } = [];

    public Engine Engine => _engine;

    /// <summary>Exposes a host object to the script. The only way anything gets in.</summary>
    public void Expose(string name, object value) => _engine.SetValue(name, value);

    /// <summary>
    /// Runs a script. Returns a result rather than throwing: a broken script is a normal thing for
    /// a user to have, and it belongs in the tests pane, not in a crash dialog.
    /// </summary>
    public ScriptOutcome Run(string? script)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(script))
        {
            return ScriptOutcome.NotRun;
        }

        try
        {
            _engine.Execute(script);
            return new ScriptOutcome(true, null, [.. Console], [.. UnsupportedCalls]);
        }
        catch (UnsupportedPmMemberException ex)
        {
            return new ScriptOutcome(false, ex.Message, [.. Console], [.. UnsupportedCalls]);
        }
        catch (JavaScriptException ex)
        {
            return new ScriptOutcome(
                false,
                $"{ex.Message} (line {ex.Location.Start.Line})",
                [.. Console],
                [.. UnsupportedCalls]);
        }
        catch (TimeoutException)
        {
            return new ScriptOutcome(
                false,
                $"The script did not finish within {Limits.Timeout.TotalSeconds:0.#}s and was stopped.",
                [.. Console],
                [.. UnsupportedCalls]);
        }
        catch (MemoryLimitExceededException)
        {
            return new ScriptOutcome(
                false,
                $"The script exceeded its {Limits.MemoryBytes / (1024 * 1024)}MB memory limit and was stopped.",
                [.. Console],
                [.. UnsupportedCalls]);
        }
        catch (StatementsCountOverflowException)
        {
            return new ScriptOutcome(
                false,
                $"The script ran more than {Limits.MaxStatements:N0} statements and was stopped. "
                + "This usually means an unterminated loop.",
                [.. Console],
                [.. UnsupportedCalls]);
        }
        catch (RecursionDepthOverflowException)
        {
            return new ScriptOutcome(
                false,
                "The script recursed too deeply and was stopped.",
                [.. Console],
                [.. UnsupportedCalls]);
        }
    }

    /// <summary>
    /// Removes the globals that would let a script reach the host, and installs the few that are
    /// genuinely useful. Belt and braces on top of the engine options: if a future Jint adds a
    /// global that bridges out, this list is the place that notices.
    /// </summary>
    private void RemoveHostAccess()
    {
        foreach (var global in new[] { "System", "importNamespace", "clr", "host", "require", "process" })
        {
            _engine.SetValue(global, Jint.Native.JsValue.Undefined);
        }

        _engine.SetValue("console", new ConsoleShim(Console));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _engine.Dispose();
        }
    }

    /// <summary>Captures output instead of writing it anywhere. SEC-07 applies to scripts too.</summary>
    private sealed class ConsoleShim(List<string> sink)
    {
        public void log(params object?[] values) => Write(values);

        public void info(params object?[] values) => Write(values);

        public void warn(params object?[] values) => Write(values);

        public void error(params object?[] values) => Write(values);

        public void debug(params object?[] values) => Write(values);

        private void Write(object?[] values) =>
            sink.Add(string.Join(' ', values.Select(v => v?.ToString() ?? "undefined")));
    }
}

/// <summary>
/// The constraints a shared collection's script runs under. TECH_SPEC 3.5.
/// </summary>
/// <param name="Timeout">Wall clock. A script that blocks the send is worse than one that fails.</param>
public sealed record ScriptLimits(
    TimeSpan Timeout,
    long MemoryBytes,
    int MaxStatements,
    int MaxRecursionDepth)
{
    public static readonly ScriptLimits Default = new(
        TimeSpan.FromSeconds(5),
        MemoryBytes: 16 * 1024 * 1024,
        MaxStatements: 500_000,
        MaxRecursionDepth: 64);

    /// <summary>Tighter limits for a collection run, where hundreds of scripts execute in sequence.</summary>
    public static readonly ScriptLimits ForRunner = Default with
    {
        Timeout = TimeSpan.FromSeconds(2),
        MaxStatements = 100_000,
    };
}

/// <param name="Unsupported">pm.* members called but not implemented. Reported, never silent.</param>
public sealed record ScriptOutcome(
    bool Succeeded,
    string? Error,
    IReadOnlyList<string> Console,
    IReadOnlyList<string> Unsupported)
{
    public static readonly ScriptOutcome NotRun = new(true, null, [], []);
}

/// <summary>
/// Thrown when a script calls a <c>pm.*</c> member Courier does not implement.
/// </summary>
/// <remarks>
/// REQUIREMENTS 9 lists unbounded pm.* work as a risk and names the mitigation: "Publish a
/// supported surface list. Report unsupported calls by name. Never claim full compatibility."
/// Naming the member in the message is the whole of the third part.
/// </remarks>
public sealed class UnsupportedPmMemberException(string member)
    : Exception($"pm.{member} is not implemented by Courier. See the supported pm.* surface in the docs.")
{
    public string Member { get; } = member;
}
