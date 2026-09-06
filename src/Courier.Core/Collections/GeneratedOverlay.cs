namespace Courier.Core.Collections;

/// <summary>
/// The machine-owned half of a generated collection. Overwritten freely on every rescan.
/// SCAN-09.
/// </summary>
public sealed class GeneratedEndpointSet
{
    public int Courier { get; set; } = CollectionFormat.Version;

    public string? Source { get; set; }

    public DateTimeOffset GeneratedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Keyed by <see cref="EndpointIdentity"/>, which is also each entry's Id.</summary>
    public List<RequestDefinition> Endpoints { get; set; } = [];
}

/// <summary>
/// The human-owned half. Courier reads this and never writes over it, which is the mechanism
/// behind P3: regenerating never destroys human work.
/// </summary>
public sealed class OverlaySet
{
    public int Courier { get; set; } = CollectionFormat.Version;

    public List<OverlayEntry> Entries { get; set; } = [];

    public OverlayEntry GetOrAdd(string endpointId)
    {
        var existing = Entries.FirstOrDefault(e => e.Id == endpointId);
        if (existing is not null)
        {
            return existing;
        }

        var added = new OverlayEntry { Id = endpointId };
        Entries.Add(added);
        return added;
    }
}

/// <summary>
/// One user's edits to one generated endpoint. Every property is nullable: null means "I did not
/// change this, use whatever the generator produced", which is what lets a regenerated route or
/// header flow through while a saved payload stays put.
/// </summary>
public sealed class OverlayEntry
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? Url { get; set; }

    public string? Description { get; set; }

    /// <summary>The saved payload. The single most valuable thing here, and the one users fear losing.</summary>
    public RequestBody? Body { get; set; }

    public Dictionary<string, string>? PathParams { get; set; }

    public List<QueryParameter>? Query { get; set; }

    public List<HeaderValue>? Headers { get; set; }

    public AuthReference? Auth { get; set; }

    public RequestScripts? Scripts { get; set; }

    public List<AssertionDefinition>? Assertions { get; set; }

    public RequestSettings? Settings { get; set; }

    /// <summary>
    /// Set when the generator has removed the endpoint but the user's work is being kept. The tree
    /// shows it as orphaned rather than deleting it, because "your saved payloads are kept" has to
    /// be true and not just reassuring.
    /// </summary>
    public bool Orphaned { get; set; }

    public bool HasEdits =>
        Name is not null || Url is not null || Description is not null || Body is not null
        || PathParams is { Count: > 0 } || Query is { Count: > 0 } || Headers is { Count: > 0 }
        || Auth is not null || Scripts is not null || Assertions is { Count: > 0 } || Settings is not null;
}

/// <summary>
/// Joins the generated set to the overlay by endpoint identity, producing what the user sees.
/// </summary>
/// <remarks>
/// This is the mechanism that makes SCAN-09 and P3 real. The generated definition supplies
/// structure — route, method, declared scopes, sample body. The overlay supplies anything the user
/// touched. Neither file is ever merged into the other on disk; the join happens in memory, every
/// load, which is why regenerating cannot lose an edit.
/// </remarks>
public static class GeneratedOverlayJoiner
{
    public static IReadOnlyList<RequestDefinition> Join(GeneratedEndpointSet generated, OverlaySet overlay)
    {
        var byId = overlay.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var joined = new List<RequestDefinition>(generated.Endpoints.Count + overlay.Entries.Count);
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var endpoint in generated.Endpoints)
        {
            if (endpoint.Id is not null && byId.TryGetValue(endpoint.Id, out var edits))
            {
                matched.Add(endpoint.Id);
                joined.Add(Apply(endpoint, edits));
            }
            else
            {
                joined.Add(endpoint);
            }
        }

        // Overlay entries with no generated counterpart. The endpoint went away, but the user's
        // payloads did not, and the sync screen promised they would be kept.
        foreach (var orphan in overlay.Entries.Where(e => !matched.Contains(e.Id) && e.HasEdits))
        {
            joined.Add(ToOrphanedRequest(orphan));
        }

        return joined;
    }

    private static RequestDefinition Apply(RequestDefinition generated, OverlayEntry edits)
    {
        var result = generated.Clone();

        if (edits.Name is not null)
        {
            result.Name = edits.Name;
        }

        if (edits.Url is not null)
        {
            result.Url = edits.Url;
        }

        if (edits.Description is not null)
        {
            result.Description = edits.Description;
        }

        if (edits.Body is not null)
        {
            result.Body = edits.Body.Clone();
        }

        if (edits.PathParams is { Count: > 0 })
        {
            foreach (var (key, value) in edits.PathParams)
            {
                result.PathParams[key] = value;
            }
        }

        if (edits.Query is { Count: > 0 })
        {
            result.Query = [.. edits.Query];
        }

        if (edits.Headers is { Count: > 0 })
        {
            result.Headers = MergeHeaders(result.Headers, edits.Headers);
        }

        if (edits.Auth is not null)
        {
            result.Auth = edits.Auth;
        }

        if (edits.Scripts is not null)
        {
            result.Scripts = edits.Scripts;
        }

        if (edits.Assertions is { Count: > 0 })
        {
            result.Assertions = [.. edits.Assertions];
        }

        if (edits.Settings is not null)
        {
            result.Settings = edits.Settings.InheritFrom(result.Settings);
        }

        result.Provenance = edits.HasEdits ? Provenance.GeneratedEdited : Provenance.Generated;
        return result;
    }

    /// <summary>A user header replaces the generated one of the same name; the rest are kept.</summary>
    private static List<HeaderValue> MergeHeaders(List<HeaderValue> generated, List<HeaderValue> edited)
    {
        var editedNames = edited.Select(h => h.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. generated.Where(h => !editedNames.Contains(h.Name)), .. edited];
    }

    private static RequestDefinition ToOrphanedRequest(OverlayEntry orphan) => new()
    {
        Id = orphan.Id,
        Name = orphan.Name ?? "Removed endpoint",
        Url = orphan.Url ?? string.Empty,
        Provenance = Provenance.GeneratedEdited,
        Body = orphan.Body?.Clone(),
        PathParams = orphan.PathParams is null ? [] : new Dictionary<string, string>(orphan.PathParams),
        Query = orphan.Query is null ? [] : [.. orphan.Query],
        Headers = orphan.Headers is null ? [] : [.. orphan.Headers],
        Auth = orphan.Auth,
        Scripts = orphan.Scripts,
        Assertions = orphan.Assertions is null ? [] : [.. orphan.Assertions],
        Settings = orphan.Settings ?? new RequestSettings(),
        UnresolvedNotes =
        [
            "This endpoint is no longer in the source. Your saved payload and variables are kept here.",
        ],
    };
}
