namespace Courier.Core.Collections;

/// <summary>
/// One request, as stored in one file. STOR-01.
/// </summary>
/// <remarks>
/// This is the model the whole application is built on, so it is deliberately plain: mutable
/// properties, no behaviour, no framework attributes. The YAML on disk is a direct projection of
/// it, which is what keeps the files readable without this tool (STOR-02).
/// </remarks>
public sealed class RequestDefinition
{
    /// <summary>File format version, written into every file so the format can outlive the tool.</summary>
    public int Courier { get; set; } = CollectionFormat.Version;

    /// <summary>
    /// Stable endpoint identity for a generated request, absent for a hand-authored one. This is
    /// the join key between generated and overlay content. SCAN-09.
    /// </summary>
    public string? Id { get; set; }

    public string Name { get; set; } = "Untitled request";

    public string Method { get; set; } = "GET";

    /// <summary>May contain variable references; resolution happens at send time. CORE-04.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Where this request came from, shown as a tree glyph. SCAN-12.</summary>
    public Provenance Provenance { get; set; } = Provenance.Authored;

    /// <summary>Folder path within the collection, forward-slashed. Drives the tree.</summary>
    public string? Folder { get; set; }

    public string? Description { get; set; }

    /// <summary>Route parameters, keyed by template token name.</summary>
    public Dictionary<string, string> PathParams { get; set; } = [];

    public List<QueryParameter> Query { get; set; } = [];

    public List<HeaderValue> Headers { get; set; } = [];

    public RequestBody? Body { get; set; }

    public AuthReference? Auth { get; set; }

    public RequestScripts? Scripts { get; set; }

    /// <summary>Declarative assertions, evaluated without scripting. TEST-03.</summary>
    public List<AssertionDefinition> Assertions { get; set; } = [];

    /// <summary>Redirect, retry and timeout, inheritable from the collection. CORE-11.</summary>
    public RequestSettings Settings { get; set; } = new();

    /// <summary>Scopes the endpoint declares it needs, surfaced before the send. SCAN-06.</summary>
    public List<string> RequiredScopes { get; set; } = [];

    /// <summary>
    /// Populated by the scanner when an endpoint could not be fully derived. An endpoint that
    /// cannot be resolved is listed with its reason, never silently omitted (REQUIREMENTS 9).
    /// </summary>
    public List<string> UnresolvedNotes { get; set; } = [];

    public RequestDefinition Clone() => new()
    {
        Courier = Courier,
        Id = Id,
        Name = Name,
        Method = Method,
        Url = Url,
        Provenance = Provenance,
        Folder = Folder,
        Description = Description,
        PathParams = new Dictionary<string, string>(PathParams),
        Query = [.. Query.Select(q => q with { })],
        Headers = [.. Headers.Select(h => h with { })],
        Body = Body?.Clone(),
        Auth = Auth is null ? null : Auth with { },
        Scripts = Scripts is null ? null : Scripts with { },
        Assertions = [.. Assertions.Select(a => a with { })],
        Settings = Settings with { },
        RequiredScopes = [.. RequiredScopes],
        UnresolvedNotes = [.. UnresolvedNotes],
    };
}

/// <summary>
/// Three states the tree distinguishes with a glyph: generated, generated-then-edited, hand-written.
/// SCAN-12, and the "from code / edited / yours" legend in the mock.
/// </summary>
public enum Provenance
{
    /// <summary>The user made this by hand. Glyph: hollow circle.</summary>
    Authored,

    /// <summary>Derived from source and untouched since. Glyph: filled square.</summary>
    Generated,

    /// <summary>Derived from source, then edited. Glyph: half-filled square.</summary>
    GeneratedEdited,
}

/// <param name="Enabled">Unchecked rows stay in the file so toggling one is not destructive.</param>
public sealed record QueryParameter(string Name, string Value, bool Enabled = true, string? Description = null)
{
    /// <summary>For the deserializer. A positional record has no parameterless constructor, and
    /// without one the format can be written but never read back.</summary>
    public QueryParameter()
        : this(string.Empty, string.Empty)
    {
    }
}

public sealed record HeaderValue(string Name, string Value, bool Enabled = true, string? Description = null)
{
    /// <summary>For the deserializer. See <see cref="QueryParameter"/>.</summary>
    public HeaderValue()
        : this(string.Empty, string.Empty)
    {
    }
}

/// <param name="Profile">Names an auth profile; the profile holds the configuration, never a secret.</param>
public sealed record AuthReference(string? Profile, bool InheritFromCollection = true)
{
    /// <summary>For the deserializer. See <see cref="QueryParameter"/>.</summary>
    public AuthReference()
        : this((string?)null)
    {
    }
}

public sealed record RequestScripts(string? PreRequest = null, string? PostResponse = null)
{
    /// <summary>For the deserializer. See <see cref="QueryParameter"/>.</summary>
    public RequestScripts()
        : this(null, null)
    {
    }
}

/// <summary>Per-request transport policy, inherited from the collection when unset. CORE-11.</summary>
public sealed record RequestSettings
{
    public bool? FollowRedirects { get; init; }

    public int? MaxRedirects { get; init; }

    public int? TimeoutMilliseconds { get; init; }

    public int? Retries { get; init; }

    /// <summary>"1.1", "2.0" or "3.0". Null lets the handler negotiate. CORE-01.</summary>
    public string? HttpVersion { get; init; }

    /// <summary>Thumbprint of a client certificate for this request. ENT-08.</summary>
    public string? ClientCertificateThumbprint { get; init; }

    public bool? UseIntegratedAuth { get; init; }

    /// <summary>Merges an inherited setting underneath this one. Request wins, collection fills gaps.</summary>
    public RequestSettings InheritFrom(RequestSettings parent) => new()
    {
        FollowRedirects = FollowRedirects ?? parent.FollowRedirects,
        MaxRedirects = MaxRedirects ?? parent.MaxRedirects,
        TimeoutMilliseconds = TimeoutMilliseconds ?? parent.TimeoutMilliseconds,
        Retries = Retries ?? parent.Retries,
        HttpVersion = HttpVersion ?? parent.HttpVersion,
        ClientCertificateThumbprint = ClientCertificateThumbprint ?? parent.ClientCertificateThumbprint,
        UseIntegratedAuth = UseIntegratedAuth ?? parent.UseIntegratedAuth,
    };

    public static readonly RequestSettings Defaults = new()
    {
        FollowRedirects = true,
        MaxRedirects = 10,
        TimeoutMilliseconds = 30_000,
        Retries = 0,
        UseIntegratedAuth = false,
    };
}
