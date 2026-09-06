using System.Buffers;
using System.Text;

namespace Courier.Core.Rendering;

/// <summary>
/// Search over the raw response bytes, not the node tree. TECH_SPEC 3.7.
/// </summary>
/// <remarks>
/// The mock shows "1,284 matches" over a 40MB body with a live match counter. That is only
/// affordable because this walks the buffer with vectorized IndexOf and never touches the index:
/// searching a tree would mean materializing 118,402 strings to look at them.
/// </remarks>
public static class BodySearch
{
    /// <summary>Above this, only offsets up to the cap are collected. The count is still exact.</summary>
    public const int MaxCollectedMatches = 100_000;

    /// <summary>
    /// Finds every occurrence of <paramref name="term"/>. Case-insensitive search is done by
    /// scanning for both cases of the first byte rather than lower-casing 40MB of input.
    /// </summary>
    public static SearchResults Find(ReadOnlySpan<byte> haystack, string term, bool caseSensitive = false)
    {
        if (string.IsNullOrEmpty(term) || haystack.IsEmpty)
        {
            return SearchResults.Empty;
        }

        var needleBytes = Encoding.UTF8.GetBytes(term);
        var offsets = new List<int>(Math.Min(1024, MaxCollectedMatches));
        var total = 0;
        var position = 0;

        if (caseSensitive)
        {
            while (position < haystack.Length)
            {
                var found = haystack[position..].IndexOf(needleBytes);
                if (found < 0)
                {
                    break;
                }

                var absolute = position + found;
                total++;
                if (offsets.Count < MaxCollectedMatches)
                {
                    offsets.Add(absolute);
                }

                position = absolute + 1;
            }
        }
        else
        {
            var lower = Encoding.UTF8.GetBytes(term.ToLowerInvariant());
            var upper = Encoding.UTF8.GetBytes(term.ToUpperInvariant());
            var searchValues = SearchValues.Create([lower[0], upper[0]]);

            while (position < haystack.Length)
            {
                var candidate = haystack[position..].IndexOfAny(searchValues);
                if (candidate < 0)
                {
                    break;
                }

                var absolute = position + candidate;
                if (MatchesAt(haystack, absolute, lower, upper))
                {
                    total++;
                    if (offsets.Count < MaxCollectedMatches)
                    {
                        offsets.Add(absolute);
                    }
                }

                position = absolute + 1;
            }
        }

        return new SearchResults(offsets, total, total > offsets.Count);
    }

    private static bool MatchesAt(ReadOnlySpan<byte> haystack, int at, ReadOnlySpan<byte> lower, ReadOnlySpan<byte> upper)
    {
        if (at + lower.Length > haystack.Length)
        {
            return false;
        }

        for (var i = 0; i < lower.Length; i++)
        {
            var b = haystack[at + i];
            if (b != lower[i] && b != upper[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Maps a byte offset back to the node that contains it, so a hit can be revealed in the tree
    /// and shown in the breadcrumb. Binary search over the pre-order node array: value starts are
    /// ascending, so the containing node is the last one starting at or before the offset whose
    /// span still covers it.
    /// </summary>
    public static int NodeContaining(JsonIndex index, int offset)
    {
        var low = 0;
        var high = index.Count - 1;
        var best = -1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            ref readonly var node = ref index[mid];

            if (node.ValueStart <= offset)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        // Walk back to the innermost node whose span actually covers the offset. A hit inside a
        // property name lands on the node that owns the name.
        for (var i = best; i >= 0; i--)
        {
            ref readonly var node = ref index[i];
            if (node.ValueStart <= offset && offset < node.ValueStart + Math.Max(node.ValueLength, 1))
            {
                return i;
            }

            if (node.NameStart >= 0 && node.NameStart <= offset && offset < node.NameStart + node.NameLength)
            {
                return i;
            }
        }

        return best;
    }
}

/// <param name="Offsets">Byte offsets, capped. Enough to navigate; the count is not capped.</param>
/// <param name="Total">Exact match count, shown as "1,284 matches".</param>
public sealed record SearchResults(IReadOnlyList<int> Offsets, int Total, bool OffsetsTruncated)
{
    public static readonly SearchResults Empty = new([], 0, false);

    public bool Any => Total > 0;
}
