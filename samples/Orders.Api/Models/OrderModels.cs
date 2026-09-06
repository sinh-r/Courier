using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Orders.Api.Models;

/// <summary>
/// DTOs with the validation attributes SCAN-04 has to honour, so a generated sample body passes
/// the endpoint's own model validation rather than producing a 400 on the user's first send.
/// </summary>
public sealed class CreateOrderRequest
{
    [Required]
    [StringLength(32, MinimumLength = 3)]
    public required string CustomerId { get; init; }

    [Required]
    [MinLength(1)]
    public required List<OrderLine> Lines { get; init; }

    public Channel Channel { get; init; } = Channel.Web;

    [EmailAddress]
    public string? NotificationEmail { get; init; }

    /// <summary>Exercises the JsonPropertyName path: the wire name differs from the C# one.</summary>
    [JsonPropertyName("po_number")]
    public string? PurchaseOrderNumber { get; init; }

    public Address? ShipTo { get; init; }
}

public sealed class OrderLine
{
    [Required]
    [RegularExpression(@"^SKU-\d{4}$")]
    public required string Sku { get; init; }

    [Range(1, 999)]
    public int Qty { get; init; }

    [Range(0.0, 100000.0)]
    public decimal UnitPrice { get; init; }
}

public sealed class Address
{
    [Required]
    public required string Line1 { get; init; }

    public string? Line2 { get; init; }

    [Required]
    [StringLength(10)]
    public required string Postcode { get; init; }

    /// <summary>A self-reference, so the cycle-breaking in the sample generator is exercised.</summary>
    public Address? Previous { get; init; }
}

public sealed class UpdateLinesRequest
{
    [Required]
    public required List<OrderLine> Lines { get; init; }
}

public sealed class CancelOrderRequest
{
    [Required]
    [StringLength(200)]
    public required string Reason { get; init; }

    public bool NotifyCustomer { get; init; } = true;
}

public sealed record OrderSummary(string Id, string Status, decimal Total);

public sealed record OrderDetail(
    string Id,
    string Status,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<OrderLine> Lines,
    decimal Total);

public sealed record AuditEntry(DateTimeOffset AtUtc, string Actor, string Action);

public enum Channel
{
    Web,
    Mobile,
    Partner,
}
