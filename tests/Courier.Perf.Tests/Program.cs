using System.Diagnostics;
using System.Text;
using Courier.App.ViewModels;
using Courier.Core.Rendering;
using Courier.Scanner;

namespace Courier.Perf.Tests;

/// <summary>
/// The release gate. REQUIREMENTS 6.1: a budget regression fails the build.
/// </summary>
/// <remarks>
/// This is the Stage 1 gate from the build plan and TECH_SPEC 7.1's first recommended step:
/// "Spike the performance-critical path before anything else ... If the budgets are unreachable,
/// the entire product thesis needs revisiting and it is far better to learn that in week one."
///
/// Run with no arguments to measure everything and exit non-zero on any failure.
/// </remarks>
internal static class Program
{
    public static int Main(string[] args)
    {
        var only = args.FirstOrDefault();
        var results = new List<BudgetResult>();

        Console.WriteLine("Courier performance budgets — REQUIREMENTS 6.1");
        Console.WriteLine($"  machine   {Environment.ProcessorCount} logical cores, {RuntimeMemoryGb():0.#} GB visible");
        Console.WriteLine($"  runtime   {Environment.Version}");
        Console.WriteLine($"  build     {(IsOptimized() ? "Release" : "DEBUG — numbers are not meaningful")}");
        Console.WriteLine();

        // Startup first, deliberately. The JSON budgets allocate a 50MB payload and a 3.4M-node
        // index, and the tab budget holds a hundred tabs; running startup after that measures a
        // machine under GC and file-cache pressure this process created, not the one a user has.
        if (Matches(only, "startup"))
        {
            results.AddRange(StartupBudget.Measure());

            if (StartupBudget.MeasureIdleCpu() is { } idle)
            {
                results.Add(idle);
            }
        }

        if (Matches(only, "json"))
        {
            results.Add(LargeResponseBudget.Measure());
            results.Add(LargeResponseBudget.MeasureSearch());
            results.Add(LargeResponseBudget.MeasureProjection());
        }

        if (Matches(only, "tabs"))
        {
            results.Add(TabBudget.MeasureWorkingSet());
            results.Add(TabBudget.MeasureSwitch());
        }

        if (Matches(only, "scan"))
        {
            results.Add(ScanBudget.MeasureIncremental());
        }

        Console.WriteLine();
        foreach (var result in results)
        {
            Console.WriteLine("  " + result);
        }

        var failed = results.Count(r => !r.Passed);
        Console.WriteLine();
        Console.WriteLine($"{results.Count - failed}/{results.Count} budgets met");

        if (!IsOptimized())
        {
            Console.WriteLine();
            Console.WriteLine("Built in Debug. Re-run with -c Release before treating any of this as a gate.");
            return 0;
        }

        return failed == 0 ? 0 : 1;
    }

    private static bool Matches(string? only, string name) =>
        only is null || only.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static double RuntimeMemoryGb() =>
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);

    /// <summary>
    /// Debug numbers are meaningless for a budget, and reporting them as a pass would be worse
    /// than not measuring at all.
    /// </summary>
    private static bool IsOptimized() =>
        !typeof(Program).Assembly
            .GetCustomAttributes(typeof(DebuggableAttribute), false)
            .Cast<DebuggableAttribute>()
            .Any(d => d.IsJITOptimizerDisabled);
}

/// <summary>PERF-05: first visible content of a 50MB JSON response under 2s, scrolling at 60fps.</summary>
internal static class LargeResponseBudget
{
    private static byte[]? _payload;

    private static byte[] Payload => _payload ??= Generate(Budgets.LargeResponseMegabytes);

    public static BudgetResult Measure()
    {
        var payload = Payload;

        // Warm the JIT so the measurement is of the indexer, not of first-call overhead.
        _ = JsonIndex.TryBuild(payload.AsSpan(0, Math.Min(payload.Length, 1 << 20)));

        var stopwatch = Stopwatch.StartNew();
        var index = JsonIndex.TryBuild(payload);
        var projection = new JsonTreeProjection(index!);
        _ = projection.RowCount;
        stopwatch.Stop();

        Console.WriteLine(
            $"  indexed {payload.Length / (1024.0 * 1024):0.#} MB into {index!.Count:N0} nodes, "
            + $"{projection.RowCount:N0} visible rows");

        return new BudgetResult(
            "PERF-05",
            "index a 50MB JSON response and project rows",
            Budgets.LargeResponseFirstContent.TotalMilliseconds,
            stopwatch.Elapsed.TotalMilliseconds,
            "ms");
    }

