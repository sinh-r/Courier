using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Minimal.Api.Models;

namespace Minimal.Api.Apis;

/// <summary>
/// A minimal-API reference for the scanner's golden tests. Deliberately shaped like a real
/// registration rather than a textbook one: groups held in variables, a nested group reached
/// through them, method-group handlers, one lambda handler, MapMethods, and one route that cannot
/// be resolved because it comes from a constant rather than a literal.
/// </summary>
public static class CatalogApi
{
    private const string SearchRoute = "/items/search";

    public static IEndpointRouteBuilder MapCatalogApi(this IEndpointRouteBuilder app)
    {
        var vApi = app.NewVersionedApi("Catalog");
        var v1 = vApi.MapGroup("api/catalog").HasApiVersion(1, 0);

        v1.MapGet("/items", GetAllItems)
            .WithName("ListItems")
            .WithSummary("List catalog items")
            .WithTags("Items")
            .Produces<CatalogItem[]>(200);

        v1.MapGet("/items/{id}", GetItemById)
            .WithTags("Items")
            .Produces<CatalogItem>(200)
            .ProducesResponseType(404);

        v1.MapPost("/items", CreateItem)
            .RequireAuthorization("Catalog.Write")
            .WithTags("Items");

        v1.MapPut("/items/{id}", UpdateItem)
            .RequireAuthorization("Catalog.Write");

        v1.MapDelete("/items/{id}", DeleteItem)
            .RequireAuthorization("Catalog.Admin");

        // Direct chaining, no intermediate variable, and no auth at all.
        app.MapGroup("api/catalog/brands").MapGet("/", GetBrands).AllowAnonymous();

        // MapMethods: one registration, two verbs.
        v1.MapMethods("/items/{id}/archive", ["POST", "PUT"], ArchiveItem);

        // A lambda handler rather than a method group.
        v1.MapGet("/items/{id}/price", (int id) => Results.Ok(9.99m)).WithTags("Items");

        // Unresolvable on purpose: the route is a constant, not a literal.
        v1.MapGet(SearchRoute, SearchItems);

        return app;
    }

    /// <summary>Lists catalog items.</summary>
    private static IResult GetAllItems([FromQuery] string? type, [FromQuery] int pageSize = 10) =>
        Results.Ok(Array.Empty<CatalogItem>());

    private static IResult GetItemById(int id) => Results.Ok(new CatalogItem(id, "Sample"));

    private static IResult CreateItem([FromBody] CreateCatalogItemRequest request) => Results.Created();

    private static IResult UpdateItem(int id, [FromBody] UpdateCatalogItemRequest request) => Results.NoContent();

    private static IResult DeleteItem(int id) => Results.NoContent();

    private static IResult GetBrands() => Results.Ok(Array.Empty<string>());

    private static IResult ArchiveItem(int id) => Results.Accepted();

    private static IResult SearchItems([FromQuery] string q) => Results.Ok(Array.Empty<CatalogItem>());
}
