namespace Courier.Core.Collections;

/// <summary>
/// Constants of the on-disk format. Versioned in frontmatter so the files outlive the tool (STOR-02).
/// </summary>
public static class CollectionFormat
{
    /// <summary>
    /// Bump only for a change that an older reader would misinterpret. Adding an optional field is
    /// not one of those.
    /// </summary>
    public const int Version = 1;

    public const string CollectionFileName = "collection.yaml";

    /// <summary>Machine-owned. Overwritten freely by the generator, committed to git. SCAN-09.</summary>
    public const string GeneratedFileName = "endpoints.generated.yaml";

    /// <summary>Human-owned. The generator never writes to this file. SCAN-09, P3.</summary>
    public const string OverlayFileName = "endpoints.overlay.yaml";

    public const string RequestsFolder = "requests";

    public const string EnvironmentsFolder = "environments";

    public const string RequestFileExtension = ".request.yaml";

    public const string EnvironmentFileExtension = ".env.yaml";

    public const string CapsuleExtension = ".capsule";

    /// <summary>Turns a request name into a file name that is stable, readable and diff-friendly.</summary>
    public static string FileNameFor(RequestDefinition request)
    {
        var slug = Slug(request.Name);
        return slug.Length == 0
            ? $"{request.Method.ToLowerInvariant()}{RequestFileExtension}"
            : $"{slug}{RequestFileExtension}";
    }

    public static string Slug(string text)
    {
        var chars = new List<char>(text.Length);
        var lastWasDash = true;

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                chars.Add(char.ToLowerInvariant(c));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                chars.Add('-');
                lastWasDash = true;
            }
        }

        while (chars.Count > 0 && chars[^1] == '-')
        {
            chars.RemoveAt(chars.Count - 1);
        }

        return new string([.. chars]);
    }
}

/// <summary>
/// Collection-level settings and defaults, stored in <c>collection.yaml</c> at the folder root.
/// </summary>
public sealed class CollectionDefinition
{
    public int Courier { get; set; } = CollectionFormat.Version;

    public string Name { get; set; } = "Collection";

    public string? Description { get; set; }

    /// <summary>Variables shared with everyone who has the folder. Never secrets. SEC-03.</summary>
    public Dictionary<string, string> Variables { get; set; } = [];

    /// <summary>Headers applied to every request unless overridden.</summary>
    public List<HeaderValue> Headers { get; set; } = [];

    /// <summary>Default auth profile name for requests that inherit.</summary>
    public string? AuthProfile { get; set; }

    /// <summary>Redirect, retry and timeout defaults. CORE-11.</summary>
    public RequestSettings Settings { get; set; } = new();

    /// <summary>Inject a W3C traceparent on outbound requests from this collection. TEL-01.</summary>
    public bool InjectTraceParent { get; set; }

    /// <summary>The solution or folder this collection was generated from, if any. SCAN-01.</summary>
    public ScanSource? ScannedFrom { get; set; }
}

/// <param name="Path">Absolute or collection-relative path to a .sln or a source folder.</param>
/// <param name="LastScannedUtc">Drives the "scanned 2s ago" line on the sync screen.</param>
/// <param name="UseSemanticAnalysis">
/// True once a solution has loaded successfully. Syntax-only is the default: it works on a folder
/// that does not even compile. SCAN-08.
/// </param>
public sealed record ScanSource(string Path, DateTimeOffset? LastScannedUtc = null, bool UseSemanticAnalysis = false)
{
    /// <summary>For the deserializer. See <see cref="QueryParameter"/>.</summary>
    public ScanSource()
        : this(string.Empty)
    {
    }
}
