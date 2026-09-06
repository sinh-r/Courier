namespace Courier.Core.Abstractions;

/// <summary>
/// Inline git status in the collection tree. STOR-05 says "without embedding a git client", so
/// implementations shell out to the git the user already has rather than linking a library.
/// </summary>
public interface IGitStatusSource
{
    /// <summary>False when the folder is not a work tree, or git is not on PATH. Not an error.</summary>
    ValueTask<bool> IsRepositoryAsync(string folder, CancellationToken ct = default);

    /// <summary>
    /// Status for every path under the folder that differs from HEAD. Paths are repository-relative
    /// with forward slashes. Returns empty when the folder is not a repository.
    /// </summary>
    ValueTask<IReadOnlyDictionary<string, GitFileState>> GetStatusAsync(string folder, CancellationToken ct = default);
}

public enum GitFileState
{
    Unchanged,
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
    Ignored,
    Conflicted,
}
