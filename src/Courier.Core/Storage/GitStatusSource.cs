using System.Diagnostics;
using System.Text;
using Courier.Core.Abstractions;

namespace Courier.Core.Storage;

/// <summary>
/// Inline git status in the collection tree, by shelling out to the git the user already has.
/// STOR-05.
/// </summary>
/// <remarks>
/// <para>
/// STOR-05 says "without embedding a git client", so there is no LibGit2Sharp here. Shelling out is
/// also more honest about the result: it reports exactly what the user would see at their own
/// prompt, including whatever their gitignore, submodules and worktree configuration do.
/// </para>
/// <para>
/// Results are cached and the caller is expected to debounce. PERF-01 forbids running this at
/// startup at all — a git status on a cold HDD-backed corporate image is not free.
/// </para>
/// </remarks>
public sealed class GitStatusSource : IGitStatusSource
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, bool> _isRepository = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<bool> IsRepositoryAsync(string folder, CancellationToken ct = default)
    {
        if (_isRepository.TryGetValue(folder, out var cached))
        {
            return cached;
        }

        var result = await RunAsync(folder, ["rev-parse", "--is-inside-work-tree"], ct).ConfigureAwait(false);
        var inside = result is { ExitCode: 0 } && result.Output.Trim() == "true";

        _isRepository[folder] = inside;
        return inside;
    }

    public async ValueTask<IReadOnlyDictionary<string, GitFileState>> GetStatusAsync(
        string folder,
        CancellationToken ct = default)
    {
        if (!await IsRepositoryAsync(folder, ct).ConfigureAwait(false))
        {
            return new Dictionary<string, GitFileState>(StringComparer.OrdinalIgnoreCase);
        }

        // porcelain=v2 is the stable machine-readable form; -z avoids quoting surprises in paths
        // with spaces or non-ASCII, which corporate repositories are full of.
        var result = await RunAsync(
            folder,
            ["status", "--porcelain=v2", "--untracked-files=all", "-z"],
            ct).ConfigureAwait(false);

        var states = new Dictionary<string, GitFileState>(StringComparer.OrdinalIgnoreCase);

        if (result is not { ExitCode: 0 })
        {
            return states;
        }

        foreach (var record in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            Parse(record, states);
        }

        return states;
    }

    /// <summary>
    /// porcelain=v2 record forms:
    /// <c>1 XY ... path</c> ordinary, <c>2 XY ... path</c> renamed, <c>u XY ... path</c> unmerged,
    /// <c>? path</c> untracked, <c>! path</c> ignored.
    /// </summary>
    private static void Parse(string record, Dictionary<string, GitFileState> states)
    {
        if (record.Length < 2)
        {
            return;
        }

        switch (record[0])
        {
            case '?':
                states[record[2..]] = GitFileState.Untracked;
                return;

            case '!':
                states[record[2..]] = GitFileState.Ignored;
                return;

            case 'u':
                var unmergedPath = LastField(record, 10);
                if (unmergedPath is not null)
                {
                    states[unmergedPath] = GitFileState.Conflicted;
                }

                return;

            case '1':
            case '2':
                var fields = record.Split(' ', 9);
                if (fields.Length < 9)
                {
                    return;
                }

                var xy = fields[1];
                var path = fields[8];

                states[path] = xy switch
                {
                    _ when xy.Contains('R') => GitFileState.Renamed,
                    _ when xy.Contains('A') => GitFileState.Added,
                    _ when xy.Contains('D') => GitFileState.Deleted,
                    _ when xy.Contains('M') => GitFileState.Modified,
                    _ => GitFileState.Modified,
                };

                return;
        }
    }

    private static string? LastField(string record, int skipFields)
    {
        var fields = record.Split(' ', skipFields + 1);
        return fields.Length > skipFields ? fields[skipFields] : null;
    }

    private static async Task<ProcessResult?> RunAsync(string folder, string[] arguments, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return new ProcessResult(process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            // git is not on PATH, or the repository is large enough that status timed out. Neither
            // is worth interrupting the user: the tree simply shows no status markers.
            return null;
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
