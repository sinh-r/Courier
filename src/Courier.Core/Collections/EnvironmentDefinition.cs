using Courier.Core.Abstractions;
using Courier.Core.Privacy;

namespace Courier.Core.Collections;

/// <summary>
/// An environment, split into values that go in git and values that never leave this machine.
/// SEC-03, CORE-12.
/// </summary>
/// <remarks>
/// The split is the whole point. Only <see cref="Shared"/> is serialized. A local variable's value
/// lives in the OS credential store and is fetched by key; the file records that the variable
/// exists and is local, and nothing else about it.
/// </remarks>
public sealed class EnvironmentDefinition
{
    public int Courier { get; set; } = CollectionFormat.Version;

    public string Name { get; set; } = "Environment";

    /// <summary>Committed to git. A secret-shaped value here is flagged inline. SEC-03.</summary>
    public Dictionary<string, string> Shared { get; set; } = [];

    /// <summary>
    /// Names only. Values are read from <see cref="ISecretStore"/> at resolution time and never
    /// written to this file.
    /// </summary>
    public List<string> LocalNames { get; set; } = [];

    /// <summary>The credential store key for a local variable in this environment.</summary>
    public SecretKey SecretKeyFor(string variableName) =>
        new(SecretKey.EnvironmentScope, $"{Name}/{variableName}");

    /// <summary>
    /// Shared entries whose value looks like a credential. Drives the inline warning:
    /// "This looks like a secret and it is in the shared column, so it will be committed."
    /// </summary>
    public IEnumerable<SharedSecretWarning> FindSecretsInSharedColumn()
    {
        foreach (var (name, value) in Shared)
        {
            var classification = SecretPatterns.Classify(value, name);
            if (classification.IsSecret)
            {
                yield return new SharedSecretWarning(name, classification.Reason ?? "looks like a secret");
            }
        }
    }
}

/// <param name="Reason">Completes the sentence shown under the row.</param>
public sealed record SharedSecretWarning(string VariableName, string Reason);
