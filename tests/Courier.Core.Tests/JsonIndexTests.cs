using System.Text;
using Courier.Core.Rendering;

namespace Courier.Core.Tests;

public sealed class JsonIndexTests
{
    private const string Sample = """
        {
          "order": {
            "id": "ORD-4471",
            "status": "Pending",
            "lines": [
              { "sku": "SKU-8841", "qty": 2 },
              { "sku": "SKU-1207", "qty": 1 }
            ],
            "total": 184.20,
            "cancelled": false,
            "note": null
          }
        }
        """;

    private static JsonIndex Index(string json) =>
        JsonIndex.TryBuild(Encoding.UTF8.GetBytes(json))
        ?? throw new InvalidOperationException("Expected the sample to index.");

    [Fact]
    public void Indexes_every_node_in_document_order()
    {
        var index = Index(Sample);

        Assert.Equal(JsonNodeKind.Object, index[0].Kind);
        Assert.Equal(-1, index[0].ParentIndex);
        Assert.Equal(1, index[0].ChildCount);

        // Root object, order object, id, status, lines array, two line objects with two fields
        // each, total, cancelled, note.
        Assert.Equal(14, index.Count);
    }

    [Fact]
    public void Subtree_size_lets_a_collapsed_branch_be_skipped_in_one_step()
    {
        var index = Index(Sample);
        var lines = FindByName(index, Sample, "lines");

        Assert.Equal(JsonNodeKind.Array, index[lines].Kind);
        Assert.Equal(2, index[lines].ChildCount);

        // Two objects with two properties each.
        Assert.Equal(6, index[lines].SubtreeSize);
        Assert.Equal(lines + 7, index.NextSibling(lines));
    }

    [Fact]
    public void Records_scalar_kinds_distinctly()
    {
        var index = Index(Sample);
        var utf8 = Encoding.UTF8.GetBytes(Sample);

        Assert.Equal(JsonNodeKind.String, index[FindByName(index, Sample, "id")].Kind);
        Assert.Equal(JsonNodeKind.Number, index[FindByName(index, Sample, "total")].Kind);
        Assert.Equal(JsonNodeKind.False, index[FindByName(index, Sample, "cancelled")].Kind);
        Assert.Equal(JsonNodeKind.Null, index[FindByName(index, Sample, "note")].Kind);

        var id = FindByName(index, Sample, "id");
        Assert.Equal("\"ORD-4471\"", Encoding.UTF8.GetString(index.RawValue(id, utf8)));
    }

    [Fact]
    public void Container_raw_value_round_trips_as_valid_json()
    {
        var index = Index(Sample);
        var utf8 = Encoding.UTF8.GetBytes(Sample);
        var lines = FindByName(index, Sample, "lines");

        var raw = Encoding.UTF8.GetString(index.RawValue(lines, utf8));

        Assert.StartsWith("[", raw, StringComparison.Ordinal);
        Assert.EndsWith("]", raw, StringComparison.Ordinal);

        // The slice must be parseable on its own, which is what makes "copy this subtree" cheap.
        using var parsed = System.Text.Json.JsonDocument.Parse(raw);
        Assert.Equal(2, parsed.RootElement.GetArrayLength());
    }

    [Fact]
    public void Path_reads_as_the_breadcrumb_the_response_pane_shows()
    {
        var index = Index(Sample);
        var utf8 = Encoding.UTF8.GetBytes(Sample);
        var lines = FindByName(index, Sample, "lines");

        // The second element of lines, then its sku.
        var secondElement = index.NextSibling(lines + 1);
        var sku = secondElement + 1;

        Assert.Equal(["order", "lines", "[1]", "sku"], index.PathTo(sku, utf8));
    }

    [Fact]
    public void Returns_null_for_content_that_is_not_json()
    {
        Assert.Null(JsonIndex.TryBuild("<html><body>nope</body></html>"u8));
        Assert.Null(JsonIndex.TryBuild([]));
    }

    [Fact]
    public void Indexes_a_large_array_without_materialising_a_document()
    {
        var json = BuildLargeArray(20_000);
        var index = Index(json);

        // One array, 20,000 objects, three properties each.
        Assert.Equal(1 + (20_000 * 4), index.Count);
        Assert.Equal(JsonNodeKind.Array, index[0].Kind);
        Assert.Equal(20_000, index[0].ChildCount);
        Assert.False(index.WasTruncated);
    }

    [Fact]
    public void Reports_truncation_rather_than_exhausting_memory()
    {
        var json = BuildLargeArray(5_000);
        var index = JsonIndex.TryBuild(Encoding.UTF8.GetBytes(json), maxNodes: 100)!;

        Assert.True(index.WasTruncated);
        Assert.True(index.Count <= 100);
    }

    internal static string BuildLargeArray(int count)
    {
        var sb = new StringBuilder(count * 48);
        sb.Append('[');

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"id\":\"ORD-").Append(i.ToString("D5")).Append("\",\"customerId\":\"CUS-")
              .Append((i % 997).ToString("D5")).Append("\",\"total\":").Append(i % 500).Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static int FindByName(JsonIndex index, string json, string name)
    {
        var utf8 = Encoding.UTF8.GetBytes(json);
        for (var i = 0; i < index.Count; i++)
        {
            if (index.NameOf(i, utf8) == name)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"No node named '{name}'.");
    }
}
