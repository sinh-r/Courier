using Courier.Core.Auth;

namespace Courier.Core.Tests;

/// <summary>
/// The Tenant ID field takes a GUID, checked before Entra is asked, so a mistyped tenant is named
/// as such rather than surfacing as an AADSTS code.
/// </summary>
public sealed class AuthProfileTenantTests
{
    [Theory]
    [InlineData(AuthKind.EntraClientCredentials)]
    [InlineData(AuthKind.EntraWindowsSignIn)]
    [InlineData(AuthKind.EntraDeviceCode)]
    [InlineData(AuthKind.EntraAuthorizationCode)]
    public void A_guid_tenant_is_accepted(AuthKind kind)
    {
        var profile = new AuthProfile { Kind = kind, Tenant = " 72f988bf-86f1-41af-91ab-2d7cd011db47 " };

        Assert.Null(profile.TenantProblem());
    }

    [Theory]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("72f988bf-86f1-41af-91ab")]
    public void A_tenant_that_is_not_a_guid_is_rejected(string tenant)
    {
        var profile = new AuthProfile { Kind = AuthKind.EntraDeviceCode, Tenant = tenant };

        Assert.Contains("must be a GUID", profile.TenantProblem());
    }

    [Fact]
    public void Client_credentials_needs_a_tenant()
    {
        var profile = new AuthProfile { Kind = AuthKind.EntraClientCredentials, Tenant = "  " };

        Assert.Contains("needs a Tenant ID", profile.TenantProblem());
    }

    [Fact]
    public void Interactive_kinds_may_leave_the_tenant_blank()
    {
        var profile = new AuthProfile { Kind = AuthKind.EntraAuthorizationCode };

        Assert.Null(profile.TenantProblem());
    }

    [Fact]
    public void Non_entra_kinds_and_explicit_authorities_are_not_checked()
    {
        Assert.Null(new AuthProfile { Kind = AuthKind.Bearer, Tenant = "anything" }.TenantProblem());
        Assert.Null(new AuthProfile
        {
            Kind = AuthKind.EntraClientCredentials,
            Tenant = "contoso.onmicrosoft.com",
            Authority = "https://login.example.com/contoso",
        }.TenantProblem());
    }
}
