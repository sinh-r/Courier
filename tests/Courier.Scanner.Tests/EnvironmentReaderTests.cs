namespace Courier.Scanner.Tests;

/// <summary>
/// SCAN-07 against the launch-profile shapes real projects actually ship, not just the tidy
/// single-profile fixtures under <c>samples/</c>.
/// </summary>
public sealed class EnvironmentReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("courier-env-reader-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Http_and_https_profiles_sharing_an_environment_collapse_into_one()
    {
        Write("launchSettings.json", RealisticWebApiTemplate);

        var environments = EnvironmentReader.Read(_root);

        // The template's "http" and "https" profiles both set ASPNETCORE_ENVIRONMENT=Development,
        // so they describe one logical target — not two environments to choose between.
        var development = Assert.Single(environments, e => e.Name == "Development");
        Assert.Equal("https://localhost:7062", development.BaseUrl);

        Assert.DoesNotContain(environments, e => e.Name == "http");
        Assert.DoesNotContain(environments, e => e.Name == "https");
    }

    [Fact]
    public void Iis_express_reads_its_url_from_the_file_level_iis_settings_block()
    {
        Write("launchSettings.json", RealisticWebApiTemplate);

        var environments = EnvironmentReader.Read(_root);

        // IIS Express has no per-profile applicationUrl; sslPort under iisSettings.iisExpress wins
        // over the plaintext applicationUrl there, same as https wins among a Project profile's
        // semicolon-separated URLs.
        var iis = Assert.Single(environments, e => e.Name.Contains("IIS Express", StringComparison.Ordinal));
        Assert.Equal("https://localhost:44321", iis.BaseUrl);
    }

    [Fact]
    public void Docker_profiles_are_skipped()
    {
        Write("launchSettings.json", RealisticWebApiTemplate);

        var environments = EnvironmentReader.Read(_root);

        Assert.DoesNotContain(environments, e => e.Name.Contains("Docker", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_profile_with_no_shared_environment_variable_keeps_its_own_name()
    {
        // Orders.Api's shape: distinct ASPNETCORE_ENVIRONMENT per profile, so nothing collapses
        // and the profile name is what the user picked deliberately.
        Write("launchSettings.json", """
            {
              "profiles": {
                "Local": {
                  "commandName": "Project",
                  "applicationUrl": "https://localhost:7043;http://localhost:5043",
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development" }
                },
                "QA-Internal": {
                  "commandName": "Project",
                  "applicationUrl": "https://qa.internal/orders",
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "QA" }
                }
              }
            }
            """);

        var environments = EnvironmentReader.Read(_root);

        Assert.Contains(environments, e => e.Name == "Local");
        Assert.Contains(environments, e => e.Name == "QA-Internal");
        Assert.DoesNotContain(environments, e => e.Name == "Development");
    }

    [Fact]
    public void A_profile_with_no_environment_variable_at_all_keeps_its_own_name()
    {
        Write("launchSettings.json", """
            {
              "profiles": {
                "Local": {
                  "commandName": "Project",
                  "applicationUrl": "https://localhost:7211;http://localhost:5211"
                }
              }
            }
            """);

        var environments = EnvironmentReader.Read(_root);

        var local = Assert.Single(environments);
        Assert.Equal("Local", local.Name);
        Assert.Equal("https://localhost:7211", local.BaseUrl);
    }

    private void Write(string fileName, string content) =>
        File.WriteAllText(Path.Combine(_root, fileName), content);

    private const string RealisticWebApiTemplate = """
        {
          "$schema": "https://json.schemastore.org/launchsettings.json",
          "iisSettings": {
            "windowsAuthentication": false,
            "anonymousAuthentication": true,
            "iisExpress": {
              "applicationUrl": "http://localhost:12345",
              "sslPort": 44321
            }
          },
          "profiles": {
            "http": {
              "commandName": "Project",
              "dotnetRunMessages": true,
              "launchBrowser": true,
              "launchUrl": "swagger",
              "applicationUrl": "http://localhost:5062",
              "environmentVariables": {
                "ASPNETCORE_ENVIRONMENT": "Development"
              }
            },
            "https": {
              "commandName": "Project",
              "applicationUrl": "https://localhost:7062;http://localhost:5062",
              "environmentVariables": {
                "ASPNETCORE_ENVIRONMENT": "Development"
              }
            },
            "IIS Express": {
              "commandName": "IISExpress",
              "launchBrowser": true,
              "environmentVariables": {
                "ASPNETCORE_ENVIRONMENT": "Development"
              }
            },
            "Docker": {
              "commandName": "Docker",
              "launchUrl": "{Scheme}://{ServiceHost}:{ServicePort}/swagger"
            }
          }
        }
        """;
}
