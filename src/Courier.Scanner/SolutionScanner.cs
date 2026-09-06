using System.Diagnostics;
using Courier.Scanner.Diffing;
using Courier.Scanner.Syntax;

namespace Courier.Scanner;

/// <summary>
/// Scans a folder or a <c>.sln</c> and derives endpoints. SCAN-01, SCAN-08, SCAN-10.
/// </summary>
/// <remarks>
/// Syntax first, always. The semantic tier is an upgrade the caller opts into, and when it fails
/// the syntax result is still returned with a reason attached — a scan that produces 47 endpoints
/// without bodies beats a scan that produces an error because the solution does not restore.
/// </remarks>
public sealed class SolutionScanner
{
    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private static readonly string[] SkippedFolders =
    [
        "bin", "obj", "node_modules", ".git", ".vs", "packages", "TestResults", "artifacts",
    ];

    private readonly ControllerSyntaxScanner _syntax = new();

    /// <param name="previousHashes">
    /// From the last scan. Supplying them makes this incremental: only changed files are parsed,
    /// which is how PERF-06 gets a 200-endpoint rescan under three seconds.
    /// </param>
    public ScanResult Scan(
        string path,
        IReadOnlyDictionary<string, string>? previousHashes = null,
        IReadOnlyList<ScannedEndpoint>? previousEndpoints = null,
        CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var root = ResolveRoot(path);

        var files = EnumerateSourceFiles(root).ToList();
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            hashes[file] = ContentHash.OfFile(file);
        }

        var endpoints = new List<ScannedEndpoint>();
        var unresolved = new List<UnresolvedEndpoint>();

        IReadOnlyList<string> toParse = files;
        if (previousHashes is not null && previousEndpoints is not null)
        {
            var (changed, deleted) = ContentHash.Changes(previousHashes, hashes);
            var untouched = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            untouched.ExceptWith(changed);

            // Carry forward everything from files that did not change.
            endpoints.AddRange(previousEndpoints.Where(
                e => untouched.Contains(e.SourceFile) && !deleted.Contains(e.SourceFile)));

            toParse = changed;
        }

        foreach (var file in toParse)
        {
            ct.ThrowIfCancellationRequested();

            string text;

            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException ex)
            {
                unresolved.Add(new UnresolvedEndpoint(
                    Path.GetFileNameWithoutExtension(file),
                    string.Empty,
                    file,
                    0,
                    $"The file could not be read: {ex.Message}"));

                continue;
            }

            foreach (var result in _syntax.ScanFile(file, text))
            {
                switch (result)
                {
                    case ScannedEndpoint endpoint:
                        endpoints.Add(endpoint);
                        break;

                    case UnresolvedEndpoint unresolvedEndpoint:
                        unresolved.Add(unresolvedEndpoint);
                        break;
                }
            }
        }

        return new ScanResult
        {
            Endpoints = Deduplicate(endpoints),
            Unresolved = unresolved,
            Environments = EnvironmentReader.Read(root),
            Tier = ScanTier.Syntax,
            FileHashes = hashes,
            Elapsed = Stopwatch.GetElapsedTime(started),
        };
    }

    /// <summary>
    /// Two endpoints with the same identity are the same endpoint. This happens legitimately —
    /// a partial class, or a source generator emitting alongside handwritten code — and keeping
    /// both would produce a duplicate row in the tree that the user cannot tell apart.
    /// </summary>
    private static IReadOnlyList<ScannedEndpoint> Deduplicate(List<ScannedEndpoint> endpoints)
    {
        var byId = new Dictionary<string, ScannedEndpoint>(StringComparer.Ordinal);

        foreach (var endpoint in endpoints)
        {
            // Prefer the one that resolved more: a sample body beats none.
            if (!byId.TryGetValue(endpoint.Id, out var existing)
                || (existing.SampleBody is null && endpoint.SampleBody is not null))
            {
                byId[endpoint.Id] = endpoint;
            }
        }

        return [.. byId.Values.OrderBy(e => e.RouteTemplate, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal)];
    }

    private static string ResolveRoot(string path)
    {
        if (Directory.Exists(path))
        {
            return path;
        }

        if (File.Exists(path))
        {
            return Path.GetDirectoryName(path) ?? path;
        }

        throw new DirectoryNotFoundException($"'{path}' is neither a folder nor a file.");
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", Enumeration).Where(IsInteresting);

    /// <summary>
    /// Skips build output and generated files. Parsing <c>obj</c> is how a scan finds the same
    /// controller three times and takes ten seconds doing it.
    /// </summary>
    private static bool IsInteresting(string path)
    {
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (SkippedFolders.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var name = Path.GetFileName(path);
        return !name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
    }
}
