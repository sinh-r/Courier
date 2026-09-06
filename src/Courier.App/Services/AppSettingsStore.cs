using System.Text.Json;
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
}
