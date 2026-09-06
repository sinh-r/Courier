using System.Collections.Concurrent;
using System.Net;

namespace Courier.Core.Http;

/// <summary>
/// Per-environment cookie storage, inspectable and editable. CORE-05.
/// </summary>
/// <remarks>
/// Per-environment rather than global because sharing a session cookie between QA and production is
/// a way to lose an afternoon. The jar is held in memory and persisted alongside history, never in
/// the collection folder: a cookie is a credential and P2 keeps it out of any file we commit.
/// </remarks>
public sealed class CookieJar
{
    private const string DefaultScope = "(no environment)";

    private readonly ConcurrentDictionary<string, CookieContainer> _byEnvironment = new(StringComparer.Ordinal);

    public CookieContainer For(string? environmentName) =>
        _byEnvironment.GetOrAdd(environmentName ?? DefaultScope, _ => new CookieContainer());

    /// <summary>Attaches the stored cookies for this URL. Called on every send.</summary>
    public void ApplyTo(HttpRequestMessage message, string? environmentName)
    {
        if (message.RequestUri is null)
        {
            return;
        }

        var header = For(environmentName).GetCookieHeader(message.RequestUri);
        if (!string.IsNullOrEmpty(header))
        {
            message.Headers.TryAddWithoutValidation("Cookie", header);
        }
    }

    /// <summary>Stores Set-Cookie from a response. Malformed cookies are dropped, not thrown on.</summary>
    public void Capture(HttpResponseMessage response, string? environmentName)
    {
        if (response.RequestMessage?.RequestUri is not { } uri
            || !response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        var container = For(environmentName);
        foreach (var value in values)
        {
            try
            {
                container.SetCookies(uri, value);
            }
            catch (CookieException)
            {
                // A server sending a cookie we cannot parse is the server's problem, not a reason
                // to fail the request the user is watching.
            }
        }
    }

    /// <summary>Everything in the jar, for the inspector. CORE-05 requires this to be visible.</summary>
    public IReadOnlyList<StoredCookie> List(string? environmentName)
    {
        var container = For(environmentName);
        var all = container.GetAllCookies();
        var result = new List<StoredCookie>(all.Count);

        foreach (Cookie cookie in all)
        {
            result.Add(new StoredCookie(
                cookie.Name,
                cookie.Value,
                cookie.Domain,
                cookie.Path,
                cookie.Expires == DateTime.MinValue ? null : cookie.Expires,
                cookie.Secure,
                cookie.HttpOnly));
        }

        return result;
    }

    public void Set(string? environmentName, StoredCookie cookie) =>
        For(environmentName).Add(new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
        {
            Secure = cookie.Secure,
            HttpOnly = cookie.HttpOnly,
            Expires = cookie.Expires ?? DateTime.MinValue,
        });

    public void Remove(string? environmentName, string name, string domain, string path)
    {
        foreach (Cookie cookie in For(environmentName).GetAllCookies())
        {
            if (cookie.Name == name && cookie.Domain == domain && cookie.Path == path)
            {
                cookie.Expired = true;
            }
        }
    }

    public void Clear(string? environmentName) =>
        _byEnvironment.TryRemove(environmentName ?? DefaultScope, out _);

    public void ClearAll() => _byEnvironment.Clear();
}

public sealed record StoredCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    DateTime? Expires,
    bool Secure,
    bool HttpOnly);
