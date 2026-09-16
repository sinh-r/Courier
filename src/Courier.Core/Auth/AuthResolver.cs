using Courier.Core.Collections;

namespace Courier.Core.Auth;

/// <summary>
/// Turns a request's <see cref="AuthReference"/> and the collection's default into the one profile
/// that actually applies, or an explanation of why none could be found. ENT-02, ENT-06.
/// </summary>
/// <remarks>
/// A request set to <see cref="AuthMode.Inherit"/> — the default for every new request — takes
/// whatever the collection specifies; a collection with no auth of its own means no auth at all.
/// Naming a profile that is not in <c>auth/</c> is never treated as "no auth": REQUIREMENTS 9's
/// "reported, never silently omitted" applies here exactly as it does to an unresolved route.
/// </remarks>
public static class AuthResolver
{
    /// <param name="requestAuth">The request's own reference, or null for a request that predates ENT-02.</param>
    /// <param name="collectionAuth">The collection's default, or null for "no default set".</param>
    /// <param name="profiles">Looks up a saved profile by name. Returns null when there is none.</param>
    public static ResolvedAuth Resolve(
        AuthReference? requestAuth,
        AuthReference? collectionAuth,
        Func<string, AuthProfile?> profiles)
    {
        var request = requestAuth ?? new AuthReference(AuthMode.Inherit);

        if (request.Mode != AuthMode.Inherit)
        {
            return ResolveReference(request, AuthSource.Request, profiles);
        }

        var collection = collectionAuth ?? new AuthReference(AuthMode.None);
        return collection.Mode == AuthMode.None
            ? ResolvedAuth.NoAuth(AuthSource.Request)
            : ResolveReference(collection, AuthSource.Collection, profiles);
    }

    private static ResolvedAuth ResolveReference(AuthReference reference, AuthSource fallbackSource, Func<string, AuthProfile?> profiles) =>
        reference.Mode switch
        {
            AuthMode.None => ResolvedAuth.NoAuth(fallbackSource),

            AuthMode.Inline => reference.Inline is { } inline
                ? new ResolvedAuth(inline, fallbackSource, null)
                : new ResolvedAuth(null, fallbackSource, "This auth is set to a custom configuration, but none was saved with it."),

            AuthMode.Profile => ResolveProfile(reference.Profile, fallbackSource, profiles),

            // Inherit only reaches here for a collection-level reference, where it means the same
            // as None — there is nothing above a collection to inherit from.
            _ => ResolvedAuth.NoAuth(fallbackSource),
        };

    private static ResolvedAuth ResolveProfile(string? name, AuthSource fallbackSource, Func<string, AuthProfile?> profiles)
    {
        if (string.IsNullOrEmpty(name))
        {
            return new ResolvedAuth(null, fallbackSource, "This auth points at a profile with no name.");
        }

        var profile = profiles(name);
        return profile is null
            ? new ResolvedAuth(null, AuthSource.Profile(name), $"Auth profile '{name}' is not in auth/. Create it, or choose a different auth for this request.")
            : new ResolvedAuth(profile, AuthSource.Profile(name), null);
    }
}

/// <param name="Profile">Null when auth could not be resolved, or when the resolution is "no auth".</param>
/// <param name="Source">Where the choice came from, for the "from collection · Bearer" line in the UI.</param>
/// <param name="Problem">Non-null when a named profile or inline config could not be used as-is.</param>
public sealed record ResolvedAuth(AuthProfile? Profile, AuthSource Source, string? Problem)
{
    public bool HasAuth => Profile is not null && Profile.Kind != AuthKind.None;

    public static ResolvedAuth NoAuth(AuthSource source) => new(null, source, null);
}

/// <summary>Where a resolved auth choice came from — shown, not just used, so the "from collection" line is honest.</summary>
public sealed record AuthSource(AuthSourceKind Kind, string? ProfileName = null)
{
    public static readonly AuthSource Request = new(AuthSourceKind.Request);
    public static readonly AuthSource Collection = new(AuthSourceKind.Collection);

    public static AuthSource Profile(string name) => new(AuthSourceKind.SavedProfile, name);

    public override string ToString() => Kind switch
    {
        AuthSourceKind.Request => "this request",
        AuthSourceKind.Collection => "the collection default",
        AuthSourceKind.SavedProfile => $"profile '{ProfileName}'",
        _ => "unknown",
    };
}

public enum AuthSourceKind
{
    Request,
    Collection,
    SavedProfile,
}
