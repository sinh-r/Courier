using System.Text.Json;

namespace Courier.Scanner.Tests;

/// <summary>
/// SCAN-04, syntax tier: a sample request body generated from the scanned source, honouring
/// validation attributes, enums, nullability and <c>[JsonPropertyName]</c>, without a restore.
/// </summary>
public sealed class SampleBodyTests
{
    [Fact]
    public void Generates_a_sample_body_valid_by_construction()
    {
        var result = new SolutionScanner().Scan(SampleSolutions.Orders, ct: TestContext.Current.CancellationToken);
        var create = result.Endpoints.Single(e => e.ActionName == "Create");

        Assert.NotNull(create.SampleBody);
        Assert.Equal("application/json", create.BodyContentType);

        using var document = JsonDocument.Parse(create.SampleBody);
        var root = document.RootElement;

        // [Required][StringLength(32, MinimumLength = 3)] on a required string.
        Assert.True(root.GetProperty("customerId").GetString()!.Length is >= 3 and <= 32);

        // [Required][MinLength(1)] List<OrderLine> — at least one element, itself walked for its
        // own constraints: [Range(1, 999)] and [Range(0.0, 100000.0)] land inside that range.
        var line = root.GetProperty("lines")[0];
        Assert.InRange(line.GetProperty("qty").GetInt32(), 1, 999);
        Assert.InRange(line.GetProperty("unitPrice").GetDouble(), 0.0, 100000.0);

        // An enum property samples its first declared member, an ASP.NET Core will accept.
        Assert.Equal("Web", root.GetProperty("channel").GetString());

        // [JsonPropertyName("po_number")] — the wire name, not the C# one.
        Assert.True(root.TryGetProperty("po_number", out _));
        Assert.False(root.TryGetProperty("purchaseOrderNumber", out _));

        // A nested DTO (Address) is walked too, and its own self-reference (Address? Previous)
        // does not recurse forever.
        var shipTo = root.GetProperty("shipTo");
        Assert.True(shipTo.TryGetProperty("line1", out _));
        Assert.Equal(JsonValueKind.Null, shipTo.GetProperty("previous").ValueKind);
    }

    [Fact]
    public void A_positional_record_body_produces_a_sample_from_its_parameters()
    {
        var result = new SolutionScanner().Scan(SampleSolutions.Minimal, ct: TestContext.Current.CancellationToken);
        var create = result.Endpoints.Single(e => e.ActionName == "CreateItem");

        Assert.NotNull(create.SampleBody);

        using var document = JsonDocument.Parse(create.SampleBody);
        Assert.True(document.RootElement.TryGetProperty("name", out _));
        Assert.True(document.RootElement.TryGetProperty("price", out _));
    }

    [Fact]
    public void An_unresolvable_body_type_is_reported_rather_than_guessed_at()
    {
        // AwkwardController's Upsert method declares `[FromBody] object body` — a body parameter
        // whose declared type (object) has no shape at all. REQUIREMENTS 9: reported, not omitted.
        var result = new SolutionScanner().Scan(SampleSolutions.Legacy, ct: TestContext.Current.CancellationToken);
        var upsert = result.Endpoints.First(e => e.ActionName == "Upsert");

        Assert.Null(upsert.SampleBody);
        Assert.Contains(upsert.PartialResolutionNotes, n => n.Contains("could not", StringComparison.OrdinalIgnoreCase) || n.Contains("Load the solution", StringComparison.Ordinal));
    }
}
