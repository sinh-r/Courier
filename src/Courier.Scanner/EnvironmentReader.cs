using System.Text.Json;

namespace Courier.Scanner;

/// <summary>
/// Derives environments and base URLs from <c>launchSettings.json</c> and <c>appsettings.*.json</c>.
/// SCAN-07.
/// </summary>
/// <remarks>
/// This is small but disproportionately valuable: it is the difference between a generated
/// collection whose first request works and one where every URL is a placeholder the user has to
/// fill in before they can try anything.
/// </remarks>
public static class EnvironmentReader
{
    public static IReadOnlyList<DerivedEnvironment> Read(string root)
    {
        var environments = new List<DerivedEnvironment>();

        foreach (var file in Find(root, "launchSettings.json"))
        {
            ReadLaunchSettings(file, environments);
        }

        foreach (var file in Find(root, "appsettings.*.json").Concat(Find(root, "appsettings.json")))
        {
            ReadAppSettings(file, environments);
        }

        // First writer wins, so a launch profile beats an appsettings guess for the same name.
        return [.. environments
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static void ReadLaunchSettings(string file, List<DerivedEnvironment> environments)
    {
        if (Parse(file) is not { } document)
        {
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("profiles", out var profiles))
            {
                return;
            }

            foreach (var profile in profiles.EnumerateObject())
            {
                if (!profile.Value.TryGetProperty("applicationUrl", out var urls))
                {
                    continue;
                }

                // "https://localhost:7043;http://localhost:5043" — https first, because a client
                // certificate or an Entra token will not travel over the plaintext one.
                var chosen = (urls.GetString() ?? string.Empty)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .OrderByDescending(u => u.StartsWith("https", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                if (chosen is not null)
                {
                    environments.Add(new DerivedEnvironment(
                        profile.Name,
                        chosen.TrimEnd('/'),
                        $"from {Path.GetFileName(file)}"));
                }
            }
        }
    }

    /// <summary>
    /// Looks for a base URL in the shapes teams actually use. Anything found here is a shared
    /// value: SEC-03 keeps secrets out of the committed column, and a base URL is not a secret.
    /// </summary>
    private static void ReadAppSettings(string file, List<DerivedEnvironment> environments)
    {
        if (Parse(file) is not { } document)
        {
            return;
        }

        using (document)
        {
            var name = EnvironmentNameFrom(Path.GetFileName(file));

            foreach (var key in new[] { "BaseUrl", "BaseAddress", "ApiBaseUrl", "ApiUrl" })
            {
                if (document.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri))
                {
                    environments.Add(new DerivedEnvironment(
                        name,
                        uri.ToString().TrimEnd('/'),
                        $"from {Path.GetFileName(file)}"));

                    return;
                }
            }
        }
    }

    /// <summary>appsettings.Staging.json becomes "Staging"; appsettings.json becomes "Default".</summary>
    private static string EnvironmentNameFrom(string fileName)
    {
        var parts = fileName.Split('.');
        return parts.Length >= 3 ? parts[1] : "Default";
    }

    private static JsonDocument? Parse(string file)
    {
        try
        {
            return JsonDocument.Parse(
                File.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A malformed appsettings is the application's problem, not a reason to fail the scan.
            return null;
        }
    }

    private static IEnumerable<string> Find(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        })
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
}
