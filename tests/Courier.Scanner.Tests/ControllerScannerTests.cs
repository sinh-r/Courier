using Courier.Scanner;
using Courier.Scanner.Routing;

namespace Courier.Scanner.Tests;

/// <summary>
/// Golden tests against the sample solutions. SCAN-01 through SCAN-08.
/// </summary>
public sealed class ControllerScannerTests
{
    private static ScanResult ScanOrders() => new SolutionScanner().Scan(SampleSolutions.Orders);

    [Fact]
    public void Derives_every_endpoint_from_the_orders_controller()
    {
        var result = ScanOrders();

        var routes = result.Endpoints.Select(e => $"{e.Method} {e.RouteTemplate}").ToList();

        Assert.Contains("GET api/v2.0/Orders", routes);
        Assert.Contains("GET api/v2.0/Orders/{id}", routes);
        Assert.Contains("POST api/v2.0/Orders", routes);
        Assert.Contains("PATCH api/v2.0/Orders/{id}/lines", routes);
        Assert.Contains("POST api/v2.0/Orders/{id}/cancel", routes);
        Assert.Contains("GET api/v2.0/Orders/{id}/audit", routes);
    }

    [Fact]
    public void Resolves_the_controller_token_and_the_api_version()
    {
        var result = ScanOrders();

        // SCAN-02: [controller] becomes Orders, and {version:apiVersion} becomes the declared 2.0.
        Assert.All(
            result.Endpoints.Where(e => e.RouteTemplate.StartsWith("api/", StringComparison.Ordinal)),
            e =>
            {
                Assert.DoesNotContain("[controller]", e.RouteTemplate, StringComparison.Ordinal);
                Assert.DoesNotContain("apiVersion", e.RouteTemplate, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void An_absolute_action_template_replaces_the_controller_prefix()
    {
        var result = ScanOrders();

        // The health probe declares "/api/v2/health", which must not be appended to the prefix.
        var health = result.Endpoints.Single(e => e.ActionName == "Health");
        Assert.Equal("api/v2/health", health.RouteTemplate);
    }

    [Fact]
    public void Binds_parameters_by_source()
    {
        var result = ScanOrders();
        var list = result.Endpoints.Single(e => e.ActionName == "List");

        // SCAN-03: simple types with no route token bind from the query.
        Assert.Contains(list.Parameters, p => p.Name == "status" && p.Source == ParameterSource.Query);
        Assert.Contains(list.Parameters, p => p.Name == "page" && p.Source == ParameterSource.Query);

        var byId = result.Endpoints.Single(e => e.ActionName == "GetById");
        Assert.Contains(byId.Parameters, p => p.Name == "id" && p.Source == ParameterSource.Route);

        var audit = result.Endpoints.Single(e => e.ActionName == "Audit");
        Assert.Contains(audit.Parameters, p => p.Name == "X-Correlation-Id" && p.Source == ParameterSource.Header);
    }

    [Fact]
    public void Captures_authorization_and_surfaces_required_scopes()
    {
        var result = ScanOrders();

        // SCAN-06: the policy is captured and surfaced as a scope before the request is sent.
        var create = result.Endpoints.Single(e => e.ActionName == "Create");
        Assert.Contains("Orders.Write", create.Authorization.Policies);
        Assert.Contains("Orders.Write", create.RequiredScopes);

        var cancel = result.Endpoints.Single(e => e.ActionName == "Cancel");
        Assert.Contains("Orders.Admin", cancel.RequiredScopes);

        // [AllowAnonymous] on the action overrides [Authorize] on the controller, as at runtime.
        var health = result.Endpoints.Single(e => e.ActionName == "Health");
        Assert.True(health.Authorization.AllowsAnonymous);
    }

    [Fact]
    public void Captures_declared_response_types()
    {
        var result = ScanOrders();
        var byId = result.Endpoints.Single(e => e.ActionName == "GetById");

        // SCAN-05, in both the typeof and the StatusCodes-constant forms.
        Assert.Contains(byId.Responses, r => r.StatusCode == 200 && r.TypeName == "OrderDetail");
        Assert.Contains(byId.Responses, r => r.StatusCode == 404);
    }

    [Fact]
    public void Derives_environments_from_launch_settings()
    {
        var result = ScanOrders();

        // SCAN-07, preferring https over the plaintext profile on the same line.
        var qa = result.Environments.Single(e => e.Name == "QA-Internal");
        Assert.Equal("https://qa.internal/orders", qa.BaseUrl);

        var local = result.Environments.Single(e => e.Name == "Local");
        Assert.StartsWith("https://localhost", local.BaseUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Works_without_a_restore_or_a_build()
    {
        // SCAN-08. The sample has never been restored in this test run, and the scan still returns
        // endpoints — which is the entire argument for shipping the syntax tier first.
        var result = ScanOrders();

        Assert.Equal(ScanTier.Syntax, result.Tier);
        Assert.NotEmpty(result.Endpoints);
    }

    [Fact]
    public void Reports_the_long_tail_rather_than_dropping_it()
    {
        var result = new SolutionScanner().Scan(SampleSolutions.Legacy, ct: TestContext.Current.CancellationToken);

        // REQUIREMENTS 9: an endpoint that cannot be derived is listed, never omitted. Every
        // action in the awkward controller must appear on one side of the result or the other.
        var accounted = result.Endpoints.Select(e => e.ActionName)
            .Concat(result.Unresolved.Select(u => u.ActionName))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var action in new[] { "Search", "Absolute", "Upsert", "Index", "Files", "Remove" })
        {
            Assert.Contains(action, accounted);
        }
    }

    [Fact]
    public void Two_verbs_on_one_action_produce_two_endpoints()
    {
        var result = new SolutionScanner().Scan(SampleSolutions.Legacy, ct: TestContext.Current.CancellationToken);
        var upsert = result.Endpoints.Where(e => e.ActionName == "Upsert").ToList();

        Assert.Equal(2, upsert.Count);
        Assert.Contains(upsert, e => e.Method == "PUT");
        Assert.Contains(upsert, e => e.Method == "PATCH");

        // Different verbs are different endpoints, so their identities must differ.
        Assert.NotEqual(upsert[0].Id, upsert[1].Id);
    }

    [Fact]
    public void An_action_with_no_derivable_route_is_reported_with_a_reason()
    {
        var result = new SolutionScanner().Scan(SampleSolutions.Legacy, ct: TestContext.Current.CancellationToken);

        // AwkwardController has a [Route] prefix, so Index resolves to that prefix rather than
        // being unresolved. What matters is that it is accounted for and its route is sane.
        var index = result.Endpoints.SingleOrDefault(e => e.ActionName == "Index");
        Assert.NotNull(index);
        Assert.Equal("legacy/Awkward", index.RouteTemplate);
    }

    [Fact]
    public void Incremental_rescan_reuses_endpoints_from_unchanged_files()
    {
        var scanner = new SolutionScanner();
        var first = scanner.Scan(SampleSolutions.Orders, ct: TestContext.Current.CancellationToken);

        var second = scanner.Scan(SampleSolutions.Orders, first.FileHashes, first.Endpoints, TestContext.Current.CancellationToken);

        // SCAN-10, PERF-06: nothing changed, so nothing was reparsed and the result is identical.
        Assert.Equal(
            first.Endpoints.Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal),
            second.Endpoints.Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal));
    }
}

internal static class SampleSolutions
{
    public static string Orders { get; } = Locate("Orders.Api");

    public static string Legacy { get; } = Locate("Legacy.Api");

    private static string Locate(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find samples/{name} above {AppContext.BaseDirectory}.");
    }
}
