using System.Buffers;
using System.Text.Json;

namespace Courier.Core.Rendering;

/// <summary>
/// A flat index of a JSON document: offsets, not an object tree.
/// </summary>
/// <remarks>
/// <para>
/// This is the component PERF-05 rests on, and TECH_SPEC 3.7 is right that no framework control
/// will do it. The rules it is built to:
/// </para>
/// <list type="bullet">
///   <item>Never materialize a <c>JsonDocument</c> for a large payload. One pass with
///   <see cref="Utf8JsonReader"/> over a buffer the caller owns.</item>
///   <item>Nodes are stored pre-order in one array, each knowing its subtree size, so the visible
///   rows for any expansion state are a single linear walk with skips — no tree traversal, no
///   allocation per row.</item>
///   <item>Values are never copied out of the buffer during indexing. A row's text is materialized
///   only when it is about to be drawn.</item>
/// </list>
/// </remarks>
public sealed class JsonIndex
{
    private readonly JsonNode[] _nodes;

    private JsonIndex(JsonNode[] nodes, int count, bool truncated)
    {
        _nodes = nodes;
        Count = count;
        WasTruncated = truncated;
    }

    /// <summary>Number of indexed nodes. Shown as "118,402 nodes" in the response header.</summary>
    public int Count { get; }

    /// <summary>True when indexing stopped at the node cap, so the UI can say so rather than lie.</summary>
    public bool WasTruncated { get; }

    public ref readonly JsonNode this[int index] => ref _nodes[index];

