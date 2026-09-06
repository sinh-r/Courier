using System.Net;

namespace Courier.Core.Abstractions;

/// <summary>
/// NTLM and Kerberos using the current OS identity, with no credential entry. ENT-07.
/// </summary>
public interface IIntegratedAuthProvider
{
    /// <summary>False on platforms where there is no integrated identity to use. Report, never fake.</summary>
    bool IsAvailable { get; }

    /// <summary>The identity that would be presented, for display before a send.</summary>
    string? CurrentIdentity { get; }

    /// <summary>
    /// Default credentials for the handler. Null when unavailable; the caller must surface that
    /// rather than silently falling back to anonymous.
    /// </summary>
    ICredentials? GetDefaultCredentials();
}

/// <summary>
/// Supplies a native window handle so the WAM broker can parent its dialog correctly. ENT-01.
/// Implemented by the UI layer; Core only ever sees the handle.
/// </summary>
public interface IBrokerWindowHandleProvider
{
    /// <summary>IntPtr.Zero when there is no window (CLI, tests) — the broker then falls back.</summary>
    IntPtr GetActiveWindowHandle();
}
