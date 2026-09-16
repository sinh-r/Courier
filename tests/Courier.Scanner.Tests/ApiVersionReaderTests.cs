using Courier.Scanner.Routing;
using Courier.Scanner.Syntax;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Courier.Scanner.Tests;

/// <summary>
/// Every spelling of a version must reach the same URL. SCAN-02. The expected text follows the API
/// explorer's default SubstitutionFormat, VVV: the minor version only when it is not zero.
/// </summary>
public sealed class ApiVersionReaderTests
{
    [Theory]
    [InlineData("[ApiVersion(\"1\")]", "1")]
    [InlineData("[ApiVersion(\"1.0\")]", "1")]
    [InlineData("[ApiVersion(\"2.0\")]", "2")]
    [InlineData("[ApiVersion(\"1.5\")]", "1.5")]
    [InlineData("[ApiVersion(\"1.0-beta\")]", "1-beta")]
    [InlineData("[ApiVersion(\"1.1-RC\")]", "1.1-RC")]
    [InlineData("[ApiVersion(\"2015-05-01\")]", "2015-05-01")]
    [InlineData("[ApiVersion(\"2015-05-01.3.0\")]", "2015-05-01.3")]
    [InlineData("[ApiVersion(1)]", "1")]
    [InlineData("[ApiVersion(1.0)]", "1")]
    [InlineData("[ApiVersion(2.1)]", "2.1")]
    [InlineData("[ApiVersion(1.0, \"beta\")]", "1-beta")]
    [InlineData("[ApiVersion(1, 0)]", "1")]
    [InlineData("[ApiVersion(1, 5)]", "1.5")]
    [InlineData("[ApiVersion(1, 0, \"alpha\")]", "1-alpha")]
    [InlineData("[ApiVersionAttribute(\"3.0\")]", "3")]
    [InlineData("[Asp.Versioning.ApiVersion(\"3.2\")]", "3.2")]
    [InlineData("[ApiVersion(\"2.0\", Deprecated = true)]", "2")]
    public void Reads_every_attribute_form_into_the_same_text(string attribute, string expected)
    {
        Assert.Equal(expected, ApiVersionReader.FromAttributes(AttributesOf(attribute)));
    }

    [Theory]
    [InlineData("[ApiVersion(Versions.Current)]")]
    [InlineData("[ApiVersion(\"v1\")]")]
    [InlineData("[ApiVersion(\"latest\")]")]
    [InlineData("[Route(\"api\")]")]
    public void Returns_null_rather_than_guessing(string attribute)
    {
        Assert.Null(ApiVersionReader.FromAttributes(AttributesOf(attribute)));
    }

    [Theory]
    [InlineData("HasApiVersion(1, 0)", "1")]
    [InlineData("HasApiVersion(2)", "2")]
    [InlineData("HasApiVersion(1.5)", "1.5")]
    [InlineData("HasApiVersion(new ApiVersion(1, 0))", "1")]
    [InlineData("HasApiVersion(new ApiVersion(2, 1, \"beta\"))", "2.1-beta")]
    public void Reads_the_arguments_of_HasApiVersion(string call, string expected)
    {
        var invocation = (InvocationExpressionSyntax)SyntaxFactory.ParseExpression(call);

        Assert.Equal(expected, ApiVersionReader.FromArguments(invocation.ArgumentList));
    }

    private static Microsoft.CodeAnalysis.SyntaxList<AttributeListSyntax> AttributesOf(string attribute) =>
        CSharpSyntaxTree.ParseText($"{attribute} class C {{ }}")
            .GetCompilationUnitRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Single()
            .AttributeLists;
}

/// <summary>The version reaches the route template for both controller and minimal API endpoints.</summary>
public sealed class ApiVersionRouteTests
{
    [Theory]
    [InlineData("[ApiVersion(1, 0)]", "api/v1/Orders")]
    [InlineData("[ApiVersion(1.0)]", "api/v1/Orders")]
    [InlineData("[ApiVersion(\"1.0\")]", "api/v1/Orders")]
    [InlineData("[ApiVersion(1, 5)]", "api/v1.5/Orders")]
    public void A_controller_version_is_substituted_whichever_way_it_is_written(string attribute, string expected)
    {
        var source = $$"""
            [ApiController]
            [Route("api/v{version:apiVersion}/[controller]")]
            {{attribute}}
            public class OrdersController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() => Ok();
            }
            """;

        var endpoint = new ControllerSyntaxScanner().ScanFile("OrdersController.cs", source)
            .OfType<ScannedEndpoint>()
            .Single();

        Assert.Equal(expected, endpoint.RouteTemplate);
        Assert.Empty(endpoint.PartialResolutionNotes);
    }

    [Fact]
    public void An_unreadable_controller_version_is_noted_rather_than_left_silently()
    {
        const string source = """
            [Route("api/v{version:apiVersion}/[controller]")]
            [ApiVersion(Versions.Current)]
            public class OrdersController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() => Ok();
            }
            """;

        var endpoint = new ControllerSyntaxScanner().ScanFile("OrdersController.cs", source)
            .OfType<ScannedEndpoint>()
            .Single();

        Assert.Equal("api/v{version:apiVersion}/Orders", endpoint.RouteTemplate);
        Assert.Contains(endpoint.PartialResolutionNotes, n => n.Contains("API version", StringComparison.Ordinal));
    }

    [Fact]
    public void A_minimal_api_group_version_is_substituted_through_a_variable()
    {
        const string source = """
            public static class CatalogApi
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    var vApi = app.NewVersionedApi("Catalog");
                    var v1 = vApi.MapGroup("api/v{version:apiVersion}/catalog").HasApiVersion(1, 0);
                    v1.MapGet("/items", () => Results.Ok());

                    app.NewVersionedApi("Catalog")
                        .MapGroup("api/v{version:apiVersion}/catalog")
                        .HasApiVersion(2.5)
                        .MapGet("/brands", () => Results.Ok());
                }
            }
            """;

        var routes = new MinimalApiScanner().ScanFile("CatalogApi.cs", source)
            .OfType<ScannedEndpoint>()
            .Select(e => e.RouteTemplate)
            .ToList();

        Assert.Contains("api/v1/catalog/items", routes);
        Assert.Contains("api/v2.5/catalog/brands", routes);
    }

    [Fact]
    public void A_minimal_api_group_without_a_readable_version_is_noted()
    {
        const string source = """
            var group = app.MapGroup("api/v{version:apiVersion}/catalog");
            group.MapGet("/items", () => Results.Ok());
            """;

        var endpoint = new MinimalApiScanner().ScanFile("Program.cs", source)
            .OfType<ScannedEndpoint>()
            .Single();

        Assert.Equal("api/v{version:apiVersion}/catalog/items", endpoint.RouteTemplate);
        Assert.Contains(endpoint.PartialResolutionNotes, n => n.Contains("API version", StringComparison.Ordinal));
    }
}
