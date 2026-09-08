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

    /// <summary>
    /// Reads every launch profile. Two things a real <c>launchSettings.json</c> does that the
    /// naive "one environment per profile key" reading gets wrong:
    /// <list type="bullet">
    /// <item>The default Web API template ships an <c>http</c> and an <c>https</c> profile that
    /// launch the exact same app on the exact same port pair — two profile names for one logical
    /// target. Collapsing them keeps a scan from offering a developer two near-duplicate
    /// environments to choose between.</item>
    /// <item>An <c>IIS Express</c> profile has no <c>applicationUrl</c> of its own; its port lives
    /// under the file-level <c>iisSettings.iisExpress</c> block instead.</item>
    /// </list>
    /// A <c>Docker</c> or <c>Executable</c> profile is skipped: neither names a URL Courier can
    /// resolve without actually running it.
    /// </summary>
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

            var candidates = new List<(string ProfileName, string Url, string? EnvironmentVariable)>();
            string? iisEnvironmentVariable = null;
            var sawIisExpressProfile = false;

            foreach (var profile in profiles.EnumerateObject())
            {
                var commandName = profile.Value.TryGetProperty("commandName", out var name)
                    ? name.GetString()
                    : "Project";

                if (string.Equals(commandName, "IISExpress", StringComparison.OrdinalIgnoreCase))
                {
                    sawIisExpressProfile = true;
                    iisEnvironmentVariable ??= ReadAspNetCoreEnvironment(profile.Value);
                    continue;
                }

                if (!string.Equals(commandName, "Project", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!profile.Value.TryGetProperty("applicationUrl", out var urls))
                {
                    continue;
                }

                if (ChooseUrl(urls.GetString()) is { } chosen)
                {
                    candidates.Add((profile.Name, chosen, ReadAspNetCoreEnvironment(profile.Value)));
                }
            }

            // A profile with no ASPNETCORE_ENVIRONMENT, or one whose value nothing else shares,
            // keeps its own name. Only a value shared by more than one profile — the http/https
            // pair — collapses into a single environment named after that shared value.
            foreach (var solo in candidates.Where(c => c.EnvironmentVariable is null))
            {
                environments.Add(new DerivedEnvironment(solo.ProfileName, solo.Url, $"from {Path.GetFileName(file)} ({solo.ProfileName} profile)"));
            }

            foreach (var group in candidates
                .Where(c => c.EnvironmentVariable is not null)
                .GroupBy(c => c.EnvironmentVariable!, StringComparer.OrdinalIgnoreCase))
            {
                var members = group.OrderByDescending(c => c.Url.StartsWith("https", StringComparison.OrdinalIgnoreCase)).ToList();
                var name = members.Count > 1 ? group.Key : members[0].ProfileName;
                var profileNames = string.Join(", ", members.Select(m => m.ProfileName));

                environments.Add(new DerivedEnvironment(name, members[0].Url, $"from {Path.GetFileName(file)} ({profileNames} profile{(members.Count > 1 ? "s" : string.Empty)})"));
            }

            if (sawIisExpressProfile && ReadIisExpressUrl(document.RootElement) is { } iisUrl)
            {
                var iisName = iisEnvironmentVariable is null ? "IIS Express" : $"{iisEnvironmentVariable} (IIS Express)";
                environments.Add(new DerivedEnvironment(iisName, iisUrl, $"from {Path.GetFileName(file)} (IIS Express)"));
            }
        }
    }

    /// <summary>"https://localhost:7043;http://localhost:5043" — https first, because a client
    /// certificate or an Entra token will not travel over the plaintext one.</summary>
    private static string? ChooseUrl(string? raw) =>
        (raw ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderByDescending(u => u.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            .Select(u => u.TrimEnd('/'))
            .FirstOrDefault();

    private static string? ReadAspNetCoreEnvironment(JsonElement profile) =>
        profile.TryGetProperty("environmentVariables", out var vars)
            && vars.TryGetProperty("ASPNETCORE_ENVIRONMENT", out var env)
            && env.ValueKind == JsonValueKind.String
            ? env.GetString()
            : null;

    /// <summary>
    /// IIS Express has no per-profile <c>applicationUrl</c>; its port is file-level, under
    /// <c>iisSettings.iisExpress</c>. <c>sslPort</c> wins when set, for the same reason https wins
    /// among a Project profile's semicolon-separated URLs.
    /// </summary>
    private static string? ReadIisExpressUrl(JsonElement root)
    {
        if (!root.TryGetProperty("iisSettings", out var iis) || !iis.TryGetProperty("iisExpress", out var iisExpress))
        {
            return null;
        }

        if (iisExpress.TryGetProperty("sslPort", out var sslPort)
            && sslPort.ValueKind == JsonValueKind.Number
            && sslPort.GetInt32() > 0)
        {
            return $"https://localhost:{sslPort.GetInt32()}";
        }

        return iisExpress.TryGetProperty("applicationUrl", out var url) && url.ValueKind == JsonValueKind.String
            ? url.GetString()?.TrimEnd('/')
            : null;
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
