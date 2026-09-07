namespace Courier.Core.Collections;

/// <summary>
/// Loads a collection folder: <c>collection.yaml</c>, the generated/overlay join, and hand-authored
/// request files. STOR-01.
/// </summary>
/// <remarks>
/// Originally private to <c>Courier.Cli</c>'s <c>run</c> command, whose own doc comment claimed it
/// "loads a collection folder the same way the app does" — which was false, since the app had its
/// own, divergent loader that read neither <c>collection.yaml</c> nor an environment file. Promoted
/// here so both actually do.
/// </remarks>
public static class CollectionLoader
{
    public static LoadedCollection Load(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new DirectoryNotFoundException($"'{folder}' does not exist.");
        }

        var serializer = new CollectionSerializer();

        var definitionPath = Path.Combine(folder, CollectionFormat.CollectionFileName);
        var definition = File.Exists(definitionPath)
            ? serializer.DeserializeCollection(File.ReadAllText(definitionPath))
            : new CollectionDefinition { Name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) };

        var requests = new List<RequestDefinition>();

        // The generated set joined with the user's overlay, in memory. SCAN-09, P3: the two files
        // are never merged on disk, so regenerating cannot lose an edit.
        var generatedPath = Path.Combine(folder, CollectionFormat.GeneratedFileName);
        if (File.Exists(generatedPath))
        {
            var generated = serializer.DeserializeGenerated(File.ReadAllText(generatedPath));

            var overlayPath = Path.Combine(folder, CollectionFormat.OverlayFileName);
            var overlay = File.Exists(overlayPath)
                ? serializer.DeserializeOverlay(File.ReadAllText(overlayPath))
                : new OverlaySet();

            requests.AddRange(GeneratedOverlayJoiner.Join(generated, overlay));
        }

        var requestsFolder = Path.Combine(folder, CollectionFormat.RequestsFolder);
        if (Directory.Exists(requestsFolder))
        {
            foreach (var file in Directory
                .EnumerateFiles(requestsFolder, $"*{CollectionFormat.RequestFileExtension}", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var request = serializer.DeserializeRequest(File.ReadAllText(file));

                    request.Folder ??= Path.GetRelativePath(requestsFolder, Path.GetDirectoryName(file)!)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .TrimStart('.');

                    requests.Add(request);
                }
                catch (CollectionFormatException)
                {
                    // A file Courier cannot read is listed as unreadable rather than dropped, for
                    // the same reason an unresolvable endpoint is: silence is the hostile outcome.
                    requests.Add(new RequestDefinition
                    {
                        Name = Path.GetFileNameWithoutExtension(file),
                        UnresolvedNotes = ["This file could not be read as a Courier request."],
                    });
                }
            }
        }

        return new LoadedCollection(definition, requests);
    }

    public static EnvironmentDefinition? LoadEnvironment(string folder, string name)
    {
        var path = EnvironmentPath(folder, name);

        return File.Exists(path)
            ? new CollectionSerializer().DeserializeEnvironment(File.ReadAllText(path))
            : null;
    }

    /// <summary>Environment names available in this collection, for a picker. Sorted, no extension.</summary>
    public static IReadOnlyList<string> ListEnvironments(string folder)
    {
        var directory = Path.Combine(folder, CollectionFormat.EnvironmentsFolder);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(directory, $"*{CollectionFormat.EnvironmentFileExtension}")
                .Select(f => Path.GetFileName(f)[..^CollectionFormat.EnvironmentFileExtension.Length])
                .OrderBy(name => name, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Writes an environment file, creating <c>environments/</c> if needed and overwriting any
    /// existing file for the same name. Unlike <see cref="Courier.Scanner.CollectionWriter"/>'s
    /// scan-derived write, this is the general-purpose save behind the GUI editor: CORE-12.
    /// </summary>
    public static void SaveEnvironment(string folder, EnvironmentDefinition environment)
    {
        var directory = Path.Combine(folder, CollectionFormat.EnvironmentsFolder);
        Directory.CreateDirectory(directory);

        var path = EnvironmentPath(folder, environment.Name);
        File.WriteAllText(path, new CollectionSerializer().SerializeEnvironment(environment));
    }

    private static string EnvironmentPath(string folder, string name) => Path.Combine(
        folder,
        CollectionFormat.EnvironmentsFolder,
        $"{name}{CollectionFormat.EnvironmentFileExtension}");
}

public sealed record LoadedCollection(CollectionDefinition Definition, IReadOnlyList<RequestDefinition> Requests);
