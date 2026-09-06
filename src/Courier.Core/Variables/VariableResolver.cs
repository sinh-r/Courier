using System.Text;
using System.Text.RegularExpressions;
using Courier.Core.Abstractions;
using Courier.Core.Collections;

namespace Courier.Core.Variables;

/// <summary>
/// Resolves <c>{{name}}</c> references with the precedence CORE-04 fixes:
/// request → environment → collection → global.
/// </summary>
/// <remarks>
/// Every resolution carries where the value came from, because CORE-04 also requires showing the
/// resolved value on hover, and "which of my four scopes won" is the question that hover is really
/// answering. Local environment values are fetched from the credential store on demand and are
/// never cached to disk.
/// </remarks>
public sealed partial class VariableResolver
{
    private const int MaxRecursionDepth = 10;

    private readonly ISecretStore _secretStore;

    public VariableResolver(ISecretStore secretStore) => _secretStore = secretStore;

    [GeneratedRegex(@"\{\{\s*([^{}\s][^{}]*?)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex { get; }

    /// <summary>Finds every reference in a string, for editor decoration and hover.</summary>
    /// <remarks>
    /// Returns a list rather than an iterator: the enumerator is a ref struct, so it cannot cross
    /// the await boundaries in <see cref="SubstituteAsync"/>.
    /// </remarks>
    public static List<VariableReference> FindReferences(string? text)
    {
        var references = new List<VariableReference>();
        if (string.IsNullOrEmpty(text))
        {
            return references;
        }

        foreach (var match in ReferenceRegex.EnumerateMatches(text))
        {
            var name = text.AsSpan(match.Index + 2, match.Length - 4).Trim().ToString();
            references.Add(new VariableReference(name, match.Index, match.Length));
        }

        return references;
    }

    /// <summary>
    /// Resolves one name against the scope stack. Returns an unresolved result rather than throwing:
    /// an unbound variable is a thing to show the user in the URL bar, not an exception.
    /// </summary>
    public async ValueTask<ResolvedValue> ResolveAsync(
        string name,
        VariableScopes scopes,
        CancellationToken ct = default)
    {
        if (scopes.Request.TryGetValue(name, out var requestValue))
        {
            return new ResolvedValue(name, requestValue, VariableOrigin.Request, IsSecret: false);
        }

        if (scopes.Environment is not null)
        {
            if (scopes.Environment.Shared.TryGetValue(name, out var sharedValue))
            {
                return new ResolvedValue(name, sharedValue, VariableOrigin.EnvironmentShared, IsSecret: false);
            }

            if (scopes.Environment.LocalNames.Contains(name, StringComparer.Ordinal))
            {
                var secret = await _secretStore
                    .GetAsync(scopes.Environment.SecretKeyFor(name), ct)
                    .ConfigureAwait(false);

                return secret is null
                    ? ResolvedValue.Missing(name, VariableOrigin.EnvironmentLocal)
                    : new ResolvedValue(name, secret, VariableOrigin.EnvironmentLocal, IsSecret: true);
            }
        }

        if (scopes.Collection.TryGetValue(name, out var collectionValue))
        {
            return new ResolvedValue(name, collectionValue, VariableOrigin.Collection, IsSecret: false);
        }

        if (scopes.Global.TryGetValue(name, out var globalValue))
        {
            return new ResolvedValue(name, globalValue, VariableOrigin.Global, IsSecret: false);
        }

        return ResolvedValue.Missing(name, VariableOrigin.None);
    }

    /// <summary>
    /// Substitutes every reference in a string. A variable whose value contains another reference is
    /// resolved too, up to a depth limit that stops a self-referential collection from hanging the
    /// send. Unbound references are left as written so the user can see what is missing.
    /// </summary>
    public async ValueTask<SubstitutionResult> SubstituteAsync(
        string? text,
        VariableScopes scopes,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new SubstitutionResult(text ?? string.Empty, [], []);
        }

        var used = new List<ResolvedValue>();
        var unbound = new List<string>();
        var current = text;

        for (var depth = 0; depth < MaxRecursionDepth; depth++)
        {
            var references = FindReferences(current);
            if (references.Count == 0)
            {
                break;
            }

            var sb = new StringBuilder(current.Length);
            var cursor = 0;
            var substitutedAny = false;

            foreach (var reference in references)
            {
                sb.Append(current, cursor, reference.Start - cursor);

                var resolved = await ResolveAsync(reference.Name, scopes, ct).ConfigureAwait(false);
                if (resolved.IsResolved)
                {
                    sb.Append(resolved.Value);
                    used.Add(resolved);
                    substitutedAny = true;
                }
                else
                {
                    // Leave the reference intact. The URL bar shows it unresolved rather than
                    // silently sending a request with an empty segment in it.
                    sb.Append(current, reference.Start, reference.Length);
                    if (!unbound.Contains(reference.Name, StringComparer.Ordinal))
                    {
                        unbound.Add(reference.Name);
                    }
                }

                cursor = reference.Start + reference.Length;
            }

            sb.Append(current, cursor, current.Length - cursor);
            current = sb.ToString();

            if (!substitutedAny)
            {
                break;
            }
        }

        return new SubstitutionResult(current, used, unbound);
    }
}

/// <param name="Start">Index of the opening brace, so an editor can decorate the exact span.</param>
public readonly record struct VariableReference(string Name, int Start, int Length);

/// <summary>The four scopes, in precedence order. CORE-04.</summary>
public sealed record VariableScopes
{
    public IReadOnlyDictionary<string, string> Request { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public EnvironmentDefinition? Environment { get; init; }

    public IReadOnlyDictionary<string, string> Collection { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Global { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public enum VariableOrigin
{
    None,
    Request,
    EnvironmentShared,
    EnvironmentLocal,
    Collection,
    Global,
}

/// <param name="IsSecret">
/// True for a value read from the credential store. The hover shows the origin and masks the value;
/// nothing marked secret is ever written to a log or a capsule.
/// </param>
public sealed record ResolvedValue(string Name, string? Value, VariableOrigin Origin, bool IsSecret)
{
    public bool IsResolved => Value is not null;

    public static ResolvedValue Missing(string name, VariableOrigin searchedTo) => new(name, null, searchedTo, false);

    /// <summary>What the hover shows. A secret is never revealed, only located. P2.</summary>
    public string Describe() => (IsResolved, IsSecret) switch
    {
        (false, _) => $"{Name} is not set in any scope",
        (true, true) => $"{Name} · {DescribeOrigin()} · value hidden",
        (true, false) => $"{Name} · {DescribeOrigin()} · {Value}",
    };

    public string DescribeOrigin() => Origin switch
    {
        VariableOrigin.Request => "this request",
        VariableOrigin.EnvironmentShared => "environment, shared",
        VariableOrigin.EnvironmentLocal => "environment, local",
        VariableOrigin.Collection => "collection",
        VariableOrigin.Global => "global",
        _ => "unset",
    };
}

/// <param name="Unbound">Names with no value anywhere. Drives the capsule import "unresolved" list too.</param>
public sealed record SubstitutionResult(
    string Text,
    IReadOnlyList<ResolvedValue> Used,
    IReadOnlyList<string> Unbound)
{
    public bool IsComplete => Unbound.Count == 0;
}
