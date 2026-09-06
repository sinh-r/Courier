using Microsoft.Data.Sqlite;

namespace Courier.Core.Storage;

/// <summary>
/// The local database: history, response cache metadata, telemetry results and suspended tab state.
/// STOR-03 — explicitly outside any git tree, because putting history in git would be miserable.
/// </summary>
/// <remarks>
/// Opened lazily and migrated on first use. Startup must not touch this: PERF-01 gives 1.5s to
/// interactive, and a schema check on a cold HDD-backed corporate image is not free.
/// </remarks>
public sealed class CourierDatabase : IDisposable, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private bool _migrated;

    private CourierDatabase(SqliteConnection connection) => _connection = connection;

    public static async Task<CourierDatabase> OpenAsync(string? path = null, CancellationToken ct = default)
    {
        StorageLocations.EnsureCreated();

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path ?? StorageLocations.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString());

        await connection.OpenAsync(ct).ConfigureAwait(false);

        var database = new CourierDatabase(connection);
        await database.MigrateAsync(ct).ConfigureAwait(false);
        return database;
    }

    /// <summary>An in-memory database for tests. Same schema, no file, no cleanup.</summary>
    public static async Task<CourierDatabase> OpenInMemoryAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var database = new CourierDatabase(connection);
        await database.MigrateAsync(ct).ConfigureAwait(false);
        return database;
    }

    internal SqliteConnection Connection => _connection;

    private async Task MigrateAsync(CancellationToken ct)
    {
        if (_migrated)
        {
            return;
        }

        await ExecuteAsync(
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS history (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                started_utc       TEXT    NOT NULL,
                method            TEXT    NOT NULL,
                url               TEXT    NOT NULL,
                path              TEXT    NOT NULL,
                status            INTEGER NULL,
                outcome           TEXT    NOT NULL,
                elapsed_ms        INTEGER NOT NULL,
                response_bytes    INTEGER NOT NULL,
                environment       TEXT    NULL,
                collection        TEXT    NULL,
                request_id        TEXT    NULL,
                trace_id          TEXT    NULL,
                failure_kind      TEXT    NULL,
                failure_message   TEXT    NULL,
                request_headers   TEXT    NULL,
                request_body      BLOB    NULL,
                response_headers  TEXT    NULL,
                response_body     BLOB    NULL,
                response_body_path TEXT   NULL
            );

            CREATE INDEX IF NOT EXISTS ix_history_started ON history (started_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_history_path    ON history (path);
            CREATE INDEX IF NOT EXISTS ix_history_trace   ON history (trace_id);

            CREATE TABLE IF NOT EXISTS tab_state (
                tab_id       TEXT PRIMARY KEY,
                ordinal      INTEGER NOT NULL,
                saved_utc    TEXT    NOT NULL,
                payload      TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS telemetry_cache (
                trace_id     TEXT PRIMARY KEY,
                backend      TEXT NOT NULL,
                fetched_utc  TEXT NOT NULL,
                payload      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS scan_cache (
                collection   TEXT NOT NULL,
                file_path    TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                scanned_utc  TEXT NOT NULL,
                PRIMARY KEY (collection, file_path)
            );

            CREATE TABLE IF NOT EXISTS host_trust (
                host        TEXT PRIMARY KEY,
                thumbprint  TEXT NOT NULL,
                reason      TEXT NOT NULL,
                recorded_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS cookies (
                environment TEXT NOT NULL,
                name        TEXT NOT NULL,
                domain      TEXT NOT NULL,
                path        TEXT NOT NULL,
                value       TEXT NOT NULL,
                expires_utc TEXT NULL,
                secure      INTEGER NOT NULL,
                http_only   INTEGER NOT NULL,
                PRIMARY KEY (environment, name, domain, path)
            );
            """,
            ct).ConfigureAwait(false);

        _migrated = true;
    }

    public async Task ExecuteAsync(string sql, CancellationToken ct = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public SqliteCommand CreateCommand(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    /// <summary>Deletes everything in one category. Backs the per-category clear on SEC-05's page.</summary>
    public async Task ClearAsync(StorageCategory category, CancellationToken ct = default)
    {
        var sql = category switch
        {
            StorageCategory.History => "DELETE FROM history;",
            StorageCategory.TelemetryResults => "DELETE FROM telemetry_cache;",
            StorageCategory.SessionState => "DELETE FROM tab_state;",
            StorageCategory.ScanCache => "DELETE FROM scan_cache;",
            StorageCategory.Cookies => "DELETE FROM cookies;",
            StorageCategory.HostTrustExceptions => "DELETE FROM host_trust;",
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };

        await ExecuteAsync(sql, ct).ConfigureAwait(false);
        await ExecuteAsync("VACUUM;", ct).ConfigureAwait(false);
    }

    public void Dispose() => _connection.Dispose();

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

public enum StorageCategory
{
    History,
    TelemetryResults,
    SessionState,
    ScanCache,
    Cookies,
    HostTrustExceptions,
}
