namespace Courier.Core.Storage;

/// <summary>
/// Every place Courier writes to disk, in one list.
/// </summary>
/// <remarks>
/// SEC-05 needs a settings page listing every location with a reveal and a per-category clear, and
/// SEC-06 needs the same list in the exported statement. Both read this, so a new store cannot be
/// added without appearing in both — which is the point.
/// </remarks>
public static class StorageLocations
{
    /// <summary>%LOCALAPPDATA%\Courier on Windows; the XDG equivalent elsewhere.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "Courier");

    /// <summary>History, response cache and telemetry results. STOR-03: outside any git tree.</summary>
    public static string Database => Path.Combine(Root, "courier.db");

    /// <summary>Large response bodies spilled from memory. Cleared freely; never contains a secret.</summary>
    public static string ResponseCache => Path.Combine(Root, "cache");

    /// <summary>Serialized state for suspended tabs and crash recovery. PERF-02, NFR-07.</summary>
    public static string SessionState => Path.Combine(Root, "session");

    /// <summary>Application logs. Secret values never reach these, at any level. SEC-07.</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>User preferences. Never holds a credential.</summary>
    public static string Settings => Path.Combine(Root, "settings.json");

    /// <summary>Imported capsules staged before the user opens them. CAP-09: written nowhere else.</summary>
    public static string CapsuleInbox => Path.Combine(Root, "capsules");

    public static IReadOnlyList<StorageLocation> Describe() =>
    [
        new("History and cache", Database,
            "Request history, cached responses and telemetry results. Searchable locally, never uploaded."),
        new("Response cache", ResponseCache,
            "Response bodies too large to hold in memory. Deleted when history is cleared."),
        new("Session state", SessionState,
            "Open tabs and unsaved edits, so a crash does not lose your work."),
        new("Logs", Logs,
            "Diagnostic logs. Secret values are removed before writing, at every verbosity level."),
        new("Settings", Settings,
            "Preferences and window layout. Contains no credentials."),
        new("Capsule inbox", CapsuleInbox,
            "Capsules you imported. Inert data; nothing here is executed."),
        new("Secrets", SecretStoreDescription,
            "Held by the operating system, not by Courier. No file Courier writes contains a secret value."),
        new("Collections", "the folder you chose",
            "Plain YAML files, one per request. Yours to move, commit and diff. Courier never copies them elsewhere."),
    ];

    private static string SecretStoreDescription => OperatingSystem.IsWindows()
        ? "Windows Credential Manager"
        : "the platform credential store, or an encrypted local file where none exists";

    /// <summary>Creates the directories that must exist. Cheap, idempotent, safe to call at startup.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ResponseCache);
        Directory.CreateDirectory(SessionState);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(CapsuleInbox);
    }
}

/// <param name="Category">The heading shown on the storage settings page and in the statement.</param>
/// <param name="Path">An absolute path, or a description where there is no single path.</param>
/// <param name="Contents">Plain English. What is in there and why it is not a risk.</param>
public sealed record StorageLocation(string Category, string Path, string Contents);
