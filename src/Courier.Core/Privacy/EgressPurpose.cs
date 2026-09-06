namespace Courier.Core.Privacy;

/// <summary>
/// Why a connection is being opened. Every outbound request declares one, and the network
/// statement (SEC-06) is grouped by it, so the reader can see there is no category they did not
/// ask for.
/// </summary>
public enum EgressPurpose
{
    /// <summary>A request the user composed and sent. The overwhelming majority.</summary>
    TargetRequest,

    /// <summary>A token endpoint or OIDC metadata document for an auth profile the user configured.</summary>
    AuthAuthority,

    /// <summary>A monitoring backend the user configured, for the telemetry bridge.</summary>
    TelemetryBackend,

    /// <summary>Azure DevOps, only when the user attaches or opens a capsule from a work item.</summary>
    WorkItemTracker,

    /// <summary>A proxy auto-configuration script named by the system proxy settings.</summary>
    ProxyAutoConfig,

    /// <summary>Explicitly opt-in and off by default. SEC-01.</summary>
    UpdateCheck,
}
