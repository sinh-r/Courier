using System.Text.Json.Serialization;
using Courier.Core.Collections;

namespace Courier.App.ViewModels;

/// <summary>
/// Everything about a tab that survives suspension, session restore and a crash.
/// </summary>
/// <remarks>
/// <para>
/// A plain serializable POCO, never a control tree. PERF-02 allows 900MB with 100 tabs open and
/// PERF-03 allows a 50ms switch at that count, and both are only reachable if an inactive tab costs
/// roughly what its text costs. TECH_SPEC 4 is blunt that this "cannot be retrofitted", so it is
/// the shape from the first commit.
/// </para>
/// <para>
/// NFR-07's crash recovery falls out of the same design: the state that makes a tab cheap to
/// suspend is exactly the state that makes it recoverable.
/// </para>
/// </remarks>
public sealed class TabState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Title { get; set; } = "Untitled";

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    /// <summary>Path to the request file, when this tab is backed by one.</summary>
    public string? RequestPath { get; set; }

    /// <summary>Stable endpoint identity, when this tab came from a generated collection.</summary>
    public string? EndpointId { get; set; }

    public string? CollectionName { get; set; }

    public string? EnvironmentName { get; set; }

    /// <summary>Unsaved edits show as a filled dot in place of the close affordance. UI_SPEC 4.2.</summary>
    public bool IsDirty { get; set; }

    public Provenance Provenance { get; set; } = Provenance.Authored;

    public string? BodyText { get; set; }

    public BodyKind BodyKind { get; set; } = BodyKind.None;

    public List<HeaderValue> Headers { get; set; } = [];

    public List<QueryParameter> Query { get; set; } = [];

    public Dictionary<string, string> PathParams { get; set; } = [];

    public string? AuthProfile { get; set; }

    public string? PreRequestScript { get; set; }

    public string? PostResponseScript { get; set; }

    public List<AssertionDefinition> Assertions { get; set; } = [];

    public RequestSettings Settings { get; set; } = new();

    public List<string> RequiredScopes { get; set; } = [];

    /// <summary>Caret position, restored so switching tabs does not lose the user's place.</summary>
    public int CaretOffset { get; set; }

    public int ActiveRequestTabIndex { get; set; }

    public int ActiveResponseTabIndex { get; set; }

    /// <summary>Set when this tab came from an imported capsule. Drives the banner. CAP-06.</summary>
    public CapsuleOrigin? Capsule { get; set; }

    /// <summary>Last touched, for the suspension policy. Not shown anywhere.</summary>
    [JsonIgnore]
    public DateTimeOffset LastActive { get; set; } = DateTimeOffset.UtcNow;

    public RequestDefinition ToDefinition() => new()
    {
        Id = EndpointId,
        Name = Title,
        Method = Method,
        Url = Url,
        Provenance = Provenance,
        Headers = [.. Headers],
        Query = [.. Query],
        PathParams = new Dictionary<string, string>(PathParams),
        Body = BodyKind == BodyKind.None ? null : new RequestBody { Kind = BodyKind, Text = BodyText },
        Auth = AuthProfile is null ? null : new AuthReference(AuthProfile),
        Scripts = PreRequestScript is null && PostResponseScript is null
            ? null
            : new RequestScripts(PreRequestScript, PostResponseScript),
        Assertions = [.. Assertions],
        Settings = Settings,
        RequiredScopes = [.. RequiredScopes],
    };

    public static TabState FromDefinition(RequestDefinition request, string? collectionName, string? environmentName) => new()
    {
        Title = request.Name,
        Method = request.Method,
        Url = request.Url,
        EndpointId = request.Id,
        CollectionName = collectionName,
        EnvironmentName = environmentName,
        Provenance = request.Provenance,
        BodyText = request.Body?.Text,
        BodyKind = request.Body?.Kind ?? BodyKind.None,
        Headers = [.. request.Headers],
        Query = [.. request.Query],
        PathParams = new Dictionary<string, string>(request.PathParams),
        AuthProfile = request.Auth?.Profile,
        PreRequestScript = request.Scripts?.PreRequest,
        PostResponseScript = request.Scripts?.PostResponse,
        Assertions = [.. request.Assertions],
        Settings = request.Settings,
        RequiredScopes = [.. request.RequiredScopes],
    };
}

/// <param name="Unresolved">Placeholders with no local value. Shown on the banner. CAP-06.</param>
public sealed record CapsuleOrigin(
    string FileName,
    string? ExportedBy,
    DateTimeOffset ImportedUtc,
    IReadOnlyList<string> Resolved,
    IReadOnlyList<string> Unresolved);