    /// <summary>
    /// Search over the raw buffer. The mock shows a live match count over 40MB, which is only
    /// affordable because this never touches the node index.
    /// </summary>
    public static BudgetResult MeasureSearch()
    {
        var payload = Payload;
        _ = BodySearch.Find(payload.AsSpan(0, 1 << 20), "customerId");

        var stopwatch = Stopwatch.StartNew();
        var matches = BodySearch.Find(payload, "customerId");
        stopwatch.Stop();

        Console.WriteLine($"  found {matches.Total:N0} matches");

        // A search is a keystroke, so it is held to the editor's frame budget rather than to the
        // two seconds the initial parse gets.
        return new BudgetResult(
            "PERF-05",
            "search a 50MB response for a term",
            Budgets.Keystroke.TotalMilliseconds * 4,
            stopwatch.Elapsed.TotalMilliseconds,
            "ms");
    }

    /// <summary>
    /// Expanding a node reprojects the visible rows. At 60fps that has one frame to complete, and
    /// this is the operation a user performs most often on a large response.
    /// </summary>
    public static BudgetResult MeasureProjection()
    {
        var index = JsonIndex.TryBuild(Payload)!;
        var projection = new JsonTreeProjection(index);
        _ = projection.RowCount;

        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < 20; i++)
        {
            projection.Toggle(0);
            _ = projection.RowCount;
        }

        stopwatch.Stop();

        return new BudgetResult(
            "PERF-05",
            "expand or collapse a node (per operation)",
            Budgets.Keystroke.TotalMilliseconds,
            stopwatch.Elapsed.TotalMilliseconds / 20,
            "ms");
    }

    /// <summary>A payload shaped like a real API response: a long array of small flat objects.</summary>
    private static byte[] Generate(int megabytes)
    {
        var target = megabytes * 1024L * 1024;
        var sb = new StringBuilder((int)(target + 1024));

        sb.Append("{\"results\":[");

        for (var i = 0; sb.Length < target; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"id\":\"ORD-").Append(i.ToString("D7"))
              .Append("\",\"customerId\":\"CUS-").Append((i % 99991).ToString("D5"))
              .Append("\",\"status\":\"Pending\",\"total\":").Append(i % 5000).Append(".25")
              .Append(",\"createdUtc\":\"2026-01-01T00:00:00Z\"")
              .Append(",\"lines\":[{\"sku\":\"SKU-").Append(i % 8841).Append("\",\"qty\":").Append(i % 7 + 1).Append("}]}");
        }

        sb.Append("]}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

/// <summary>
/// PERF-02 and PERF-03. REQUIREMENTS 6.1 singles these out as the differentiator.
/// </summary>
internal static class TabBudget
{
    public static BudgetResult MeasureWorkingSet()
    {
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var tabs = Build(Budgets.TabCount);

        // Everything past the live budget is suspended, which is the mechanism the requirement
        // depends on — 100 realized editors would not fit in 900MB and never could.
        tabs.EnforceBudget();

        var after = GC.GetTotalMemory(forceFullCollection: true);
        var process = Process.GetCurrentProcess();
        process.Refresh();

        Console.WriteLine(
            $"  {tabs.Items.Count} tabs, {tabs.Items.Count(t => t.IsSuspended)} suspended, "
            + $"managed delta {(after - before) / (1024.0 * 1024):0.#} MB, "
            + $"process working set {process.WorkingSet64 / (1024.0 * 1024):0.#} MB");

        GC.KeepAlive(tabs);

        return new BudgetResult(
            "PERF-02",
            $"{Budgets.TabCount} open tabs, process working set",
            Budgets.HundredTabsWorkingSetBytes / (1024.0 * 1024),
            process.WorkingSet64 / (1024.0 * 1024),
            "MB");
    }

    public static BudgetResult MeasureSwitch()
    {
        var tabs = Build(Budgets.TabCount);
        tabs.EnforceBudget();

        // Switch to tabs that are definitely suspended, so the measurement includes the rehydrate
        // rather than just re-pointing at something already in memory.
        var targets = tabs.Items.Where(t => t.IsSuspended).Take(30).ToList();
        var stopwatch = Stopwatch.StartNew();

        foreach (var tab in targets)
        {
            tabs.Select(tab);
        }

        stopwatch.Stop();

        return new BudgetResult(
            "PERF-03",
            $"switch to a suspended tab at {Budgets.TabCount} tabs",
            Budgets.TabSwitch.TotalMilliseconds,
            stopwatch.Elapsed.TotalMilliseconds / targets.Count,
            "ms");
    }

    private static TabCollection Build(int count)
    {
        var tabs = new TabCollection(() => throw new NotSupportedException("The budget run does not open the database."));

        for (var i = 0; i < count; i++)
        {
            tabs.Items.Add(new TabViewModel(new TabState
            {
                Title = $"api/v2/orders/{i}/lines",
                Method = i % 3 == 0 ? "GET" : i % 3 == 1 ? "POST" : "PATCH",
                Url = $"{{{{baseUrl}}}}/api/v2/orders/{i}/lines",
                EnvironmentName = "QA-Internal",

                // A realistic body, because an empty tab measures nothing worth knowing.
                BodyText = SampleBody(i),
                BodyKind = Core.Collections.BodyKind.Json,
            }));
        }

        tabs.Select(tabs.Items[0]);
        return tabs;
    }

    private static string SampleBody(int seed)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"customerId\":\"CUS-").Append(seed.ToString("D5")).Append("\",\"lines\":[");

        for (var i = 0; i < 40; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"sku\":\"SKU-").Append((seed + i) % 9999).Append("\",\"qty\":").Append(i % 5 + 1).Append('}');
        }

        sb.Append("]}");
        return sb.ToString();
    }
}

