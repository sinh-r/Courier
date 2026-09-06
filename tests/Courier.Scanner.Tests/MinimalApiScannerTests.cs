using Courier.Scanner;
using Courier.Scanner.Syntax;

namespace Courier.Scanner.Tests;

/// <summary>
/// Golden tests for minimal-API endpoint derivation, against samples/Minimal.Api. Real minimal-API
/// code rarely looks like the textbook <c>app.MapGet("/x", () => ...)</c> in Program.cs — groups are
/// held in variables, handlers are method groups, and registration happens inside an extension
/// method. This fixture is shaped that way on purpose.
/// </summary>
public sealed class MinimalApiScannerTests
{
    private static ScanResult Scan() => new SolutionScanner().Scan(
        SampleSolutions.Minimal, ct: TestContext.Current.CancellationToken);

    [Fact]
    public void Resolves_a_group_prefix_held_in_a_variable()
    {
        var result = Scan();
        var routes = result.Endpoints.Select(e => $"{e.Method} {e.RouteTemplate}").ToList();

        Assert.Contains("GET api/catalog/items", routes);
        Assert.Contains("GET api/catalog/items/{id}", routes);
    }

    [Fact]
    public void Resolves_a_group_reached_through_a_versioned_api_builder()
    {
        // vApi = app.NewVersionedApi("Catalog") contributes no prefix of its own; v1's prefix comes
        // from the .MapGroup("api/catalog") chained onto it. If that chain were not walked, these
        // routes would resolve to just "items" with no "api/catalog" prefix.
        var result = Scan();
        var byId = result.Endpoints.Single(e => e.ActionName == "GetItemById");

        Assert.Equal("api/catalog/items/{id}", byId.RouteTemplate);
    }

    [Fact]
    public void Direct_chaining_without_an_intermediate_variable_also_resolves()
    {
        var result = Scan();
        var brands = result.Endpoints.Single(e => e.ActionName == "GetBrands");

        Assert.Equal("api/catalog/brands", brands.RouteTemplate);
        Assert.True(brands.Authorization.AllowsAnonymous);
    }

    [Fact]
    public void Every_verb_produces_its_own_endpoint()
    {
        var result = Scan();
        var byAction = result.Endpoints.ToLookup(e => e.ActionName);

        Assert.Equal("GET", byAction["GetAllItems"].Single().Method);
        Assert.Equal("POST", byAction["CreateItem"].Single().Method);
        Assert.Equal("PUT", byAction["UpdateItem"].Single().Method);
        Assert.Equal("DELETE", byAction["DeleteItem"].Single().Method);
    }

    [Fact]
    public void MapMethods_with_a_verb_array_produces_one_endpoint_per_verb()
    {
        var result = Scan();
        var archive = result.Endpoints.Where(e => e.ActionName == "ArchiveItem").ToList();

        Assert.Equal(2, archive.Count);
        Assert.Contains(archive, e => e.Method == "POST");
        Assert.Contains(archive, e => e.Method == "PUT");
        Assert.NotEqual(archive[0].Id, archive[1].Id);
    }

    [Fact]
    public void A_lambda_handler_binds_parameters_the_same_way_a_method_group_does()
    {
        var result = Scan();
        var price = result.Endpoints.Single(e => e.RouteTemplate == "api/catalog/items/{id}/price");

        Assert.Contains(price.Parameters, p => p.Name == "id" && p.Source == ParameterSource.Route);
    }

    [Fact]
    public void Binds_parameters_by_source()
    {
        var result = Scan();

        var list = result.Endpoints.Single(e => e.ActionName == "GetAllItems");
        Assert.Contains(list.Parameters, p => p.Name == "type" && p.Source == ParameterSource.Query);
        Assert.Contains(list.Parameters, p => p.Name == "pageSize" && p.Source == ParameterSource.Query);

        var byId = result.Endpoints.Single(e => e.ActionName == "GetItemById");
        Assert.Contains(byId.Parameters, p => p.Name == "id" && p.Source == ParameterSource.Route);
    }

    [Fact]
    public void RequireAuthorization_surfaces_a_scope_the_same_way_the_authorize_attribute_does()
    {
        var result = Scan();

        var create = result.Endpoints.Single(e => e.ActionName == "CreateItem");
        Assert.Contains("Catalog.Write", create.Authorization.Policies);
        Assert.Contains("Catalog.Write", create.RequiredScopes);

        var delete = result.Endpoints.Single(e => e.ActionName == "DeleteItem");
        Assert.Contains("Catalog.Admin", delete.RequiredScopes);
    }

    [Fact]
    public void Declared_responses_are_captured_from_the_fluent_chain()
    {
        var result = Scan();
        var byId = result.Endpoints.Single(e => e.ActionName == "GetItemById");

        Assert.Contains(byId.Responses, r => r.StatusCode == 200 && r.TypeName == "CatalogItem");
        Assert.Contains(byId.Responses, r => r.StatusCode == 404);
    }

    [Fact]
    public void A_route_that_is_not_a_literal_is_reported_as_unresolved_rather_than_dropped()
    {
        var result = Scan();

        Assert.Contains(result.Unresolved, u => u.ActionName == "SearchItems");
        Assert.DoesNotContain(result.Endpoints, e => e.ActionName == "SearchItems");
    }

    [Fact]
    public void Derives_environments_from_launch_settings_the_same_way_as_a_controller_project()
    {
        var result = Scan();

        var local = result.Environments.Single(e => e.Name == "Local");
        Assert.StartsWith("https://localhost", local.BaseUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Works_without_a_restore_or_a_build()
    {
        var result = Scan();

        Assert.Equal(ScanTier.Syntax, result.Tier);
        Assert.NotEmpty(result.Endpoints);
    }
}
