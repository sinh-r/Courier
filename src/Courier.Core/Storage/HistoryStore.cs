using System.Text.Json;
using Courier.Core.Http;
using Microsoft.Data.Sqlite;

namespace Courier.Core.Storage;

/// <summary>
/// Local, searchable request history with replay. CORE-06.
/// </summary>
/// <remarks>
/// Bodies are kept so a history row can be replayed and turned into a capsule without re-sending.
/// A body already spilled to the response cache is referenced by path rather than copied into the
/// database, which is what keeps a session full of 40MB responses from producing a 4GB history file.
/// </remarks>
public sealed class HistoryStore
{
    private readonly CourierDatabase _database;

    public HistoryStore(CourierDatabase database) => _database = database;

    public async Task<long> RecordAsync(
        ExchangeResult result,
        string? environmentName,
        string? collectionName,
        string? requestId,
        CancellationToken ct = default)
    {
        await using var command = _database.CreateCommand(
            """
            INSERT INTO history (
                started_utc, method, url, path, status, outcome, elapsed_ms, response_bytes,
                environment, collection, request_id, trace_id, failure_kind, failure_message,
                request_headers, request_body, response_headers, response_body, response_body_path)
            VALUES (
                $started, $method, $url, $path, $status, $outcome, $elapsed, $bytes,
                $environment, $collection, $requestId, $traceId, $failureKind, $failureMessage,
                $requestHeaders, $requestBody, $responseHeaders, $responseBody, $responseBodyPath);
            SELECT last_insert_rowid();
            """);

        command.Parameters.AddWithValue("$started", result.StartedUtc.ToString("O"));
        command.Parameters.AddWithValue("$method", result.Request.Method);
        command.Parameters.AddWithValue("$url", result.Request.Url.ToString());
        command.Parameters.AddWithValue("$path", result.Request.Url.AbsolutePath);
        command.Parameters.AddWithValue("$status", (object?)result.Response?.Status ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", result.Outcome.ToString());
        command.Parameters.AddWithValue("$elapsed", (long)result.Elapsed.TotalMilliseconds);
        command.Parameters.AddWithValue("$bytes", result.Response?.ContentLength ?? 0);
        command.Parameters.AddWithValue("$environment", (object?)environmentName ?? DBNull.Value);
        command.Parameters.AddWithValue("$collection", (object?)collectionName ?? DBNull.Value);
        command.Parameters.AddWithValue("$requestId", (object?)requestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$traceId", (object?)result.TraceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureKind", (object?)result.Failure?.Kind.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureMessage", (object?)result.Failure?.Explanation ?? DBNull.Value);
        command.Parameters.AddWithValue("$requestHeaders", JsonSerializer.Serialize(result.Request.Headers));
        command.Parameters.AddWithValue("$requestBody", (object?)result.Request.Body ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$responseHeaders",
            result.Response is null ? DBNull.Value : JsonSerializer.Serialize(result.Response.Headers));
        command.Parameters.AddWithValue("$responseBody", (object?)result.Response?.Body ?? DBNull.Value);
        command.Parameters.AddWithValue("$responseBodyPath", (object?)result.Response?.BodyPath ?? DBNull.Value);

        var id = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(id);
    }

    /// <summary>
    /// The history pane. Filtering happens in SQL so a 1,204-row list stays instant, and the
    /// "this tab" variant in the inspector is the same query with a request id.
    /// </summary>
    public async Task<IReadOnlyList<HistoryEntry>> QueryAsync(
        HistoryQuery query,
        CancellationToken ct = default)
    {
        var sql = """
            SELECT id, started_utc, method, url, path, status, outcome, elapsed_ms, response_bytes,
                   environment, collection, request_id, trace_id, failure_kind, failure_message
            FROM history
            WHERE ($requestId IS NULL OR request_id = $requestId)
              AND ($environment IS NULL OR environment = $environment)
              AND ($text IS NULL OR url LIKE '%' || $text || '%' OR method LIKE '%' || $text || '%')
              AND ($since IS NULL OR started_utc >= $since)
            ORDER BY started_utc DESC
            LIMIT $limit;
            """;

        await using var command = _database.CreateCommand(sql);
        command.Parameters.AddWithValue("$requestId", (object?)query.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$environment", (object?)query.EnvironmentName ?? DBNull.Value);
        command.Parameters.AddWithValue("$text", (object?)query.Text ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$since",
            query.Since is null ? DBNull.Value : query.Since.Value.ToString("O"));
        command.Parameters.AddWithValue("$limit", query.Limit);

        var entries = new List<HistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(Read(reader));
        }

        return entries;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var command = _database.CreateCommand("SELECT COUNT(*) FROM history;");
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Everything needed to replay a request from history. CORE-06.</summary>
    public async Task<HistoryPayload?> GetPayloadAsync(long id, CancellationToken ct = default)
    {
        await using var command = _database.CreateCommand(
            """
            SELECT method, url, request_headers, request_body, response_headers, response_body,
                   response_body_path
            FROM history WHERE id = $id;
            """);

        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new HistoryPayload(
            reader.GetString(0),
            new Uri(reader.GetString(1)),
            Deserialize(reader.IsDBNull(2) ? null : reader.GetString(2)),
            reader.IsDBNull(3) ? null : (byte[])reader[3],
            Deserialize(reader.IsDBNull(4) ? null : reader.GetString(4)),
            reader.IsDBNull(5) ? null : (byte[])reader[5],
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    /// <summary>Trims history to the configurable retention window. CORE-06.</summary>
    public async Task<int> PruneAsync(TimeSpan retention, CancellationToken ct = default)
    {
        await using var command = _database.CreateCommand(
            "DELETE FROM history WHERE started_utc < $cutoff;");

        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(retention).ToString("O"));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static HistoryEntry Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        DateTimeOffset.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        Enum.Parse<ExchangeOutcome>(reader.GetString(6)),
        TimeSpan.FromMilliseconds(reader.GetInt64(7)),
        reader.GetInt64(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetString(13),
        reader.IsDBNull(14) ? null : reader.GetString(14));

    private static IReadOnlyList<KeyValuePair<string, string>> Deserialize(string? json) =>
        json is null
            ? []
            : JsonSerializer.Deserialize<List<KeyValuePair<string, string>>>(json) ?? [];
}

public sealed record HistoryQuery
{
    public string? RequestId { get; init; }

    public string? EnvironmentName { get; init; }

    public string? Text { get; init; }

    public DateTimeOffset? Since { get; init; }

    public int Limit { get; init; } = 500;
}

/// <param name="Status">Null when the request never left. The pane shows a glyph, not a number.</param>
public sealed record HistoryEntry(
    long Id,
    DateTimeOffset StartedUtc,
    string Method,
    string Url,
    string Path,
    int? Status,
    ExchangeOutcome Outcome,
    TimeSpan Elapsed,
    long ResponseBytes,
    string? EnvironmentName,
    string? CollectionName,
    string? RequestId,
    string? TraceId,
    string? FailureKind,
    string? FailureMessage);

public sealed record HistoryPayload(
    string Method,
    Uri Url,
    IReadOnlyList<KeyValuePair<string, string>> RequestHeaders,
    byte[]? RequestBody,
    IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders,
    byte[]? ResponseBody,
    string? ResponseBodyPath);