/// <summary>PERF-06: incremental rescan of a 200-endpoint solution under three seconds.</summary>
internal static class ScanBudget
{
    public static BudgetResult MeasureIncremental()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"courier-perf-{Guid.NewGuid():n}");

        try
        {
            GenerateSolution(folder, Budgets.RescanEndpointCount);

            var scanner = new SolutionScanner();
            var first = scanner.Scan(folder);

            Console.WriteLine(
                $"  generated {first.Endpoints.Count} endpoints across "
                + $"{first.FileHashes.Count} files; full scan {first.Elapsed.TotalMilliseconds:0} ms");

            // Change one file, which is what a rescan on save or on build actually faces.
            var touched = Directory.EnumerateFiles(folder, "*.cs").First();
            File.AppendAllText(touched, Environment.NewLine + "// touched");

            var stopwatch = Stopwatch.StartNew();
            var second = scanner.Scan(folder, first.FileHashes, first.Endpoints);
            stopwatch.Stop();

            Console.WriteLine($"  incremental rescan found {second.Endpoints.Count} endpoints");

            return new BudgetResult(
                "PERF-06",
                $"incremental rescan of {Budgets.RescanEndpointCount} endpoints",
                Budgets.IncrementalRescan.TotalMilliseconds,
                stopwatch.Elapsed.TotalMilliseconds,
                "ms");
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static void GenerateSolution(string folder, int endpoints)
    {
        Directory.CreateDirectory(folder);

        const int PerController = 8;
        var controllers = (endpoints + PerController - 1) / PerController;

        for (var c = 0; c < controllers; c++)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using Microsoft.AspNetCore.Mvc;");
            sb.AppendLine();
            sb.AppendLine($"namespace Generated.Controllers;");
            sb.AppendLine();
            sb.AppendLine("[ApiController]");
            sb.AppendLine("[Route(\"api/v1/[controller]\")]");
            sb.AppendLine($"public sealed class Resource{c}Controller : ControllerBase");
            sb.AppendLine("{");

            for (var a = 0; a < PerController; a++)
            {
                sb.AppendLine($"    [HttpGet(\"{a}/{{id}}\")]");
                sb.AppendLine("    [ProducesResponseType(200)]");
                sb.AppendLine($"    public IActionResult Get{a}(string id, [FromQuery] int page) => Ok();");
                sb.AppendLine();
            }

            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(folder, $"Resource{c}Controller.cs"), sb.ToString());
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a budget run over.
        }
    }
}
