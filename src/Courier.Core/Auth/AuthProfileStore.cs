using Courier.Core.Collections;

namespace Courier.Core.Auth;

/// <summary>
/// Reads and writes saved auth profiles: <c>auth/&lt;name&gt;.auth.yaml</c>, committed and shared
/// with the team. The profile never carries a secret — that lives in the credential store, keyed by
/// <see cref="AuthProfile.SecretRef"/>, which is what makes committing this file safe.
/// </summary>
/// <remarks>
/// Same shape as <see cref="CollectionLoader"/>'s environment methods: one file per name, the
/// folder created on first write, a case-sensitive name equal to the file's base name.
/// </remarks>
public static class AuthProfileStore
{
    public static IReadOnlyList<string> ListNames(string collectionFolder)
    {
        var directory = Path.Combine(collectionFolder, CollectionFormat.AuthFolder);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(directory, $"*{CollectionFormat.AuthFileExtension}")
            .Select(f => Path.GetFileName(f)[..^CollectionFormat.AuthFileExtension.Length])
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    public static IReadOnlyList<AuthProfile> LoadAll(string collectionFolder) =>
        [.. ListNames(collectionFolder).Select(name => Load(collectionFolder, name)).OfType<AuthProfile>()];

    /// <summary>Null when no profile has this name — a dangling reference, not a load failure.</summary>
    public static AuthProfile? Load(string collectionFolder, string name)
    {
        var path = ProfilePath(collectionFolder, name);
        if (!File.Exists(path))
        {
            return null;
        }

        var profile = new CollectionSerializer().DeserializeAuthProfile(File.ReadAllText(path));

        // The file name is authoritative — it is what every AuthReference.Profile points at — so a
        // profile renamed by editing the YAML by hand still resolves under its old file name.
        profile.Name = name;
        return profile;
    }

    public static void Save(string collectionFolder, AuthProfile profile)
    {
        var directory = Path.Combine(collectionFolder, CollectionFormat.AuthFolder);
        Directory.CreateDirectory(directory);

        File.WriteAllText(ProfilePath(collectionFolder, profile.Name), new CollectionSerializer().SerializeAuthProfile(profile));
    }

    /// <summary>Renames a profile's file. The secret is untouched — it is keyed by <see cref="AuthProfile.SecretRef"/>, not by name.</summary>
    public static void Rename(string collectionFolder, string oldName, string newName)
    {
        var oldPath = ProfilePath(collectionFolder, oldName);
        var newPath = ProfilePath(collectionFolder, newName);

        if (File.Exists(oldPath))
        {
            File.Move(oldPath, newPath, overwrite: false);
        }
    }

    public static void Delete(string collectionFolder, string name)
    {
        var path = ProfilePath(collectionFolder, name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string ProfilePath(string collectionFolder, string name) => Path.Combine(
        collectionFolder,
        CollectionFormat.AuthFolder,
        $"{name}{CollectionFormat.AuthFileExtension}");
}
