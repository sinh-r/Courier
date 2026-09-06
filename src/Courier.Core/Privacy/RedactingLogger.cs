using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Courier.Core.Privacy;

/// <summary>
/// A logger provider that cannot emit a secret. SEC-07: no secret value is ever written to a log
/// file, including at verbose logging levels.
/// </summary>
/// <remarks>
/// Two mechanisms, because either alone leaks. Pattern scrubbing catches tokens and connection
/// strings that appear in text nobody thought to guard. The known-value set catches the case
/// patterns miss entirely — a password like "letmein" is invisible to every heuristic, but Courier
/// knows it is a secret because it read it out of the credential store.
/// </remarks>
public sealed class RedactingLoggerProvider : ILoggerProvider
{
    private readonly ILoggerProvider _inner;
    private readonly SecretRegistry _registry;

    public RedactingLoggerProvider(ILoggerProvider inner, SecretRegistry registry)
    {
        _inner = inner;
        _registry = registry;
    }

    public ILogger CreateLogger(string categoryName) =>
        new RedactingLogger(_inner.CreateLogger(categoryName), _registry);

    public void Dispose() => _inner.Dispose();

    private sealed class RedactingLogger(ILogger inner, SecretRegistry registry) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            inner.Log(
                logLevel,
                eventId,
                state,
                exception,
                (s, e) => Redact(formatter(s, e)));
        }

        private string Redact(string message) => SecretPatterns.Scrub(registry.Mask(message));
    }
}

/// <summary>
/// The set of literal secret values this process has handled, so they can be masked wherever they
/// turn up. Values are held only in memory, never persisted, and cleared on sign-out.
/// </summary>
/// <remarks>
/// Storing the secrets in order to hide them looks backwards, but the alternative is worse: without
/// this, a token pasted into a header is scrubbed by pattern while a short API key is logged in
/// full. Anything registered here is already in memory because Courier is about to send it.
/// </remarks>
public sealed class SecretRegistry
{
    private const int MinimumMaskableLength = 6;

    private readonly ConcurrentDictionary<string, byte> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers a value to be masked from all future log output. Values shorter than six
    /// characters are ignored: masking those would corrupt ordinary log text without protecting
    /// anything a determined reader could not guess.
    /// </summary>
    public void Remember(string? value)
    {
        if (!string.IsNullOrEmpty(value) && value.Length >= MinimumMaskableLength)
        {
            _values.TryAdd(value, 0);
        }
    }

    public void Forget(string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _values.TryRemove(value, out _);
        }
    }

    public void Clear() => _values.Clear();

    public int Count => _values.Count;

    /// <summary>Replaces every known secret value in the text. Ordinal, longest first.</summary>
    public string Mask(string text)
    {
        if (string.IsNullOrEmpty(text) || _values.IsEmpty)
        {
            return text;
        }

        var result = text;
        foreach (var secret in _values.Keys.OrderByDescending(v => v.Length))
        {
            if (result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        return result;
    }
}
