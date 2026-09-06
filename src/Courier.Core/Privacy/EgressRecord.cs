namespace Courier.Core.Privacy;

/// <summary>
/// One connection this process opened, or refused to. The full set is what SEC-06's auditable
/// statement is rendered from, so it is generated from behaviour rather than written by hand and
/// cannot drift from what the code actually does.
/// </summary>
/// <param name="Host">Host only. Paths can carry identifiers, so they are not recorded here.</param>
/// <param name="Initiator">The component that asked, e.g. "RequestExecutor", "EntraTokenProvider".</param>
public sealed record EgressRecord(
    string Host,
    int Port,
    string Scheme,
    EgressPurpose Purpose,
    string Initiator,
    DateTimeOffset AtUtc,
    bool WasAllowed,
    string? DenialReason = null)
{
    public string Destination => Port switch
    {
        443 when Scheme == "https" => $"{Scheme}://{Host}",
        80 when Scheme == "http" => $"{Scheme}://{Host}",
        _ => $"{Scheme}://{Host}:{Port}",
    };
}
