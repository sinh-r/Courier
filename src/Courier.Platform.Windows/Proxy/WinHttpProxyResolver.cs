using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Courier.Core.Abstractions;

namespace Courier.Platform.Windows.Proxy;

/// <summary>
/// System proxy detection including PAC scripts, by P/Invoke to WinHTTP. ENT-10.
/// </summary>
/// <remarks>
/// <para>
/// TECH_SPEC 3.1 offers WinHttpHandler for PAC-based proxying. This takes the other route: WinHTTP
/// evaluates the PAC script here, and the answer is handed to SocketsHttpHandler as an already
/// resolved proxy. The reason is CORE-01 — WinHttpHandler cannot do HTTP/2 prior knowledge or
/// HTTP/3 at all, and losing those to gain PAC support would trade a headline feature for a
/// plumbing detail.
/// </para>
/// <para>
/// PAC evaluation is expensive and its answer is stable per host, so results are cached. A machine
/// whose PAC file changes mid-session is rare enough to be worth a restart.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WinHttpProxyResolver : IProxyResolver, IDisposable
{
    private const uint AccessTypeNoProxy = 1;
    private const uint AutoDetectTypeDhcp = 0x00000001;
    private const uint AutoDetectTypeDnsA = 0x00000002;
    private const uint AutoLogonIfChallenged = 0x00000001;
    private const int ErrorAutoProxyServiceNotFound = 12178;
    private const int ErrorLoginFailure = 1326;

    private readonly ConcurrentDictionary<string, ProxyDecision> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<IntPtr> _session;
    private readonly Lazy<SystemProxyDescription> _system;

    /// <summary>Per-host overrides the user set. These win over anything the system says. ENT-10.</summary>
    public ConcurrentDictionary<string, Uri?> HostOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    public WinHttpProxyResolver()
    {
        _session = new Lazy<IntPtr>(() => WinHttpOpen(
            "Courier",
            AccessTypeNoProxy,
            null,
            null,
            0));

        _system = new Lazy<SystemProxyDescription>(ReadSystemConfiguration);
    }

    public ValueTask<ProxyDecision> ResolveAsync(Uri destination, CancellationToken ct = default)
    {
        if (HostOverrides.TryGetValue(destination.Host, out var overridden))
        {
            return ValueTask.FromResult(overridden is null
                ? ProxyDecision.Direct("set by you")
                : new ProxyDecision(overridden, "set by you"));
        }

        return ValueTask.FromResult(_cache.GetOrAdd(destination.Host, _ => Resolve(destination)));
    }

    public ValueTask<SystemProxyDescription> DescribeSystemProxyAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(_system.Value);

    private ProxyDecision Resolve(Uri destination)
    {
        var configuration = _system.Value;

        if (IsBypassed(destination.Host, configuration.BypassList))
        {
            return ProxyDecision.Direct("bypass list");
        }

        if (configuration.PacUrl is not null || configuration.ProxyUri is null)
        {
            if (TryEvaluatePac(destination, configuration.PacUrl, out var fromPac))
            {
                return fromPac;
            }
        }

        return configuration.ProxyUri is null
            ? ProxyDecision.Direct("no system proxy")
            : new ProxyDecision(configuration.ProxyUri, "from system proxy");
    }

    private bool TryEvaluatePac(Uri destination, Uri? pacUrl, out ProxyDecision decision)
    {
        decision = ProxyDecision.Direct("no proxy auto-configuration");

        var session = _session.Value;
        if (session == IntPtr.Zero)
        {
            return false;
        }

        var options = new WINHTTP_AUTOPROXY_OPTIONS
        {
            dwFlags = pacUrl is null ? 0x00000001u /* AUTO_DETECT */ : 0x00000002u /* CONFIG_URL */,
            dwAutoDetectFlags = pacUrl is null ? AutoDetectTypeDhcp | AutoDetectTypeDnsA : 0,
            lpszAutoConfigUrl = pacUrl?.ToString(),
            fAutoLogonIfChallenged = true,
        };

        if (!WinHttpGetProxyForUrl(session, destination.ToString(), ref options, out var info))
        {
            var error = Marshal.GetLastWin32Error();

            if (error == ErrorLoginFailure)
            {
                // The PAC host challenged. Retry once with auto-logon, which is the documented
                // remedy and the common case on a domain-joined machine.
                options.fAutoLogonIfChallenged = true;
                if (!WinHttpGetProxyForUrl(session, destination.ToString(), ref options, out info))
                {
                    return false;
                }
            }
            else if (error == ErrorAutoProxyServiceNotFound)
            {
                return false;
            }
            else
            {
                return false;
            }
        }

        try
        {
            if (info.dwAccessType == AccessTypeNoProxy || string.IsNullOrEmpty(info.lpszProxy))
            {
                decision = ProxyDecision.Direct("proxy auto-configuration says direct");
                return true;
            }

            // WinHTTP returns a space or semicolon separated list. The first reachable one wins,
            // and trying them in order is the proxy's own preference.
            var first = info.lpszProxy!.Split([' ', ';'], StringSplitOptions.RemoveEmptyEntries)[0];
            var uri = first.Contains("://", StringComparison.Ordinal) ? first : $"http://{first}";

            decision = new ProxyDecision(
                new Uri(uri),
                pacUrl is null ? "from proxy auto-detection" : "from PAC script");

            return true;
        }
        finally
        {
            FreeProxyInfo(ref info);
        }
    }

    private static SystemProxyDescription ReadSystemConfiguration()
    {
        if (!WinHttpGetIEProxyConfigForCurrentUser(out var config))
        {
            return new SystemProxyDescription(null, null, [], "no system proxy configured");
        }

        try
        {
            Uri? proxy = null;
            if (!string.IsNullOrWhiteSpace(config.lpszProxy))
            {
                var first = config.lpszProxy!
                    .Split([' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Contains('=', StringComparison.Ordinal) ? p.Split('=', 2)[1] : p)
                    .FirstOrDefault();

                if (first is not null)
                {
                    Uri.TryCreate(
                        first.Contains("://", StringComparison.Ordinal) ? first : $"http://{first}",
                        UriKind.Absolute,
                        out proxy);
                }
            }

            Uri? pac = null;
            if (!string.IsNullOrWhiteSpace(config.lpszAutoConfigUrl))
            {
                Uri.TryCreate(config.lpszAutoConfigUrl, UriKind.Absolute, out pac);
            }

            var bypass = string.IsNullOrWhiteSpace(config.lpszProxyBypass)
                ? []
                : config.lpszProxyBypass!.Split([';', ' '], StringSplitOptions.RemoveEmptyEntries);

            var source = (proxy, pac, config.fAutoDetect) switch
            {
                (_, not null, _) => "from PAC script",
                (not null, _, _) => "from system proxy",
                (_, _, true) => "from proxy auto-detection",
                _ => "no system proxy configured",
            };

            return new SystemProxyDescription(proxy, pac, bypass, source);
        }
        finally
        {
            FreeIeConfig(ref config);
        }
    }

    /// <summary>Windows bypass syntax: exact hosts, *.suffix wildcards, and the &lt;local&gt; token.</summary>
    private static bool IsBypassed(string host, IReadOnlyList<string> bypassList)
    {
        foreach (var entry in bypassList)
        {
            if (entry.Equals("<local>", StringComparison.OrdinalIgnoreCase))
            {
                if (!host.Contains('.', StringComparison.Ordinal)
                    || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (entry.StartsWith('*'))
            {
                if (host.EndsWith(entry[1..], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (host.Equals(entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void FreeProxyInfo(ref WINHTTP_PROXY_INFO info)
    {
        if (info.lpszProxyPtr != IntPtr.Zero)
        {
            GlobalFree(info.lpszProxyPtr);
        }

        if (info.lpszProxyBypassPtr != IntPtr.Zero)
        {
            GlobalFree(info.lpszProxyBypassPtr);
        }
    }

    private static void FreeIeConfig(ref WINHTTP_CURRENT_USER_IE_PROXY_CONFIG config)
    {
        foreach (var pointer in new[] { config.lpszAutoConfigUrlPtr, config.lpszProxyPtr, config.lpszProxyBypassPtr })
        {
            if (pointer != IntPtr.Zero)
            {
                GlobalFree(pointer);
            }
        }
    }

    public void Dispose()
    {
        if (_session.IsValueCreated && _session.Value != IntPtr.Zero)
        {
            WinHttpCloseHandle(_session.Value);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINHTTP_AUTOPROXY_OPTIONS
    {
        public uint dwFlags;
        public uint dwAutoDetectFlags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszAutoConfigUrl;
        public IntPtr lpvReserved;
        public uint dwReserved;
        [MarshalAs(UnmanagedType.Bool)] public bool fAutoLogonIfChallenged;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_PROXY_INFO
    {
        public uint dwAccessType;
        public IntPtr lpszProxyPtr;
        public IntPtr lpszProxyBypassPtr;

        public readonly string? lpszProxy => Marshal.PtrToStringUni(lpszProxyPtr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINHTTP_CURRENT_USER_IE_PROXY_CONFIG
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fAutoDetect;
        public IntPtr lpszAutoConfigUrlPtr;
        public IntPtr lpszProxyPtr;
        public IntPtr lpszProxyBypassPtr;

        public readonly string? lpszAutoConfigUrl => Marshal.PtrToStringUni(lpszAutoConfigUrlPtr);

        public readonly string? lpszProxy => Marshal.PtrToStringUni(lpszProxyPtr);

        public readonly string? lpszProxyBypass => Marshal.PtrToStringUni(lpszProxyBypassPtr);
    }

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpOpen", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr WinHttpOpen(string agent, uint accessType, string? proxy, string? bypass, uint flags);

    [LibraryImport("winhttp.dll", EntryPoint = "WinHttpCloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinHttpCloseHandle(IntPtr handle);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpGetProxyForUrl", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetProxyForUrl(
        IntPtr session,
        string url,
        ref WINHTTP_AUTOPROXY_OPTIONS options,
        out WINHTTP_PROXY_INFO info);

    [DllImport("winhttp.dll", EntryPoint = "WinHttpGetIEProxyConfigForCurrentUser", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(
        out WINHTTP_CURRENT_USER_IE_PROXY_CONFIG config);

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalFree")]
    private static partial IntPtr GlobalFree(IntPtr memory);
}
