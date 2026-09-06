using Courier.Core.Collections;

namespace Courier.Scanner.Tests;

/// <summary>
/// The join key between generated endpoints and the user's overlay. SCAN-09.
/// </summary>
/// <remarks>
/// TECH_SPEC 3.6: "Getting this identity wrong is the failure mode that makes the whole feature
/// hostile, so test it hard."
///
/// Two ways to be wrong, and they fail differently. Too unstable and a saved payload detaches from
/// its endpoint on an unrelated edit — the user loses work and stops trusting regeneration. Too
/// stable and a payload reattaches to the wrong endpoint — the user sends the wrong body to the
/// wrong URL. Every test here pins one side or the other.
/// </remarks>
public sealed class EndpointIdentityTests
{
    private const string Controller = "Orders.Api.Controllers.OrdersController";

    [Fact]
    public void Is_stable_across_a_method_rename()
    {
        // The whole point. Renaming Get to GetById in C# must not orphan the saved payload.
        var before = EndpointIdentity.Compute("GET", "api/v2/orders/{id}", Controller);
        var after = EndpointIdentity.Compute("GET", "api/v2/orders/{id}", Controller);

        Assert.Equal(before, after);
    }

    [Fact]
    public void Is_stable_across_route_constraint_and_optional_markers()
    {
        // {id}, {id:int}, {id?} and {id=1} are the same endpoint with different declarations.
        var plain = EndpointIdentity.Compute("GET", "api/v2/orders/{id}", Controller);

        Assert.Equal(plain, EndpointIdentity.Compute("GET", "api/v2/orders/{id:int}", Controller));
        Assert.Equal(plain, EndpointIdentity.Compute("GET", "api/v2/orders/{id?}", Controller));
        Assert.Equal(plain, EndpointIdentity.Compute("GET", "api/v2/orders/{id=1}", Controller));
        Assert.Equal(plain, EndpointIdentity.Compute("GET", "api/v2/orders/{id:guid:required}", Controller));
    }

    [Fact]
    public void Is_stable_across_leading_and_trailing_slashes_and_casing()
    {
        var plain = EndpointIdentity.Compute("GET", "api/v2/orders", Controller);

        Assert.Equal(plain, EndpointIdentity.Compute("GET", "/api/v2/orders", Controller));
        Assert.Equal(plain, EndpointIdentity.Compute("GET", "api/v2/orders/", Controller));
        Assert.Equal(plain, EndpointIdentity.Compute("GET", "API/V2/Orders", Controller));
        Assert.Equal(plain, EndpointIdentity.Compute("get", "api/v2/orders", Controller));
    }

    [Fact]
    public void Changes_when_the_route_parameter_is_renamed()
    {
        // {id} to {orderId} is a breaking URL-shape change for anyone reading the collection, and
        // the saved path parameter no longer applies. It must be a different endpoint.
        Assert.NotEqual(
            EndpointIdentity.Compute("GET", "api/v2/orders/{id}", Controller),
            EndpointIdentity.Compute("GET", "api/v2/orders/{orderId}", Controller));
    }

    [Fact]
    public void Changes_when_the_verb_changes()
    {
        Assert.NotEqual(
            EndpointIdentity.Compute("GET", "api/v2/orders", Controller),
            EndpointIdentity.Compute("POST", "api/v2/orders", Controller));
    }

    [Fact]
    public void Changes_when_the_api_version_changes()
    {
        // A v1 payload must not silently reattach to the v2 endpoint.
        Assert.NotEqual(
            EndpointIdentity.Compute("GET", "api/v1/orders", Controller),
            EndpointIdentity.Compute("GET", "api/v2/orders", Controller));
    }

    [Fact]
    public void Distinguishes_the_same_route_on_two_controllers()
    {
        // Two controllers can legitimately expose the same route in different areas. Folding them
        // together would attach one controller's payload to the other's endpoint.
        Assert.NotEqual(
            EndpointIdentity.Compute("GET", "api/v2/orders", "Orders.Api.Controllers.OrdersController"),
            EndpointIdentity.Compute("GET", "api/v2/orders", "Legacy.Api.Controllers.OrdersController"));
    }

    [Fact]
    public void Changes_when_the_controller_moves_namespace()
    {
        // A deliberate trade-off, documented here because it is the one case that costs the user:
        // moving a controller between namespaces orphans its overlay entries. They are kept rather
        // than deleted, so the payload survives and can be reattached by hand.
        Assert.NotEqual(
            EndpointIdentity.Compute("GET", "api/v2/orders", "Orders.Api.Controllers.OrdersController"),
            EndpointIdentity.Compute("GET", "api/v2/orders", "Orders.Api.V2.OrdersController"));
    }

    [Fact]
    public void Is_a_readable_fixed_length_hex_id()
    {
        var id = EndpointIdentity.Compute("GET", "api/v2/orders/{id}", Controller);

        // 128 bits as 32 hex characters: enough to keep thousands of endpoints apart, short enough
        // to stay readable in a YAML file a human has to diff.
        Assert.Equal(32, id.Length);
        Assert.True(id.All(c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
    }

    [Theory]
    [InlineData("{id:int}", "{id}")]
    [InlineData("{id?}", "{id}")]
    [InlineData("{*path}", "{path}")]
    [InlineData("{**path}", "{path}")]
    [InlineData("/api/Orders/", "api/orders")]
    [InlineData("{soft:bool=true}", "{soft}")]
    public void Normalises_a_route_to_its_identity_form(string input, string expected) =>
        Assert.Equal(expected, EndpointIdentity.NormaliseRoute(input));
}
