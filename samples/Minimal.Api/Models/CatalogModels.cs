namespace Minimal.Api.Models;

public sealed record CatalogItem(int Id, string Name);

public sealed record CreateCatalogItemRequest(string Name, decimal Price);

public sealed record UpdateCatalogItemRequest(string Name, decimal Price);
