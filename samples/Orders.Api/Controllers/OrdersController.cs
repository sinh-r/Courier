using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Orders.Api.Models;

namespace Orders.Api.Controllers;

/// <summary>
/// The controller the mock depicts. Ordinary attribute routing with a versioned prefix, scoped
/// authorization, DTOs with validation attributes, and declared response types.
/// </summary>
[ApiController]
[Route("api/v{version:apiVersion}/[controller]")]
[ApiVersion("2.0")]
[Authorize]
public sealed class OrdersController : ControllerBase
{
    /// <summary>Lists orders for the current tenant.</summary>
    [HttpGet]
    [Authorize(Policy = "Orders.Read")]
    [ProducesResponseType(typeof(OrderSummary[]), StatusCodes.Status200OK)]
    public IActionResult List([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => Ok(Array.Empty<OrderSummary>());

    /// <summary>Gets a single order by its identifier.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = "Orders.Read")]
    [ProducesResponseType(typeof(OrderDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetById(string id) => Ok();

    /// <summary>Creates an order.</summary>
    [HttpPost]
    [Authorize(Policy = "Orders.Write")]
    [ProducesResponseType(typeof(OrderDetail), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Create([FromBody] CreateOrderRequest request) => Created(string.Empty, null);

    /// <summary>Updates the lines on an existing order.</summary>
    [HttpPatch("{id}/lines")]
    [Authorize(Policy = "Orders.Write")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult UpdateLines(string id, [FromBody] UpdateLinesRequest request) => NoContent();

    /// <summary>Cancels an order.</summary>
    [HttpPost("{id}/cancel")]
    [Authorize(Policy = "Orders.Admin")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult Cancel(string id, [FromBody] CancelOrderRequest request) => Accepted();

    /// <summary>Returns the audit trail for an order.</summary>
    [HttpGet("{id}/audit")]
    [Authorize(Policy = "Orders.Read")]
    [ProducesResponseType(typeof(AuditEntry[]), StatusCodes.Status200OK)]
    public IActionResult Audit(string id, [FromHeader(Name = "X-Correlation-Id")] string? correlationId)
        => Ok(Array.Empty<AuditEntry>());

    /// <summary>Health probe. Deliberately anonymous, to exercise the AllowAnonymous path.</summary>
    [HttpHead("/api/v2/health")]
    [AllowAnonymous]
    public IActionResult Health() => Ok();
}
