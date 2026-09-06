using System.Collections.Concurrent;

namespace Courier.Core.Privacy;

/// <summary>
/// Decides whether a destination is one the user actually asked for. SEC-01.
/// </summary>
public interface IEgressPolicy
{
    EgressVerdict Evaluate(Uri destination, EgressPurpose purpose);
}

/// <param name="Reason">Always populated on a denial, and shown verbatim in the exception.</param>
public readonly record struct EgressVerdict(bool IsAllowed, string? Reason)
{
    public static EgressVerdict Allow() => new(true, null);

    public static EgressVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// The shipping policy. Everything is denied unless a user action registered the destination:
/// sending a request, configuring an auth profile, connecting a monitoring backend, attaching to a
/// work item. Nothing is allowed by default, and there is no category for analytics, crash
/// reporting or an update check that the user has not switched on.
/// </summary>
public sealed class UserIntentEgressPolicy : IEgressPolicy
{
    private readonly ConcurrentDictionary<string, EgressPurpose> _registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Off by default, and stays off unless the user opts in. SEC-01.</summary>
    public bool UpdateCheckOptedIn { get; set; }

    /// <summary>
    /// Records that a user action makes this host legitimate. Called when a request is composed,
    /// an auth profile saved, or a telemetry backend connected.
    /// </summary>
    public void Register(Uri destination, EgressPurpose purpose) =>
        _registered[destination.Host] = purpose;

    public void Forget(Uri destination) => _registered.TryRemove(destination.Host, out _);

    /// <summary>Hosts the user has made legitimate, for the settings page and the statement.</summary>
    public IReadOnlyDictionary<string, EgressPurpose> Registered => _registered;

    public EgressVerdict Evaluate(Uri destination, EgressPurpose purpose)
    {
        if (purpose == EgressPurpose.UpdateCheck && !UpdateCheckOptedIn)
        {
            return EgressVerdict.Deny(
                "Update checking is off. Courier does not contact any server unless you turn it on.");
        }

        // A request the user pressed Send on is self-authorising: the URL is the intent. The same
        // is true of a PAC script, whose URL comes from the machine's own proxy configuration.
        if (purpose is EgressPurpose.TargetRequest or EgressPurpose.ProxyAutoConfig)
        {
            return EgressVerdict.Allow();
        }

        if (_registered.TryGetValue(destination.Host, out var registeredFor) && registeredFor == purpose)
        {
            return EgressVerdict.Allow();
        }

        return EgressVerdict.Deny(
            $"No configuration in this profile names {destination.Host} as a {Describe(purpose)}.");
    }

    private static string Describe(EgressPurpose purpose) => purpose switch
    {
        EgressPurpose.AuthAuthority => "token authority",
        EgressPurpose.TelemetryBackend => "monitoring backend",
        EgressPurpose.WorkItemTracker => "work item tracker",
        EgressPurpose.ProxyAutoConfig => "proxy auto-configuration script",
        EgressPurpose.UpdateCheck => "update server",
        _ => "target",
    };
}
