namespace Courier.Core.Abstractions;

/// <summary>
/// System proxy detection including PAC scripts and proxy authentication, with per-host override.
/// ENT-10. Every resolution states where it came from so the settings page can label the row.
/// </summary>
public interface IProxyResolver
{
    ValueTask<ProxyDecision> ResolveAsync(Uri destination, CancellationToken ct = default);

    /// <summary>The configured system-wide state, for display on the trust and network page.</summary>
    ValueTask<SystemProxyDescription> DescribeSystemProxyAsync(CancellationToken ct = default);
}

/// <param name="ProxyUri">Null means "go direct".</param>
/// <param name="Source">Provenance for the UI: "from system proxy", "from PAC script", "set by you".</param>
/// <param name="RequiresAuthentication">True when the proxy has challenged before.</param>
public sealed record ProxyDecision(Uri? ProxyUri, string Source, bool RequiresAuthentication = false)
{
    public static ProxyDecision Direct(string source) => new(null, source);
}

/// <param name="ProxyUri">The static proxy, if one is configured.</param>
/// <param name="PacUrl">The PAC script URL, if auto-configuration is in use.</param>
/// <param name="BypassList">Hosts that go direct.</param>
public sealed record SystemProxyDescription(Uri? ProxyUri, Uri? PacUrl, IReadOnlyList<string> BypassList, string Source);
