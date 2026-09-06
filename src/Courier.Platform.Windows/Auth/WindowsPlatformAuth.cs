using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using Courier.Core.Abstractions;
using Courier.Core.Auth;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Courier.Platform.Windows.Auth;

/// <summary>
/// NTLM and Kerberos with the current Windows identity, and no credential entry. ENT-07.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsIntegratedAuth : IIntegratedAuthProvider
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public string? CurrentIdentity
    {
        get
        {
            try
            {
                return WindowsIdentity.GetCurrent().Name;
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public ICredentials? GetDefaultCredentials() => IsAvailable ? CredentialCache.DefaultNetworkCredentials : null;
}

/// <summary>
/// Adds the WAM broker to MSAL, which is what makes ENT-01 — silent SSO from the machine's
/// existing Windows sign-in — actually work.
/// </summary>
/// <remarks>
/// <para>
/// TECH_SPEC 1.2 and 7.2 both flag window-handle parenting under Avalonia as the most likely place
/// to hit a wall, and this is that place. The broker shows a native dialog and needs an HWND to
/// parent it to; without one it either fails or shows a window behind the app, which looks like a
/// hang. The handle comes from the UI layer through <see cref="IBrokerWindowHandleProvider"/>, so
/// nothing about Avalonia leaks into Courier.Core.
/// </para>
/// <para>
/// <b>Needs live validation:</b> this path cannot be exercised without a real tenant and a
/// domain-joined machine.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WamBrokerConfigurator(IBrokerWindowHandleProvider windows) : IBrokerConfigurator
{
    public PublicClientApplicationBuilder Configure(PublicClientApplicationBuilder builder) =>
        builder
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
            {
                Title = "Courier",

                // Lets AcquireTokenSilent use the account the machine is already signed in with,
                // rather than requiring a prior interactive sign-in inside Courier.
                ListOperatingSystemAccounts = true,
            })
            .WithParentActivityOrWindow(GetParentWindow);

    public IntPtr GetParentWindow() => windows.GetActiveWindowHandle();
}
