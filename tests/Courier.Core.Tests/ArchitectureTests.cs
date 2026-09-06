using System.Text.RegularExpressions;

namespace Courier.Core.Tests;

/// <summary>
/// The tests that keep the privacy claim and the layering honest.
/// </summary>
/// <remarks>
/// These are source-scanning rather than reflection-based on purpose: they must fail at build time
/// on the line that introduced the problem, and they must catch a handler constructed inside a
/// method that no test ever calls. If one of these fires, fix the call site — the test is the only
/// thing standing between SEC-01 and a well-meaning refactor.
/// </remarks>
public sealed class ArchitectureTests
{
    /// <summary>The one file allowed to construct an HTTP handler. SEC-01, SEC-02.</summary>
    private const string ChokepointFile = "EgressGate.cs";

    private static readonly string[] HandlerConstructions =
    [
        "new HttpClient(",
        "new HttpClient ",
        "new SocketsHttpHandler",
        "new HttpClientHandler",
        "new WinHttpHandler",
        "new HttpMessageInvoker",
        "new TcpClient",
        "new Socket(",
        "new ClientWebSocket",
    ];

    [Fact]
    public void Only_the_egress_gate_constructs_http_handlers()
    {
        var offenders = new List<string>();

        foreach (var file in ProductionSources())
        {
            if (Path.GetFileName(file) == ChokepointFile)
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var lines = text.Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Comments and doc comments name these types constantly; only code counts.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith('*'))
                {
                    continue;
                }

                // String literals too. The code exporters emit "new HttpClient()" as text for the
                // user's generated C# (CORE-10), which is not this process opening a socket. An
                // exemption for those files would blind the test to a real one alongside it, so the
                // literals come out and the rest of the line is still checked.
                var code = WithoutStringLiterals(line);

                foreach (var construction in HandlerConstructions)
                {
                    if (code.Contains(construction, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Relative(file)}:{i + 1}  {trimmed}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Every outbound socket must be opened by Courier.Core.Privacy.EgressGate, so that SEC-01 "
            + "(no connection the user did not ask for) and SEC-06 (an auditable statement of network "
            + "behaviour) stay true. These call sites bypass it:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Core_does_not_reference_ui_or_windows_only_packages()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoLayout.Src, "Courier.Core", "Courier.Core.csproj"));

        Assert.DoesNotContain("Avalonia", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("net10.0-windows", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Courier.Platform.Windows", csproj, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Core_sources_use_no_windows_only_namespaces()
    {
        string[] forbidden =
        [
            "using Microsoft.Win32",
            "using System.Windows",
            "using Avalonia",
            "System.Security.Cryptography.ProtectedData",
        ];

        var offenders = new List<string>();
        var coreDirectory = Path.Combine(RepoLayout.Src, "Courier.Core");

        foreach (var file in Directory.EnumerateFiles(coreDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (IsGenerated(file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var namespaceName in forbidden)
            {
                if (text.Contains(namespaceName, StringComparison.Ordinal))
                {
                    offenders.Add($"{Relative(file)} uses {namespaceName}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Courier.Core must build and run on Linux so a contributor without Windows can work on "
            + "the scanner and the CLI can exist at all. Move this behind an interface in "
            + "Courier.Core.Abstractions:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void No_source_writes_a_secret_to_a_file_path()
    {
        // A crude but effective guard against the specific mistake STOR-04 forbids: persisting a
        // credential-store read straight into a file Courier writes.
        var offenders = new List<string>();
        var pattern = new Regex(
            @"File\.(WriteAll\w+|AppendAll\w+)\s*\([^)]*\b(secret|password|token|credential)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var file in ProductionSources())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in pattern.Matches(text))
            {
                offenders.Add($"{Relative(file)}  {match.Value.Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "STOR-04 and P2: no file Courier writes may contain a secret value. Route this through "
            + "ISecretStore:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Blanks the contents of double-quoted strings so a literal mentioning a handler type is not
    /// mistaken for constructing one. Deliberately simple: it only has to be right about ordinary
    /// single-line literals, and anything it gets wrong fails toward reporting rather than hiding.
    /// </summary>
    private static string WithoutStringLiterals(string line)
    {
        var result = new System.Text.StringBuilder(line.Length);
        var inLiteral = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '\\' && inLiteral && i + 1 < line.Length)
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                inLiteral = !inLiteral;
                continue;
            }

            if (!inLiteral)
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static IEnumerable<string> ProductionSources() =>
        Directory.EnumerateFiles(RepoLayout.Src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsGenerated(f));

    private static bool IsGenerated(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.EndsWith(".g.cs", StringComparison.Ordinal)
        || path.EndsWith(".Designer.cs", StringComparison.Ordinal);

    private static string Relative(string path) => Path.GetRelativePath(RepoLayout.Root, path);
}
