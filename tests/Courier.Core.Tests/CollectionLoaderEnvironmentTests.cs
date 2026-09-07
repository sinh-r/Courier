using Courier.Core.Collections;

namespace Courier.Core.Tests;

/// <summary>
/// <see cref="CollectionLoader.SaveEnvironment"/> — the general-purpose write behind the GUI editor,
/// CORE-12. <see cref="CollectionFormatTests"/> already covers the YAML shape in memory; this covers
/// the actual file round trip: creating <c>environments/</c>, overwriting, and reading back.
/// </summary>
public sealed class CollectionLoaderEnvironmentTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("courier-env-tests-").FullName;

    [Fact]
    public void SaveEnvironment_creates_the_environments_folder_and_is_read_back_by_LoadEnvironment()
    {
        var environment = new EnvironmentDefinition
        {
            Name = "QA-Internal",
            Shared = { ["baseUrl"] = "https://qa.internal" },
            LocalNames = { "apiKey" },
        };

        CollectionLoader.SaveEnvironment(_folder, environment);

        Assert.True(Directory.Exists(Path.Combine(_folder, CollectionFormat.EnvironmentsFolder)));

        var restored = CollectionLoader.LoadEnvironment(_folder, "QA-Internal");

        Assert.NotNull(restored);
        Assert.Equal("https://qa.internal", restored!.Shared["baseUrl"]);
        Assert.Equal(["apiKey"], restored.LocalNames);
    }

    [Fact]
    public void SaveEnvironment_never_writes_a_local_value_to_disk()
    {
        // SaveEnvironment only ever receives an EnvironmentDefinition, which has no slot for a
        // local value in the first place — LocalNames is names-only (EnvironmentDefinition.cs:28).
        // This asserts that invariant holds all the way to the file on disk, not just in memory.
        var environment = new EnvironmentDefinition
        {
            Name = "QA-Internal",
            Shared = { ["baseUrl"] = "https://qa.internal" },
            LocalNames = { "apiKey" },
        };

        CollectionLoader.SaveEnvironment(_folder, environment);

        var path = Path.Combine(_folder, CollectionFormat.EnvironmentsFolder, "QA-Internal" + CollectionFormat.EnvironmentFileExtension);
        var yaml = File.ReadAllText(path);

        Assert.DoesNotContain("apiKey:", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveEnvironment_overwrites_an_existing_file()
    {
        CollectionLoader.SaveEnvironment(_folder, new EnvironmentDefinition
        {
            Name = "QA-Internal",
            Shared = { ["baseUrl"] = "https://old.example" },
        });

        CollectionLoader.SaveEnvironment(_folder, new EnvironmentDefinition
        {
            Name = "QA-Internal",
            Shared = { ["baseUrl"] = "https://new.example" },
        });

        var restored = CollectionLoader.LoadEnvironment(_folder, "QA-Internal");

        Assert.Equal("https://new.example", restored!.Shared["baseUrl"]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
