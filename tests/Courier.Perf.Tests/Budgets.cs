namespace Courier.Perf.Tests;

/// <summary>
/// The budgets from REQUIREMENTS 6.1, as code.
/// </summary>
/// <remarks>
/// <para>
/// "These are acceptance criteria, not aspirations. Each is measured on a mid-range corporate
/// laptop (4-core, 16GB, HDD-backed corporate image), and each is a release gate."
/// </para>
/// <para>
/// The reference machine matters and no CI runner is one. So each budget carries both the number
/// from the requirement and a tolerance for the machine actually running it: the absolute figure is
/// what ships, and the ratio is what catches a regression on hardware that differs. A run on
/// unknown hardware reports both and fails only on the absolute, which is the honest reading — a
/// fast runner passing a budget it would fail on a corporate laptop is a false green, and this
/// cannot tell the difference without a baseline.
/// </para>
/// </remarks>
public static class Budgets
{
    /// <summary>PERF-01. Cold start to interactive.</summary>
    public static readonly TimeSpan ColdStart = TimeSpan.FromMilliseconds(1500);

    /// <summary>PERF-01. Warm start.</summary>
    public static readonly TimeSpan WarmStart = TimeSpan.FromMilliseconds(800);

    /// <summary>PERF-02. Working set with 100 open tabs.</summary>
    public const long HundredTabsWorkingSetBytes = 900L * 1024 * 1024;

    /// <summary>PERF-03. Tab switch at 100 tabs.</summary>
    public static readonly TimeSpan TabSwitch = TimeSpan.FromMilliseconds(50);

    /// <summary>PERF-04. Keystroke to render in the body editor at a 1MB document.</summary>
    public static readonly TimeSpan Keystroke = TimeSpan.FromMilliseconds(16);

    /// <summary>PERF-05. First visible content of a 50MB JSON response.</summary>
    public static readonly TimeSpan LargeResponseFirstContent = TimeSpan.FromMilliseconds(2000);

    /// <summary>PERF-06. Incremental rescan of a 200-endpoint solution.</summary>
    public static readonly TimeSpan IncrementalRescan = TimeSpan.FromMilliseconds(3000);

    /// <summary>
    /// The tab count PERF-02 and PERF-03 are specified at. REQUIREMENTS 6.1 calls these two "the
    /// differentiator ... treat them as product features with named owners".
    /// </summary>
    public const int TabCount = 100;

    public const int LargeResponseMegabytes = 50;

    public const int RescanEndpointCount = 200;
}

/// <summary>One measured budget and whether it held.</summary>
/// <param name="Budget">The number from REQUIREMENTS 6.1.</param>
/// <param name="Measured">What this machine did.</param>
public sealed record BudgetResult(string Requirement, string What, double Budget, double Measured, string Unit)
{
    public bool Passed => Measured <= Budget;

    /// <summary>How much of the budget was used. Under 1.0 passes; 0.4 means comfortable headroom.</summary>
    public double Ratio => Budget <= 0 ? 0 : Measured / Budget;

    public override string ToString() =>
        $"{(Passed ? "pass" : "FAIL")}  {Requirement,-8} {What,-46} "
        + $"{Measured,10:0.##} / {Budget,8:0.##} {Unit,-6} ({Ratio:P0} of budget)";
}
