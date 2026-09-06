using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Courier.Core.Collections;

/// <summary>
/// Reads and writes the on-disk format. YAML, one file per request, in the user's chosen folder.
/// STOR-01, STOR-02.
/// </summary>
/// <remarks>
/// Deliberately not a custom DSL (TECH_SPEC 3.6). The settings here exist to make git diffs
/// readable: camelCase keys, defaults omitted, literal block scalars for multi-line bodies so a
/// JSON payload appears in the file as the JSON the user typed rather than as an escaped string.
/// </remarks>
public sealed class CollectionSerializer
{
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public CollectionSerializer()
    {
        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(
                DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .WithIndentedSequences()

            // A timestamp must be one ISO-8601 scalar. Without this YamlDotNet writes every
            // property of the DateTimeOffset struct — twenty lines of Ticks and DayOfWeek — which
            // is neither readable nor parseable by anything else, and STOR-02 asks for both.
            .WithTypeConverter(new Iso8601DateTimeOffsetConverter())

            // No anchors and aliases. YamlDotNet emits "&o0 {}" and "*o0" when two properties
            // happen to share a reference, which is valid YAML and unreadable in a diff — the file
            // suddenly refers to a value defined forty lines earlier for no reason the user can see.
            .DisableAliases()
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new Iso8601DateTimeOffsetConverter())
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public string SerializeRequest(RequestDefinition request) => WithBanner(_serializer.Serialize(request));

    public RequestDefinition DeserializeRequest(string yaml)
    {
        var request = _deserializer.Deserialize<RequestDefinition>(yaml)
            ?? throw new CollectionFormatException("The file is empty or contains no request.");

        AssertReadableVersion(request.Courier);
        return request;
    }

    public string SerializeCollection(CollectionDefinition collection) =>
        WithBanner(_serializer.Serialize(collection));

    public CollectionDefinition DeserializeCollection(string yaml)
    {
        var collection = _deserializer.Deserialize<CollectionDefinition>(yaml)
            ?? throw new CollectionFormatException("The file is empty or contains no collection.");

        AssertReadableVersion(collection.Courier);
        return collection;
    }

    public string SerializeEnvironment(EnvironmentDefinition environment) =>
        WithBanner(_serializer.Serialize(environment));

    public EnvironmentDefinition DeserializeEnvironment(string yaml)
    {
        var environment = _deserializer.Deserialize<EnvironmentDefinition>(yaml)
            ?? throw new CollectionFormatException("The file is empty or contains no environment.");

        AssertReadableVersion(environment.Courier);
        return environment;
    }

    /// <summary>The machine-owned generated file. Overwritten wholesale; never merged. SCAN-09.</summary>
    public string SerializeGenerated(GeneratedEndpointSet set) => WithBanner(
        _serializer.Serialize(set),
        "This file is written by Courier from your source code. Your edits belong in "
        + CollectionFormat.OverlayFileName + ", which Courier never overwrites.");

    public GeneratedEndpointSet DeserializeGenerated(string yaml)
    {
        var set = _deserializer.Deserialize<GeneratedEndpointSet>(yaml)
            ?? throw new CollectionFormatException("The file is empty or contains no endpoints.");

        AssertReadableVersion(set.Courier);
        return set;
    }

    /// <summary>The human-owned overlay. Courier reads it and joins by id; it never rewrites it.</summary>
    public string SerializeOverlay(OverlaySet overlay) => WithBanner(
        _serializer.Serialize(overlay),
        "Yours. Courier reads this and never overwrites it, so regenerating cannot lose your work.");

    public OverlaySet DeserializeOverlay(string yaml)
    {
        var overlay = _deserializer.Deserialize<OverlaySet>(yaml)
            ?? throw new CollectionFormatException("The file is empty or contains no overlay entries.");

        AssertReadableVersion(overlay.Courier);
        return overlay;
    }

    private static string WithBanner(string yaml, string? note = null)
    {
        var banner = note is null
            ? $"# Courier collection format v{CollectionFormat.Version}. Plain YAML: readable and editable without Courier.\n"
            : $"# Courier collection format v{CollectionFormat.Version}.\n# {note}\n";

        return banner + yaml;
    }

    private static void AssertReadableVersion(int version)
    {
        if (version > CollectionFormat.Version)
        {
            throw new CollectionFormatException(
                $"This file is format version {version} and this build of Courier reads version "
                + $"{CollectionFormat.Version}. Update Courier, or open the file in a text editor — "
                + "it is plain YAML and nothing in it is locked away.");
        }
    }
}

public sealed class CollectionFormatException(string message) : Exception(message);
