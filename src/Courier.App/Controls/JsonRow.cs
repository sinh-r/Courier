using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Courier.Core.Rendering;

namespace Courier.App.Controls;

/// <summary>
/// One visible row, materialized only when the repeater asks for it.
/// </summary>
/// <remarks>
/// This is the boundary where offsets become strings. TECH_SPEC 3.7's rule — build a flat index of
/// node offsets, not an object tree — only pays off if nothing upstream of here allocates per node.
/// A 40MB response therefore costs one <see cref="JsonRow"/> per row actually on screen, roughly
/// forty of them, rather than 118,402.
/// </remarks>
public sealed class JsonRow
{
    /// <summary>Values longer than this are elided; the full value is available on selection.</summary>
    private const int MaxValueLength = 200;

    public required int NodeIndex { get; init; }

    public required string Name { get; init; }

    public required string Separator { get; init; }

    public required string Value { get; init; }

    public required string Chevron { get; init; }

    public required int Depth { get; init; }

    public required IBrush ValueBrush { get; init; }

    public Thickness IndentMargin => new(8 + (Depth * 14), 0, 8, 0);

    public static JsonRow Create(JsonIndex index, ReadOnlySpan<byte> utf8, int nodeIndex, bool expanded)
    {
        ref readonly var node = ref index[nodeIndex];

        var name = index.NameOf(nodeIndex, utf8)
            ?? (node.ParentIndex >= 0 && index[node.ParentIndex].Kind == JsonNodeKind.Array
                ? $"[{index.IndexWithinParent(nodeIndex)}]"
                : string.Empty);

        return new JsonRow
        {
            NodeIndex = nodeIndex,
            Depth = node.Depth,
            Name = name,
            Separator = name.Length > 0 && !node.IsContainer ? ":" : string.Empty,
            Chevron = node.IsContainer ? (expanded ? "▾" : "▸") : " ",
            Value = DescribeValue(index, utf8, nodeIndex, expanded),
            ValueBrush = BrushFor(node.Kind),
        };
    }

    /// <summary>
    /// A collapsed container summarises itself the way the mock does — <c>{ id: "ORD-0001", … 14
    /// keys }</c> and <c>[3]</c> — so a collapsed row still tells the reader what is inside.
    /// </summary>
    private static string DescribeValue(JsonIndex index, ReadOnlySpan<byte> utf8, int nodeIndex, bool expanded)
    {
        ref readonly var node = ref index[nodeIndex];

        if (node.Kind == JsonNodeKind.Array)
        {
            return expanded ? string.Empty : $"[{node.ChildCount}]";
        }

        if (node.Kind == JsonNodeKind.Object)
        {
            if (expanded)
            {
                return string.Empty;
            }

            if (node.ChildCount == 0)
            {
                return "{ }";
            }

            var firstChild = nodeIndex + 1;
            var firstName = index.NameOf(firstChild, utf8);
            var firstValue = Truncate(Encoding.UTF8.GetString(index.RawValue(firstChild, utf8)), 24);

            return node.ChildCount == 1
                ? $"{{ {firstName}: {firstValue} }}"
                : $"{{ {firstName}: {firstValue}, … {node.ChildCount} keys }}";
        }

        return Truncate(Encoding.UTF8.GetString(index.RawValue(nodeIndex, utf8)), MaxValueLength);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : $"{value[..max]}…";

    /// <summary>
    /// Scalars are muted, keys are ink. Values do not get a type colour: UI_SPEC 3.1 reserves
    /// colour for meaning the user would otherwise have to read, and "this is a number" is legible
    /// from the glyphs.
    /// </summary>
    private static IBrush BrushFor(JsonNodeKind kind) => kind switch
    {
        JsonNodeKind.Object or JsonNodeKind.Array => Brushes.Gray,
        _ => Brushes.Gray,
    };
}

/// <summary>
/// The repeater's data source: a virtual list that materializes a <see cref="JsonRow"/> on demand
/// and never holds more than the rows on screen.
/// </summary>
public sealed class JsonRowSource : IList, INotifyCollectionChanged
{
    private readonly ViewModels.ResponseViewModel _response;
    private readonly JsonTreeProjection _projection;

    /// <summary>
    /// Takes the response rather than the bytes. The buffer may be a memory-mapped view, and a
    /// span cannot be stored in a field, so the response is the only thing that can hand out a
    /// view of it without copying 40MB onto the managed heap.
    /// </summary>
    public JsonRowSource(ViewModels.ResponseViewModel response, JsonTreeProjection projection)
    {
        _response = response;
        _projection = projection;
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Count => _projection.RowCount;

    public bool IsFixedSize => false;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int index]
    {
        get
        {
            var node = _projection.NodeAt(index);
            return _response.CreateRow(node, _projection.IsExpanded(node));
        }

        set => throw new NotSupportedException();
    }

    /// <summary>Called after an expand or collapse; the row set changes wholesale.</summary>
    public void Invalidate() =>
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i]!;
        }
    }

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public bool Contains(object? value) => false;

    public int IndexOf(object? value) => -1;

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        for (var i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }
}
