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

    /// <summary>Overrides the content type <see cref="BodyKind"/> would otherwise imply — set by
    /// the scanner for a generated JSON sample, or by hand for anything else. Round-tripped through
    /// <see cref="ToDefinition"/>/<see cref="FromDefinition"/> so it survives a save.</summary>
    public string? BodyContentType { get; set; }

    /// <summary>Fields for a form-urlencoded or multipart body. Not yet editable from the app UI,
    /// but preserved through save so an imported or hand-authored one round-trips.</summary>
    public List<FormField> BodyForm { get; set; } = [];

    public string? BodyBinaryPath { get; set; }

    public string? BodyGraphQlVariables { get; set; }

    public string? Folder { get; set; }

    public string? Description { get; set; }

    /// <summary>Populated by the scanner when an endpoint could not be fully derived. Round-tripped
    /// rather than dropped, so a note survives the tab opening and being saved back out.</summary>
    public List<string> UnresolvedNotes { get; set; } = [];

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
        Folder = Folder,
        Description = Description,
        Headers = [.. Headers],
        Query = [.. Query],
        PathParams = new Dictionary<string, string>(PathParams),
        Body = BodyKind == BodyKind.None
            ? null
            : new RequestBody
            {
                Kind = BodyKind,
                Text = BodyText,
                ContentType = BodyContentType,
                Form = [.. BodyForm],
                BinaryPath = BodyBinaryPath,
                GraphQlVariables = BodyGraphQlVariables,
            },
        Auth = AuthProfile is null ? null : new AuthReference(AuthProfile),
        Scripts = PreRequestScript is null && PostResponseScript is null
            ? null
            : new RequestScripts(PreRequestScript, PostResponseScript),
        Assertions = [.. Assertions],
        Settings = Settings,
        RequiredScopes = [.. RequiredScopes],
        UnresolvedNotes = [.. UnresolvedNotes],
    };

    public static TabState FromDefinition(RequestDefinition request, string? collectionName, string? environmentName)
    {
        // Defensive: a well-formed file keeps the query out of Url entirely (COLLECTION_FORMAT §5.2),
        // but a hand-edited file or one saved before this split existed might not. Splitting here
        // means TabState.Url is always bare, which is the invariant the URL-bar sync in
        // TabViewModel depends on. A name already present in request.Query is left alone — that
        // list, with whatever Enabled state it already carries, is authoritative.
        var (url, embeddedQuery) = QueryString.Split(request.Url);
        var existingNames = new HashSet<string>(request.Query.Select(q => q.Name), StringComparer.Ordinal);
        var query = new List<QueryParameter>(request.Query);
        query.AddRange(embeddedQuery.Where(q => existingNames.Add(q.Name)));

        return new()
        {
            Title = request.Name,
            Method = request.Method,
            Url = url,
            EndpointId = request.Id,
            CollectionName = collectionName,
            EnvironmentName = environmentName,
            Provenance = request.Provenance,
            Folder = request.Folder,
            Description = request.Description,
            BodyText = request.Body?.Text,
            BodyKind = request.Body?.Kind ?? BodyKind.None,
            BodyContentType = request.Body?.ContentType,
            BodyForm = [.. request.Body?.Form ?? []],
            BodyBinaryPath = request.Body?.BinaryPath,
            BodyGraphQlVariables = request.Body?.GraphQlVariables,
            Headers = [.. request.Headers],
            Query = query,
            PathParams = new Dictionary<string, string>(request.PathParams),
            AuthProfile = request.Auth?.Profile,
            PreRequestScript = request.Scripts?.PreRequest,
            PostResponseScript = request.Scripts?.PostResponse,
            Assertions = [.. request.Assertions],
            Settings = request.Settings,
            RequiredScopes = [.. request.RequiredScopes],
            UnresolvedNotes = [.. request.UnresolvedNotes],

            // A request with query parameters and no body opens on Params, since that is usually
            // the only tab with anything to look at; everything else opens on Body. Path is never
            // chosen here — TabViewModel only ever selects it when the URL actually has {tokens}.
            ActiveRequestTabIndex = query.Any(q => q.Name.Length > 0) && (request.Body?.Kind ?? BodyKind.None) == BodyKind.None
                ? 1
                : 3,
        };
    }
}

/// <param name="Unresolved">Placeholders with no local value. Shown on the banner. CAP-06.</param>
public sealed record CapsuleOrigin(
    string FileName,
    string? ExportedBy,
    DateTimeOffset ImportedUtc,
    IReadOnlyList<string> Resolved,
    IReadOnlyList<string> Unresolved);
