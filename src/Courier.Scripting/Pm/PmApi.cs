using System.Text.Json;
using Courier.Core.Http;
using Courier.Scripting.Sandbox;

namespace Courier.Scripting.Pm;

/// <summary>
/// The <c>pm.*</c> compatibility shim. TEST-02.
/// </summary>
/// <remarks>
/// <para>
/// The documented surface, and only that: <c>pm.test</c>, <c>pm.expect</c>, <c>pm.response</c>,
/// <c>pm.environment</c>, <c>pm.variables</c>, <c>pm.request</c>. Anything else raises
/// <see cref="UnsupportedPmMemberException"/> naming the member.
/// </para>
/// <para>
/// The temptation is to add "just one more" member each time a script fails. REQUIREMENTS 9 is a
/// warning against exactly that: the surface is a published promise, and every unlisted member
/// added quietly turns "we support this list" into "we support whatever you happen to try".
/// </para>
/// </remarks>
public sealed class PmApi
{
    private readonly PmVariableScope _environment;
    private readonly PmVariableScope _variables;
    private readonly PmVariableScope _globals;
    private readonly PmVariableScope _collectionVariables;
    private readonly List<string> _unsupported;

    private readonly Jint.Engine _engine;

    public PmApi(
        Jint.Engine engine,
        ExchangeResult? result,
        IDictionary<string, string> environment,
        IDictionary<string, string> variables,
        IDictionary<string, string> globals,
        IDictionary<string, string> collectionVariables,
        List<string> unsupported)
    {
        _engine = engine;
        _environment = new PmVariableScope(environment);
        _variables = new PmVariableScope(variables);
        _globals = new PmVariableScope(globals);
        _collectionVariables = new PmVariableScope(collectionVariables);
        _unsupported = unsupported;

        response = result is null ? null : new PmResponse(result);
        request = result is null ? null : new PmRequest(result.Request);
    }

    /// <summary>Assertions the script declared, in order. Rendered in the tests pane.</summary>
    public List<PmTestResult> Results { get; } = [];

    // Lower-cased deliberately: these are the names Postman scripts already use, and the point of
    // the shim is that an existing script runs unmodified.
    public PmResponse? response { get; }

    public PmRequest? request { get; }

    public PmVariableScope environment => _environment;

    public PmVariableScope variables => _variables;

    public PmVariableScope globals => _globals;

    public PmVariableScope collectionVariables => _collectionVariables;

    /// <summary>
    /// <c>pm.test(name, fn)</c>. A throwing callback is a failed assertion, not a failed script:
    /// that is how Postman behaves and how the tests pane needs it to behave.
    /// </summary>
    public void test(string name, Jint.Native.JsValue body)
    {
        try
        {
            // Through the engine, not DynamicInvoke: a JS function is a JsValue with a closure over
            // the script's scope, not a CLR delegate, and invoking it any other way loses that.
            _engine.Invoke(body);
            Results.Add(new PmTestResult(name, true, null));
        }
        catch (UnsupportedPmMemberException)
        {
            // Not an assertion failure. It has to reach the caller so the run reports the member
            // by name rather than burying it as one red row.
            throw;
        }
        catch (Exception ex)
        {
            Results.Add(new PmTestResult(name, false, Unwrap(ex)));
        }
    }

    /// <summary><c>pm.expect(value)</c>, returning the chai-style assertion chain.</summary>
    public PmExpectation expect(object? value) => new(value);

    /// <summary>Reports a member Courier does not implement, by name. TEST-02.</summary>
    public void Unsupported(string member)
    {
        if (!_unsupported.Contains(member, StringComparer.Ordinal))
        {
            _unsupported.Add(member);
        }

        throw new UnsupportedPmMemberException(member);
    }

    /// <summary>Members named here throw with a useful message rather than "undefined is not a function".</summary>
    public void sendRequest() => Unsupported("sendRequest");

    public void setNextRequest() => Unsupported("setNextRequest");

    public void visualizer() => Unsupported("visualizer");

    public void cookies() => Unsupported("cookies");

    public void iterationData() => Unsupported("iterationData");

    public void execution() => Unsupported("execution");

