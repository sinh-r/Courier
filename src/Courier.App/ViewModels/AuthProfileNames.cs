namespace Courier.App.ViewModels;

/// <summary>
/// The per-request auth profile combo box's fixed item list. ENT-02/ENT-06 name a much larger set
/// of real flows; this is the minimal set that lets the field record a choice rather than sit
/// permanently empty. Wiring an actual token acquisition chain per profile is deferred pending a
/// real tenant to validate against (Docs/NEEDS_LIVE_VALIDATION.md).
/// </summary>
public static class AuthProfileNames
{
    public static readonly string[] All = ["None", "Bearer", "Basic", "API Key"];
}
