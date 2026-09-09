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

    [Fact]
    public void Postman_environment_variables_split_into_base_url_shared_and_secret()
    {
        Write("Local.postman_environment.json", """
            {
              "id": "abc-123",
              "name": "Local",
              "values": [
                { "key": "baseUrl", "value": "https://api.local.example.com", "enabled": true, "type": "default" },
                { "key": "region", "value": "us-east-1", "enabled": true, "type": "default" },
                { "key": "apiKey", "value": "hunter2", "enabled": true, "type": "secret" },
                { "key": "unused", "value": "nope", "enabled": false, "type": "default" }
              ],
              "_postman_variable_scope": "environment"
            }
            """);

        var local = Assert.Single(EnvironmentReader.Read(_root));

        Assert.Equal("Local", local.Name);
        Assert.Equal("https://api.local.example.com", local.BaseUrl);

        Assert.NotNull(local.Variables);
        var variables = local.Variables!;
        Assert.Equal("us-east-1", variables["region"]);
        Assert.DoesNotContain("baseUrl", variables.Keys);
        Assert.DoesNotContain("unused", variables.Keys);
        Assert.DoesNotContain("apiKey", variables.Keys);

        Assert.NotNull(local.SecretVariables);
        Assert.Equal("hunter2", local.SecretVariables!["apiKey"]);
    }

    [Fact]
    public void A_postman_type_secret_marker_is_honoured_even_when_the_value_looks_plain()
    {
        Write("Local.postman_environment.json", """
            {
              "name": "Local",
              "values": [
                { "key": "note", "value": "hello", "enabled": true, "type": "secret" }
              ]
            }
            """);

        var local = Assert.Single(EnvironmentReader.Read(_root));

        Assert.NotNull(local.SecretVariables);
        Assert.Equal("hello", local.SecretVariables!["note"]);
        Assert.Null(local.Variables);
    }

    [Fact]
    public void An_unmarked_but_secret_shaped_variable_is_still_detected()
    {
        Write("Local.postman_environment.json", """
            {
              "name": "Local",
              "values": [
                { "key": "password", "value": "whatever-it-is", "enabled": true, "type": "default" }
              ]
            }
            """);

        var local = Assert.Single(EnvironmentReader.Read(_root));

        Assert.NotNull(local.SecretVariables);
        Assert.Equal("whatever-it-is", local.SecretVariables!["password"]);
    }

    [Fact]
    public void Malformed_postman_environment_json_is_swallowed()
    {
        Write("Broken.postman_environment.json", "{ this is not json");

        Assert.Empty(EnvironmentReader.Read(_root));
    }

    [Fact]
    public void A_postman_environment_with_no_name_falls_back_to_the_filename()
    {
        Write("Staging.postman_environment.json", """
            {
              "values": [
                { "key": "region", "value": "eu-west-1", "enabled": true }
              ]
            }
            """);

        var staging = Assert.Single(EnvironmentReader.Read(_root));

        Assert.Equal("Staging", staging.Name);
    }

    [Fact]
    public void A_postman_environment_loses_a_name_collision_to_launch_settings()
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

        Write("Local.postman_environment.json", """
            {
              "name": "Local",
              "values": [
                { "key": "baseUrl", "value": "https://postman-exported.example.com", "enabled": true }
              ]
            }
            """);

        var local = Assert.Single(EnvironmentReader.Read(_root), e => e.Name == "Local");

        Assert.Equal("https://localhost:7211", local.BaseUrl);
        Assert.Null(local.Variables);
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