    /// <summary>
    /// The message a failed assertion shows. A CLR exception thrown inside a JS callback arrives
    /// wrapped, sometimes twice, and the useful sentence is at the bottom.
    /// </summary>
    private static string Unwrap(Exception ex)
    {
        var current = ex;

        while (current is System.Reflection.TargetInvocationException or Jint.Runtime.JavaScriptException
               && current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current.Message;
    }
}

/// <param name="Error">Null on a pass. On a failure this is what the tests pane shows.</param>
public sealed record PmTestResult(string Name, bool Passed, string? Error);

/// <summary>
/// <c>pm.environment</c> and friends. Set and unset write through to the live scope, which is what
/// makes the common "capture a token in one request, use it in the next" script work.
/// </summary>
public sealed class PmVariableScope(IDictionary<string, string> values)
{
    public object? get(string key) => values.TryGetValue(key, out var value) ? value : null;

    public void set(string key, object? value) => values[key] = value?.ToString() ?? string.Empty;

    public void unset(string key) => values.Remove(key);

    public bool has(string key) => values.ContainsKey(key);

    public void clear() => values.Clear();

    public string[] keys() => [.. values.Keys];

    public object? replaceIn(string template)
    {
        var result = template;

        foreach (var (key, value) in values)
        {
            result = result.Replace($"{{{{{key}}}}}", value, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>Postman's alias for get. Included because scripts in the wild use it.</summary>
    public object? toObject() => values.ToDictionary(p => p.Key, p => p.Value);
}

/// <summary><c>pm.response</c>.</summary>
public sealed class PmResponse(ExchangeResult result)
{
    public int code => result.Response?.Status ?? 0;

    public string status => result.Response?.ReasonPhrase ?? string.Empty;

    public double responseTime => result.Elapsed.TotalMilliseconds;

    public long responseSize => result.Response?.ContentLength ?? 0;

    public string text() => result.Response?.Body is { } body
        ? System.Text.Encoding.UTF8.GetString(body)
        : string.Empty;

    /// <summary>
    /// Parsed JSON as a plain dictionary tree, which Jint surfaces to the script as an ordinary
    /// object. Throws on a non-JSON body, matching Postman, so <c>pm.test</c> reports it as a
    /// failed assertion rather than a silent undefined.
    /// </summary>
    public object? json()
    {
        var text = this.text();

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("The response body is empty, so it is not JSON.");
        }

        using var document = JsonDocument.Parse(text);
        return Convert(document.RootElement);
    }

    public PmHeaders headers => new(result.Response?.Headers ?? []);

    /// <summary><c>pm.response.to.have.status(200)</c>.</summary>
    public PmResponseAssertion to => new(this);

    private static object? Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => Convert(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(Convert).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}

public sealed class PmHeaders(IReadOnlyList<KeyValuePair<string, string>> headers)
{
    public object? get(string name) => headers
        .FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
        .Value;

    public bool has(string name) =>
        headers.Any(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));

    public object[] all() => [.. headers.Select(h => new { key = h.Key, value = h.Value })];
}

/// <summary>The <c>pm.response.to.have.status(...)</c> chain, which is near-universal in the wild.</summary>
public sealed class PmResponseAssertion(PmResponse response)
{
    public PmResponseAssertion have => this;

    public PmResponseAssertion be => this;

    public void status(object expected)
    {
        var actual = response.code;

        if (expected is string name)
        {
            var matches = name.ToLowerInvariant() switch
            {
                "ok" => actual == 200,
                "created" => actual == 201,
                "accepted" => actual == 202,
                "nocontent" or "no content" => actual == 204,
                "badrequest" or "bad request" => actual == 400,
                "unauthorized" => actual == 401,
                "forbidden" => actual == 403,
                "notfound" or "not found" => actual == 404,
                _ => false,
            };

            if (!matches)
            {
                throw new PmAssertionException($"expected status {name} but got {actual}");
            }

            return;
        }

        var code = System.Convert.ToInt32(expected);
        if (actual != code)
        {
            throw new PmAssertionException($"expected status {code} but got {actual}");
        }
    }

    public void success()
    {
        if (response.code is < 200 or > 299)
        {
            throw new PmAssertionException($"expected a 2xx status but got {response.code}");
        }
    }

    public void header(string name)
    {
        if (!response.headers.has(name))
        {
            throw new PmAssertionException($"expected a {name} header, and there was none");
        }
    }
}

/// <summary><c>pm.request</c>. Read-only: a script that mutates the request mid-send is a trap.</summary>
public sealed class PmRequest(SentRequest request)
{
    public string method => request.Method;

    public string url => request.Url.ToString();

    public PmHeaders headers => new(request.Headers);

    public object? body => request.Body is { } bytes
        ? System.Text.Encoding.UTF8.GetString(bytes)
        : null;
}

public sealed class PmAssertionException(string message) : Exception(message);
