using System.Text.Json;
using Courier.Core.Collections;
using Courier.Core.Storage;

namespace Courier.App.Services;

/// <summary>
/// The one local preferences file (theme today; window layout later). Written at
/// <see cref="StorageLocations.Settings"/>, which SEC-05 already lists and already promises "never
/// holds a credential" — this keeps that promise true rather than adding a new store.
/// </summary>
public static class AppSettingsStore
{
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(StorageLocations.Settings))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(StorageLocations.Settings);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(StorageLocations.Root);
            File.WriteAllText(StorageLocations.Settings, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a preference write is not worth taking the app down for.
        }
    }
}

public sealed class AppSettings
{
    /// <summary>"Light", "Dark" or "System".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Applied to every outbound request, under the collection's own headers and the request's own,
    /// which each win by name. Courier sent neither an <c>Accept</c> nor a <c>User-Agent</c> before
    /// this existed — some WAF-fronted corporate APIs challenge or block a request with no
    /// <c>User-Agent</c> at all, which looked like a Courier bug rather than a missing header.
    /// </summary>
    public List<HeaderValue> DefaultHeaders { get; set; } =
    [
        new HeaderValue("Accept", "*/*"),
        new HeaderValue("User-Agent", $"Courier/{Version}"),
    ];

    private static string Version { get; } =
        typeof(AppSettings).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
}
