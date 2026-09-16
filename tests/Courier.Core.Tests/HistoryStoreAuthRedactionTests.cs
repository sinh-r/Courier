using System.Net;
using Courier.Core.Http;
using Courier.Core.Storage;

namespace Courier.Core.Tests;

/// <summary>
/// History is local, but SEC-07's "never in a log line" still applies to it: a token sent once
/// should not sit in plaintext in every future history export or screen share.
/// </summary>
public sealed class HistoryStoreAuthRedactionTests
{
    private static ExchangeResult Result(
        IReadOnlyList<KeyValuePair<string, string>> requestHeaders,
        IReadOnlyList<KeyValuePair<string, string>>? responseHeaders = null) => new()
    {
        Outcome = ExchangeOutcome.Completed,
        Request = new SentRequest("GET", new Uri("https://api.example/orders"), requestHeaders, null, null, new Version(1, 1)),
        Response = new ReceivedResponse(HttpStatusCode.OK, "OK", responseHeaders ?? [], null, null, 0, "application/json", new Version(1, 1)),
        Elapsed = TimeSpan.FromMilliseconds(42),
        StartedUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task An_Authorization_header_is_redacted_even_without_being_named_as_a_secret_value()
    {
        await using var database = await CourierDatabase.OpenInMemoryAsync(TestContext.Current.CancellationToken);
        var store = new HistoryStore(database);

        var id = await store.RecordAsync(
            Result([new KeyValuePair<string, string>("Authorization", "Bearer eyJhbGciOiJIUzI1NiJ9.abc.def")]),
            environmentName: null, collectionName: null, requestId: null,
            ct: TestContext.Current.CancellationToken);

        var payload = await store.GetPayloadAsync(id, TestContext.Current.CancellationToken);

        var authorization = payload!.RequestHeaders.Single(h => h.Key == "Authorization").Value;
        Assert.DoesNotContain("eyJ", authorization, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_reported_as_secret_by_the_auth_provider_is_redacted_wherever_it_appears()
    {
        await using var database = await CourierDatabase.OpenInMemoryAsync(TestContext.Current.CancellationToken);
        var store = new HistoryStore(database);

        // A custom header name the pattern list would not recognise on its own — this is exactly
        // what PreparedRequest.SecretValues exists to catch.
        var id = await store.RecordAsync(
            Result([new KeyValuePair<string, string>("X-Service-Credential", "s3cr3t-value")]),
            environmentName: null, collectionName: null, requestId: null,
            secretValues: ["s3cr3t-value"],
            ct: TestContext.Current.CancellationToken);

        var payload = await store.GetPayloadAsync(id, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("s3cr3t-value", payload!.RequestHeaders.Single(h => h.Key == "X-Service-Credential").Value);
    }

    [Fact]
    public async Task An_ordinary_header_is_left_exactly_as_sent()
    {
        await using var database = await CourierDatabase.OpenInMemoryAsync(TestContext.Current.CancellationToken);
        var store = new HistoryStore(database);

        var id = await store.RecordAsync(
            Result([new KeyValuePair<string, string>("Accept", "application/json")]),
            environmentName: null, collectionName: null, requestId: null,
            ct: TestContext.Current.CancellationToken);

        var payload = await store.GetPayloadAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal("application/json", payload!.RequestHeaders.Single(h => h.Key == "Accept").Value);
    }

    [Fact]
    public async Task A_Set_Cookie_response_header_is_redacted_too()
    {
        await using var database = await CourierDatabase.OpenInMemoryAsync(TestContext.Current.CancellationToken);
        var store = new HistoryStore(database);

        var id = await store.RecordAsync(
            Result([], [new KeyValuePair<string, string>("Set-Cookie", "session=abc123; HttpOnly")]),
            environmentName: null, collectionName: null, requestId: null,
            ct: TestContext.Current.CancellationToken);

        var payload = await store.GetPayloadAsync(id, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("abc123", payload!.ResponseHeaders.Single(h => h.Key == "Set-Cookie").Value);
    }
}
