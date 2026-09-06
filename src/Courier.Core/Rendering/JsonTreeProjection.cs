namespace Courier.Core.Rendering;

/// <summary>
/// Turns an index plus an expansion state into the list of rows a virtualizing list should draw.
/// </summary>
/// <remarks>
/// Expansion is a bitset over node indices, not a set of objects, so toggling a row is one bit flip
/// and recomputing the projection is one linear pass with subtree skips. At 118,402 nodes that pass
/// is well under a frame, which is what keeps scrolling at 60fps (PERF-05) without incremental
/// bookkeeping that would be easy to get subtly wrong.
/// </remarks>
public sealed class JsonTreeProjection
{
    /// <summary>Depth past which containers start collapsed. The mock shows "collapsed past depth 2".</summary>
    public const int DefaultCollapseDepth = 2;

    private readonly JsonIndex _index;
    private readonly ulong[] _expanded;
    private int[] _rows = [];
    private int _rowCount;
    private bool _dirty = true;

    public JsonTreeProjection(JsonIndex index, int collapseDepth = DefaultCollapseDepth)
    {
        _index = index;
        _expanded = new ulong[(index.Count + 63) / 64];
        CollapseDepth = collapseDepth;
        ExpandToDepth(collapseDepth);
    }

    public int CollapseDepth { get; }

    /// <summary>Row count for the virtualizing list's extent. Recomputed lazily.</summary>
    public int RowCount
    {
        get
        {
            EnsureProjected();
            return _rowCount;
        }
    }

    /// <summary>Node index for a visible row.</summary>
    public int NodeAt(int row)
    {
        EnsureProjected();
        return _rows[row];
    }

    public bool IsExpanded(int nodeIndex) =>
        (_expanded[nodeIndex >> 6] & (1UL << (nodeIndex & 63))) != 0;

    public void SetExpanded(int nodeIndex, bool expanded)
    {
        var word = nodeIndex >> 6;
        var bit = 1UL << (nodeIndex & 63);

        if (expanded)
        {
            _expanded[word] |= bit;
        }
        else
        {
            _expanded[word] &= ~bit;
        }

        _dirty = true;
    }

    public void Toggle(int nodeIndex) => SetExpanded(nodeIndex, !IsExpanded(nodeIndex));

    /// <summary>Expands every container down to a depth, collapsing everything deeper.</summary>
    public void ExpandToDepth(int depth)
    {
        Array.Clear(_expanded);

        for (var i = 0; i < _index.Count; i++)
        {
            ref readonly var node = ref _index[i];
            if (node.IsContainer && node.Depth < depth)
            {
                SetExpanded(i, true);
            }
        }

        _dirty = true;
    }

    public void ExpandAll()
    {
        for (var i = 0; i < _index.Count; i++)
        {
            if (_index[i].IsContainer)
            {
                SetExpanded(i, true);
            }
        }

        _dirty = true;
    }

    public void CollapseAll()
    {
        Array.Clear(_expanded);
        _dirty = true;
    }

    /// <summary>
    /// Expands every ancestor of a node so it becomes visible, and returns its row. Used by search
    /// navigation and by the breadcrumb, both of which have to reveal a hit inside a collapsed branch.
    /// </summary>
    public int RevealAndFindRow(int nodeIndex)
    {
        var ancestor = _index[nodeIndex].ParentIndex;
        while (ancestor >= 0)
        {
            SetExpanded(ancestor, true);
            ancestor = _index[ancestor].ParentIndex;
        }

        EnsureProjected();
        return Array.BinarySearch(_rows, 0, _rowCount, nodeIndex) is var found && found >= 0 ? found : -1;
    }

    private void EnsureProjected()
    {
        if (!_dirty)
        {
            return;
        }

        if (_rows.Length < _index.Count)
        {
            _rows = new int[_index.Count];
        }

        var row = 0;
        var i = 0;

        while (i < _index.Count)
        {
            _rows[row++] = i;

            ref readonly var node = ref _index[i];
            if (node.IsContainer && !IsExpanded(i))
            {
                i = _index.NextSibling(i);   // skip the whole collapsed subtree
            }
            else
            {
                i++;
            }
        }

        _rowCount = row;
        _dirty = false;
    }
}
