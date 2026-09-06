using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using Courier.Core.Collections;
using Courier.Core.Privacy;
using Courier.Core.Storage;

namespace Courier.Core.Http;

/// <summary>
/// Sends one request and reports exactly what happened. CORE-01, CORE-11.
/// </summary>
/// <remarks>
/// Redirects and retries are handled here rather than by the handler, because CORE-11 makes them
/// per-request policy that the collection can supply defaults for, and because the response pane
/// has to be able to show the hops. The handler is configured with AllowAutoRedirect = false for
/// exactly that reason.
/// </remarks>
public sealed class RequestExecutor
{
    /// <summary>Bodies larger than this spill to the response cache instead of staying in memory. PERF-05.</summary>
    public const long SpillThresholdBytes = 5L * 1024 * 1024;

    private const string Initiator = nameof(RequestExecutor);

    private readonly EgressGate _gate;
    private readonly CookieJar _cookies;
    private readonly TraceInjector _trace;

    public RequestExecutor(EgressGate gate, CookieJar cookies, TraceInjector trace)
    {
        _gate = gate;
        _cookies = cookies;
        _trace = trace;
    }

    public async Task<ExchangeResult> SendAsync(PreparedRequest prepared, CancellationToken ct = default)
    {
        var settings = prepared.Settings.InheritFrom(RequestSettings.Defaults);
        var attempts = Math.Max(1, (settings.Retries ?? 0) + 1);
        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        ExchangeResult? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            last = await SendOnceAsync(prepared, settings, startedUtc, stopwatch, attempt, ct).ConfigureAwait(false);

            // A response is an answer, even a 500. Only a transport failure is worth retrying, and
            // only when the user asked for retries.
            if (last.Outcome != ExchangeOutcome.TransportFailure || attempt == attempts)
            {
                return last;
            }

            var backoff = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1));
            await Task.Delay(backoff, ct).ConfigureAwait(false);
        }

        return last!;
    }

    private async Task<ExchangeResult> SendOnceAsync(
        PreparedRequest prepared,
        RequestSettings settings,
        DateTimeOffset startedUtc,
        Stopwatch stopwatch,
        int attempt,
        CancellationToken ct)
    {
        var redirects = new List<RedirectHop>();
        var url = prepared.Url;
        var maxRedirects = settings.FollowRedirects == true ? settings.MaxRedirects ?? 10 : 0;

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(settings.TimeoutMilliseconds ?? 30_000));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        for (var hop = 0; ; hop++)
        {
            using var message = BuildMessage(prepared, url, settings);
            var traceId = _trace.Inject(message, prepared.InjectTraceParent);
            _cookies.ApplyTo(message, prepared.EnvironmentName);

            var sent = Describe(message, prepared.BodyBytes);

            try
            {
                var invoker = _gate.Authorize(url, EgressPurpose.TargetRequest, Initiator, prepared.HandlerProfile);

                using var response = await invoker
                    .SendAsync(message, linked.Token)
                    .ConfigureAwait(false);

                _cookies.Capture(response, prepared.EnvironmentName);

                if (hop < maxRedirects && TryGetRedirect(response, url, out var next))
                {
                    redirects.Add(new RedirectHop((int)response.StatusCode, url, next));
                    url = next;
                    continue;
                }

                var received = await ReadResponseAsync(response, linked.Token).ConfigureAwait(false);

                return new ExchangeResult
                {
                    Outcome = ExchangeOutcome.Completed,
                    Request = sent,
                    Response = received,
                    Elapsed = stopwatch.Elapsed,
                    StartedUtc = startedUtc,
                    TraceId = traceId,
                    Redirects = redirects,
                    Attempts = attempt,
                };
            }
            catch (EgressDeniedException denied)
            {
                return Failed(
                    sent,
                    stopwatch,
                    startedUtc,
                    attempt,
                    redirects,
                    ExchangeOutcome.Refused,
                    new TransportFailure(TransportFailureKind.Unknown, denied.Message));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                return Failed(
                    sent,
                    stopwatch,
                    startedUtc,
                    attempt,
                    redirects,
                    ExchangeOutcome.TransportFailure,
                    new TransportFailure(
                        TransportFailureKind.Timeout,
                        $"No response within {settings.TimeoutMilliseconds} ms. Raise the timeout for this request, or check the host is reachable.",
                        $"The request left this machine and nothing came back before the deadline."));
            }
            catch (OperationCanceledException)
            {
                return Failed(
                    sent,
                    stopwatch,
                    startedUtc,
                    attempt,
                    redirects,
                    ExchangeOutcome.Cancelled,
                    null);
            }
            catch (HttpRequestException ex)
            {
                return Failed(
                    sent,
                    stopwatch,
                    startedUtc,
                    attempt,
                    redirects,
                    ExchangeOutcome.TransportFailure,
                    Explain(ex, url));
            }
        }
    }

    private static ExchangeResult Failed(
        SentRequest sent,
        Stopwatch stopwatch,
        DateTimeOffset startedUtc,
        int attempt,
        IReadOnlyList<RedirectHop> redirects,
        ExchangeOutcome outcome,
        TransportFailure? failure) => new()
        {
            Outcome = outcome,
            Request = sent,
            Failure = failure,
            Elapsed = stopwatch.Elapsed,
            StartedUtc = startedUtc,
            Redirects = redirects,
            Attempts = attempt,
        };

    private HttpRequestMessage BuildMessage(PreparedRequest prepared, Uri url, RequestSettings settings)
    {
        var message = new HttpRequestMessage(new HttpMethod(prepared.Method), url)
        {
            Version = ParseVersion(settings.HttpVersion),
            VersionPolicy = settings.HttpVersion is null
                ? HttpVersionPolicy.RequestVersionOrLower
                : HttpVersionPolicy.RequestVersionExact,
        };

        if (prepared.BodyBytes is { Length: > 0 })
        {
            message.Content = new ByteArrayContent(prepared.BodyBytes);
            if (!string.IsNullOrEmpty(prepared.ContentType))
            {
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(prepared.ContentType);
            }
        }

        foreach (var (name, value) in prepared.Headers)
        {
            // Content headers must go on the content, not the request, or HttpClient rejects them.
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    private static Version ParseVersion(string? version) => version switch
    {
        "1.0" => HttpVersion.Version10,
        "1.1" => HttpVersion.Version11,
        "2" or "2.0" => HttpVersion.Version20,
        "3" or "3.0" => HttpVersion.Version30,
        _ => HttpVersion.Version20,
    };

    private static bool TryGetRedirect(HttpResponseMessage response, Uri current, out Uri target)
    {
        target = current;

        if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
        {
            return false;
        }

        var location = response.Headers.Location;
        if (location is null)
        {
            return false;
        }

        target = location.IsAbsoluteUri ? location : new Uri(current, location);
        return true;
    }

    private static async Task<ReceivedResponse> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var headers = response.Headers
            .Concat(response.Content.Headers)
            .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
            .ToList();

        var declaredLength = response.Content.Headers.ContentLength ?? -1;
        byte[]? body = null;
        string? bodyPath = null;
        long length;

        if (declaredLength >= 0 && declaredLength < RequestExecutor.SpillThresholdBytes)
        {
            body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            length = body.LongLength;
        }
        else
        {
            // Unknown or large. Stream to the response cache and render from a memory-mapped view;
            // never materialize a 40MB payload on the managed heap. PERF-05.
            Directory.CreateDirectory(StorageLocations.ResponseCache);
            bodyPath = Path.Combine(StorageLocations.ResponseCache, $"{Guid.NewGuid():n}.body");

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var destination = new FileStream(
                bodyPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81_920,
                useAsync: true);

            await source.CopyToAsync(destination, ct).ConfigureAwait(false);
            length = destination.Length;

            // Small after all: pull it back into memory and drop the file, so the common case does
            // not leave litter behind.
            if (length < SpillThresholdBytes)
            {
                await destination.DisposeAsync().ConfigureAwait(false);
                body = await File.ReadAllBytesAsync(bodyPath, ct).ConfigureAwait(false);
                TryDelete(bodyPath);
                bodyPath = null;
            }
        }

        return new ReceivedResponse(
            response.StatusCode,
            response.ReasonPhrase ?? string.Empty,
            headers,
            body,
            bodyPath,
            length,
            response.Content.Headers.ContentType?.ToString(),
            response.Version);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A file left in the cache is harmless; the storage page can clear it.
        }
    }

    private static SentRequest Describe(HttpRequestMessage message, byte[]? body) => new(
        message.Method.Method,
        message.RequestUri!,
        [
            .. message.Headers.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))),
            .. message.Content?.Headers.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
               ?? [],
        ],
        body,
        message.Content?.Headers.ContentType?.ToString(),
        message.Version);

    /// <summary>
    /// Turns an exception into a sentence that says what happened and what to do about it.
    /// UI_SPEC 3.7: the interface never apologises and never hedges.
    /// </summary>
    private static TransportFailure Explain(HttpRequestException ex, Uri url)
    {
        var host = url.Host;

        if (ex.InnerException is AuthenticationException authEx)
        {
            return new TransportFailure(
                TransportFailureKind.TlsHandshake,
                $"The TLS handshake with {host} failed. Check the trust and network settings for this host.",
                authEx.Message);
        }

        if (ex.InnerException is SocketException socketEx)
        {
            return socketEx.SocketErrorCode switch
            {
                SocketError.HostNotFound => new TransportFailure(
                    TransportFailureKind.NameResolution,
                    $"{host} could not be resolved. Check the spelling, your VPN, and whether this host is internal-only.",
                    socketEx.Message),

                SocketError.ConnectionRefused => new TransportFailure(
                    TransportFailureKind.ConnectionRefused,
                    $"{host} refused the connection on port {url.Port}. Nothing is listening there.",
                    socketEx.Message),

                SocketError.ConnectionReset => new TransportFailure(
                    TransportFailureKind.ConnectionReset,
                    $"{host} closed the connection before answering.",
                    socketEx.Message),

                SocketError.TimedOut => new TransportFailure(
                    TransportFailureKind.Timeout,
                    $"{host} did not answer in time.",
                    socketEx.Message),

                _ => new TransportFailure(
                    TransportFailureKind.Unknown,
                    $"The request to {host} never completed. {socketEx.Message}",
                    socketEx.Message),
            };
        }

        if (ex.HttpRequestError == HttpRequestError.ProxyTunnelError)
        {
            return new TransportFailure(
                TransportFailureKind.ProxyUnreachable,
                "The proxy refused to open a tunnel to this host. Check the proxy settings and the bypass list.",
                ex.Message);
        }

        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return new TransportFailure(
                TransportFailureKind.CertificateNotTrusted,
                $"The certificate presented by {host} is not trusted by this machine. "
                + "Add its root CA to the machine store, or allow it for this host only.",
                ex.Message);
        }

        return new TransportFailure(
            TransportFailureKind.Unknown,
            $"The request to {host} never left, or nothing came back. {ex.Message}",
            ex.InnerException?.Message);
    }
}
