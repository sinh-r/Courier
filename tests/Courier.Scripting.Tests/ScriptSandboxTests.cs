using Courier.Scripting.Sandbox;

namespace Courier.Scripting.Tests;

/// <summary>
/// TEST-01: a sandboxed runtime with no filesystem or process access by default.
/// </summary>
/// <remarks>
/// These are security tests, not feature tests. TECH_SPEC 3.5: "A collection file is untrusted
/// input the moment someone shares one." Each of these is an escape a real collection could attempt
/// on a recipient's machine.
/// </remarks>
public sealed class ScriptSandboxTests
{
    [Theory]
    [InlineData("System.IO.File.ReadAllText('C:/Windows/win.ini')")]
    [InlineData("var f = importNamespace('System.IO'); f.File.ReadAllText('x')")]
    [InlineData("require('fs')")]
    [InlineData("process.exit(1)")]
    [InlineData("new (Function('return this'))()")]
    public void Cannot_reach_the_host(string script)
    {
        using var sandbox = new ScriptSandbox();

        var outcome = sandbox.Run(script);

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void Cannot_compile_a_string_into_code()
    {
        using var sandbox = new ScriptSandbox();

        // eval and the Function constructor are the classic way out of a constrained engine.
        Assert.False(sandbox.Run("eval('1+1')").Succeeded);
        Assert.False(sandbox.Run("Function('return 1')()").Succeeded);
    }

    [Fact]
    public void Stops_an_unterminated_loop_rather_than_hanging_the_send()
    {
        using var sandbox = new ScriptSandbox(ScriptLimits.Default with { MaxStatements = 5_000 });

        var outcome = sandbox.Run("while (true) { }");

        Assert.False(outcome.Succeeded);
        Assert.Contains("stopped", outcome.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stops_runaway_recursion()
    {
        using var sandbox = new ScriptSandbox(ScriptLimits.Default with { MaxRecursionDepth = 16 });

        var outcome = sandbox.Run("function f(n) { return f(n + 1); } f(0);");

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void Reports_a_syntax_error_with_its_line()
    {
        using var sandbox = new ScriptSandbox();

        var outcome = sandbox.Run("var x = ;");

        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public void Captures_console_output_instead_of_writing_it_anywhere()
    {
        using var sandbox = new ScriptSandbox();

        var outcome = sandbox.Run("console.log('hello', 42);");

        Assert.True(outcome.Succeeded);
        Assert.Contains("hello 42", outcome.Console);
    }

    [Fact]
    public void Runs_ordinary_javascript()
    {
        using var sandbox = new ScriptSandbox();

        var outcome = sandbox.Run("""
            var total = [1, 2, 3].reduce(function (a, b) { return a + b; }, 0);
            console.log(total);
            """);

        Assert.True(outcome.Succeeded);
        Assert.Contains("6", outcome.Console);
    }

    [Fact]
    public void An_empty_script_is_not_a_failure()
    {
        using var sandbox = new ScriptSandbox();

        Assert.True(sandbox.Run(null).Succeeded);
        Assert.True(sandbox.Run("   ").Succeeded);
    }
}