    /// <summary>
    /// Indexes a UTF-8 JSON document. Returns null when the payload is not JSON, which is a normal
    /// outcome — the caller falls back to the raw view rather than showing an error.
    /// </summary>
    /// <param name="maxNodes">
    /// A ceiling so a pathological document cannot exhaust memory. Reaching it sets
    /// <see cref="WasTruncated"/>; the raw view still shows everything.
    /// </param>
    public static JsonIndex? TryBuild(ReadOnlySpan<byte> utf8, int maxNodes = 4_000_000)
    {
        if (utf8.IsEmpty)
        {
            return null;
        }

        // Estimate from document size rather than growing from nothing: one node per ~24 bytes is
        // close for real API payloads and avoids a dozen array copies on a 40MB body.
        var capacity = Math.Clamp((int)(utf8.Length / 24) + 16, 16, maxNodes);
        var nodes = ArrayPool<JsonNode>.Shared.Rent(capacity);
        var count = 0;
        var truncated = false;

        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 512,
            });

            // Container stack holds the index of the open object or array, so a child can record its
            // parent and a closing token can finalise the subtree size in O(1).
            Span<int> stack = stackalloc int[64];
            var stackArray = default(int[]);
            var depth = 0;

            var pendingNameStart = -1;
            var pendingNameLength = 0;

            while (reader.Read())
            {
                if (count >= maxNodes)
                {
                    truncated = true;
                    break;
                }

                var token = reader.TokenType;

                if (token == JsonTokenType.PropertyName)
                {
                    // TokenStartIndex points at the opening quote; the name sits inside it.
                    pendingNameStart = (int)reader.TokenStartIndex + 1;
                    pendingNameLength = reader.ValueSpan.Length;
                    continue;
                }

                if (token is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    depth--;
                    var container = stack[depth];
                    ref var open = ref nodes[container];

                    // Everything appended since the container opened is its subtree.
                    open.SubtreeSize = count - container - 1;

                    // The container's value span runs from its opening brace to this closing one,
                    // which is what lets "copy this subtree" be a slice rather than a re-serialize.
                    open.ValueLength = (int)reader.TokenStartIndex + 1 - open.ValueStart;
                    continue;
                }

                if (count >= nodes.Length)
                {
                    Grow(ref nodes, count);
                }

                var parent = depth > 0 ? stack[depth - 1] : -1;
                ref var node = ref nodes[count];

                node.ParentIndex = parent;
                node.NameStart = pendingNameStart;
                node.NameLength = pendingNameLength;
                node.Depth = depth;
                node.ValueStart = (int)reader.TokenStartIndex;
                node.SubtreeSize = 0;
                node.ChildCount = 0;

                pendingNameStart = -1;
                pendingNameLength = 0;

                if (parent >= 0)
                {
                    nodes[parent].ChildCount++;
                }

                switch (token)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        node.Kind = token == JsonTokenType.StartObject ? JsonNodeKind.Object : JsonNodeKind.Array;
                        EnsureStack(ref stack, ref stackArray, depth);
                        stack[depth] = count;
                        depth++;
                        break;

                    case JsonTokenType.String:
                        node.Kind = JsonNodeKind.String;
                        // Include both quotes so the raw slice round-trips as valid JSON.
                        node.ValueLength = (int)(reader.BytesConsumed - reader.TokenStartIndex);
                        break;

                    case JsonTokenType.Number:
                        node.Kind = JsonNodeKind.Number;
                        node.ValueLength = reader.ValueSpan.Length;
                        break;

                    case JsonTokenType.True:
                        node.Kind = JsonNodeKind.True;
                        node.ValueLength = 4;
                        break;

                    case JsonTokenType.False:
                        node.Kind = JsonNodeKind.False;
                        node.ValueLength = 5;
                        break;

                    case JsonTokenType.Null:
                        node.Kind = JsonNodeKind.Null;
                        node.ValueLength = 4;
                        break;

                    default:
                        continue;
                }

                count++;
            }

            if (count == 0)
            {
                return null;
            }

            var owned = new JsonNode[count];
            Array.Copy(nodes, owned, count);
            return new JsonIndex(owned, count, truncated);
        }
        catch (JsonException)
        {
            // Not JSON, or malformed past the point of usefulness. The raw and preview views still
            // work, so this is a fallback rather than a failure.
            return null;
        }
        finally
        {
            ArrayPool<JsonNode>.Shared.Return(nodes);
        }
    }

    private static void Grow(ref JsonNode[] nodes, int count)
    {
        var bigger = ArrayPool<JsonNode>.Shared.Rent(nodes.Length * 2);
        Array.Copy(nodes, bigger, count);
        ArrayPool<JsonNode>.Shared.Return(nodes);
        nodes = bigger;
    }

    private static void EnsureStack(ref Span<int> stack, ref int[]? backing, int depth)
    {
        if (depth < stack.Length)
        {
            return;
        }

        var bigger = new int[stack.Length * 2];
        stack.CopyTo(bigger);
        backing = bigger;
        stack = bigger;
    }

    /// <summary>Index of the first node after this node's subtree, for skipping a collapsed branch.</summary>
    public int NextSibling(int index) => index + 1 + _nodes[index].SubtreeSize;

    /// <summary>
    /// The dotted path to a node, for the breadcrumb: <c>results › [418] › lines › [2]</c>.
    /// Walks parents rather than storing a path per node, which would double the index size.
    /// </summary>
    public IReadOnlyList<string> PathTo(int index, ReadOnlySpan<byte> utf8)
    {
        var segments = new List<string>();
        var current = index;

        while (current >= 0)
        {
            ref readonly var node = ref _nodes[current];
            var parent = node.ParentIndex;

            if (node.NameLength > 0)
            {
                segments.Add(System.Text.Encoding.UTF8.GetString(utf8.Slice(node.NameStart, node.NameLength)));
            }
            else if (parent >= 0 && _nodes[parent].Kind == JsonNodeKind.Array)
            {
                segments.Add($"[{IndexWithinParent(current)}]");
            }

            current = parent;
        }

        segments.Reverse();
        return segments;
    }

    /// <summary>Position of a node among its parent's children, for array breadcrumbs.</summary>
    public int IndexWithinParent(int index)
    {
        var parent = _nodes[index].ParentIndex;
        if (parent < 0)
        {
            return 0;
        }

        var position = 0;
        var cursor = parent + 1;
        var end = NextSibling(parent);

        while (cursor < end && cursor < index)
        {
            position++;
            cursor = NextSibling(cursor);
        }

        return position;
    }

    /// <summary>The name of a node, or null for an array element.</summary>
    public string? NameOf(int index, ReadOnlySpan<byte> utf8)
    {
        ref readonly var node = ref _nodes[index];
        return node.NameLength > 0
            ? System.Text.Encoding.UTF8.GetString(utf8.Slice(node.NameStart, node.NameLength))
            : null;
    }

    /// <summary>The raw value slice, still valid JSON for objects, arrays and strings.</summary>
    public ReadOnlySpan<byte> RawValue(int index, ReadOnlySpan<byte> utf8)
    {
        ref readonly var node = ref _nodes[index];
        var length = Math.Min(node.ValueLength, utf8.Length - node.ValueStart);
        return length <= 0 ? [] : utf8.Slice(node.ValueStart, length);
    }
}

public enum JsonNodeKind : byte
{
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null,
}

/// <summary>
/// One node. Offsets into the caller's buffer; no strings, no references, no allocation.
/// </summary>
public struct JsonNode
{
    /// <summary>-1 for the root.</summary>
    public int ParentIndex;

    /// <summary>Offset of the property name inside its quotes, or -1 for an array element.</summary>
    public int NameStart;

    public int NameLength;

    /// <summary>Offset of the value, including the opening brace, bracket or quote.</summary>
    public int ValueStart;

    public int ValueLength;

    /// <summary>Total descendants. The subtree occupies [self+1, self+SubtreeSize].</summary>
    public int SubtreeSize;

    /// <summary>Direct children, for the "{ id: …, 14 keys }" and "[3]" summaries.</summary>
    public int ChildCount;

    public int Depth;

    public JsonNodeKind Kind;

    public readonly bool IsContainer => Kind is JsonNodeKind.Object or JsonNodeKind.Array;
}
