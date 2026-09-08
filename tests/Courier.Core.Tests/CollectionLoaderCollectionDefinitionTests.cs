using Courier.Core.Collections;

namespace Courier.Core.Tests;

/// <summary>
/// <see cref="CollectionLoader.SaveCollectionDefinition"/> — the app's own write path for
/// <c>collection.yaml</c>, distinct from <c>Courier.Scanner.CollectionWriter.WriteCollectionDefinition</c>'s
/// "never overwrite": this one is the human's own edit (declaring a folder, today), made through
/// the app, so unlike the scanner's writer it must actually overwrite.
/// </summary>
public sealed class CollectionLoaderCollectionDefinitionTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("courier-collectiondef-tests-").FullName;

    [Fact]
    public void SaveCollectionDefinition_is_read_back_by_Load()
    {
        CollectionLoader.SaveCollectionDefinition(_folder, new CollectionDefinition
        {
            Name = "Orders.Api",
            Folders = ["Articles"],
        });

        var loaded = CollectionLoader.Load(_folder);

        Assert.Equal("Orders.Api", loaded.Definition.Name);
        Assert.Equal(["Articles"], loaded.Definition.Folders);
    }

    [Fact]
    public void SaveCollectionDefinition_overwrites_an_existing_file_unlike_the_scanners_writer()
    {
        CollectionLoader.SaveCollectionDefinition(_folder, new CollectionDefinition { Name = "Orders.Api" });
        CollectionLoader.SaveCollectionDefinition(_folder, new CollectionDefinition
        {
            Name = "Orders.Api",
            Folders = ["Articles", "Comments"],
        });

        var loaded = CollectionLoader.Load(_folder);

        Assert.Equal(["Articles", "Comments"], loaded.Definition.Folders);
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
