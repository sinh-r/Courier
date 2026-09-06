using System.Security.Cryptography;
using System.Text;
using Courier.Core.Collections;

namespace Courier.Scanner.Diffing;

/// <summary>
/// Compares a fresh scan against the last one and reports what changed. SCAN-10.
/// </summary>
/// <remarks>
/// <para>
/// The diff is what the sync screen renders, and the screen is the product's strongest moment, so
/// the diff has to be trustworthy in a specific way: it must never report a change the user did not
/// cause, and it must never quietly drop an endpoint whose identity shifted.
/// </para>
/// <para>
/// Rename detection falls out of <see cref="EndpointIdentity"/> being a hash of only verb, route and
/// declaring type: a method renamed in C# keeps its identity, so it shows as unchanged rather than
/// as a delete plus an add that would orphan the user's saved payload.
/// </para>
/// </remarks>
public static class ScanDiff
{
    public static ScanChangeSet Compare(
        IReadOnlyList<RequestDefinition> previous,
        IReadOnlyList<RequestDefinition> current)
    {
        var before = previous.Where(r => r.Id is not null).ToDictionary(r => r.Id!, StringComparer.Ordinal);
        var after = current.Where(r => r.Id is not null).ToDictionary(r => r.Id!, StringComparer.Ordinal);

        var added = new List<RequestDefinition>();
        var removed = new List<RequestDefinition>();
        var changed = new List<EndpointChange>();

        foreach (var (id, endpoint) in after)
        {
            if (!before.TryGetValue(id, out var old))
            {
                added.Add(endpoint);
                continue;
            }

            var fields = CompareFields(old, endpoint);
            if (fields.Count > 0)
            {
                changed.Add(new EndpointChange(endpoint, fields));
            }
        }

        foreach (var (id, endpoint) in before)
        {
            if (!after.ContainsKey(id))
            {
                removed.Add(endpoint);
            }
        }

        return new ScanChangeSet(
            [.. added.OrderBy(e => e.Url, StringComparer.Ordinal)],
            [.. changed.OrderBy(c => c.Endpoint.Url, StringComparer.Ordinal)],
            [.. removed.OrderBy(e => e.Url, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Field-level differences, as the mock shows them: body properties added and removed, and the
    /// before/after of the required scopes.
    /// </summary>
    private static IReadOnlyList<FieldChange> CompareFields(RequestDefinition old, RequestDefinition current)
    {
        var changes = new List<FieldChange>();

        CompareBody(old.Body?.Text, current.Body?.Text, changes);

        var oldScopes = old.RequiredScopes.ToHashSet(StringComparer.Ordinal);
        var newScopes = current.RequiredScopes.ToHashSet(StringComparer.Ordinal);

        if (!oldScopes.SetEquals(newScopes))
        {
            changes.Add(new FieldChange(
                "auth",
                FieldChangeKind.Modified,
                $"{Describe(old.RequiredScopes)} → {Describe(current.RequiredScopes)}"));
        }

        foreach (var header in current.Headers.Where(h => old.Headers.All(o => o.Name != h.Name)))
        {
            changes.Add(new FieldChange("header", FieldChangeKind.Added, header.Name));
        }

        foreach (var header in old.Headers.Where(h => current.Headers.All(n => n.Name != h.Name)))
        {
            changes.Add(new FieldChange("header", FieldChangeKind.Removed, header.Name));
        }

        return changes;
    }

    /// <summary>
    /// Compares the top-level property names of two JSON samples. Deliberately shallow: the sync
    /// screen is a review, not a merge tool, and "+ customerId  string, required" is what the user
    /// needs to decide with.
    /// </summary>
    private static void CompareBody(string? before, string? after, List<FieldChange> changes)
    {
        if (before == after)
        {
            return;
        }

        var oldProperties = PropertyNames(before);
        var newProperties = PropertyNames(after);

        foreach (var name in newProperties.Keys.Where(k => !oldProperties.ContainsKey(k)))
        {
            changes.Add(new FieldChange("body", FieldChangeKind.Added, $"{name}  {newProperties[name]}"));
        }

        foreach (var name in oldProperties.Keys.Where(k => !newProperties.ContainsKey(k)))
        {
            changes.Add(new FieldChange("body", FieldChangeKind.Removed, name));
        }
    }

    private static Dictionary<string, string> PropertyNames(string? json)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(json))
        {
            return names;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return names;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                names[property.Name] = property.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => "string",
                    System.Text.Json.JsonValueKind.Number => "number",
                    System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
                    System.Text.Json.JsonValueKind.Array => "array",
                    System.Text.Json.JsonValueKind.Object => "object",
                    _ => "null",
                };
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A body that is not JSON compares as opaque; the endpoint still shows as changed.
        }

        return names;
    }

    private static string Describe(IReadOnlyList<string> scopes) =>
        scopes.Count == 0 ? "none" : string.Join(", ", scopes);
}

/// <summary>
/// Content hashing for the incremental rescan. SCAN-10, PERF-06.
/// </summary>
/// <remarks>
/// TECH_SPEC 3.4 points at Graphify.NET for this logic. That project is not available here, so it
/// is written fresh: SHA-256 per file, compared against the previous scan, so a 200-endpoint
/// solution rescans in under the three seconds PERF-06 allows by parsing only what changed.
/// </remarks>
public static class ContentHash
{
    public static string OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string OfText(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Files whose content differs from the previous scan, plus files that have gone. Returning
    /// both is what lets a rescan drop endpoints from a deleted file without re-reading everything.
    /// </summary>
    public static (IReadOnlyList<string> Changed, IReadOnlyList<string> Deleted) Changes(
        IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> current)
    {
        var changed = current
            .Where(pair => !previous.TryGetValue(pair.Key, out var hash) || hash != pair.Value)
            .Select(pair => pair.Key)
            .ToList();

        var deleted = previous.Keys.Where(path => !current.ContainsKey(path)).ToList();

        return (changed, deleted);
    }
}

public sealed record ScanChangeSet(
    IReadOnlyList<RequestDefinition> Added,
    IReadOnlyList<EndpointChange> Changed,
    IReadOnlyList<RequestDefinition> Removed)
{
    public int Count => Added.Count + Changed.Count + Removed.Count;

    public bool IsEmpty => Count == 0;
}

public sealed record EndpointChange(RequestDefinition Endpoint, IReadOnlyList<FieldChange> Fields);

public sealed record FieldChange(string Section, FieldChangeKind Kind, string Description);

public enum FieldChangeKind
{
    Added,
    Removed,
    Modified,
}
