namespace Courier.Core.Abstractions;

/// <summary>
/// The only place a secret value is ever persisted. STOR-04 and P2: the user's secret never
/// enters a file we write, a log we emit, or a payload we transmit anywhere but the target host.
/// </summary>
/// <remarks>
/// Keys are opaque, stable and non-secret. They appear in collection files; the values never do.
/// </remarks>
public interface ISecretStore
{
    /// <summary>A short description of where secrets physically live, for the storage settings page (SEC-05).</summary>
    string LocationDescription { get; }

    /// <summary>True when this store is backed by real OS protection rather than a fallback.</summary>
    bool IsHardwareBacked { get; }

    ValueTask<string?> GetAsync(SecretKey key, CancellationToken ct = default);

    ValueTask SetAsync(SecretKey key, string value, CancellationToken ct = default);

    ValueTask DeleteAsync(SecretKey key, CancellationToken ct = default);

    /// <summary>Keys only. An implementation that cannot enumerate returns an empty list rather than throwing.</summary>
    ValueTask<IReadOnlyList<SecretKey>> ListAsync(string scope, CancellationToken ct = default);
}

/// <summary>
/// A namespaced handle to a secret. The scope keeps environments, auth profiles and telemetry
/// backends from colliding, and lets the storage page clear one category without touching another.
/// </summary>
public readonly record struct SecretKey(string Scope, string Name)
{
    public const string EnvironmentScope = "env";
    public const string AuthScope = "auth";
    public const string TelemetryScope = "telemetry";
    public const string CertificateScope = "cert";

    public override string ToString() => $"{Scope}/{Name}";
}
