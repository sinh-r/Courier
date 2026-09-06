namespace Courier.Core.Privacy;

/// <summary>
/// Thrown when something in the process tried to reach a destination the user did not ask for.
/// This is a bug in Courier, not a user error, and the message says so.
/// </summary>
public sealed class EgressDeniedException(string destination, EgressPurpose purpose, string reason)
    : InvalidOperationException(
        $"Courier refused to connect to {destination} for {purpose}. {reason} " +
        "Nothing was sent. This is a defect in Courier: report it with this message.")
{
    public string Destination { get; } = destination;

    public EgressPurpose Purpose { get; } = purpose;

    public string Reason { get; } = reason;
}
