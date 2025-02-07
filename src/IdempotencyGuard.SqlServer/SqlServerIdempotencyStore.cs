using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.SqlServer;

public class SqlServerIdempotencyStore : IIdempotencyStore
{
    private readonly string _connectionString;
    private readonly SqlServerIdempotencyOptions _options;
    private bool _tableCreated;

    public SqlServerIdempotencyStore(IOptions<SqlServerIdempotencyOptions> options)
    {
        _options = options.Value;
        _connectionString = _options.ConnectionString;
    }

    private string FullTableName => $"[{_options.SchemaName}].[{_options.TableName}]";

    public async Task<ClaimResult> TryClaimAsync(
        string key,
        string requestFingerprint,
        TimeSpan claimTtl,
        CancellationToken ct = default)
    {
        await EnsureTableCreatedAsync(ct);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(claimTtl);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            MERGE {FullTableName} WITH (HOLDLOCK) AS target
            USING (SELECT @key AS [Key]) AS source
            ON target.[Key] = source.[Key]
            WHEN NOT MATCHED THEN
                INSERT ([Key], Fingerprint, State, ClaimedAtUtc, ExpiresAtUtc)
                VALUES (@key, @fingerprint, 'claimed', @now, @expires)
            OUTPUT $action AS MergeAction,
                   inserted.[Key], inserted.Fingerprint, inserted.State,
                   inserted.ClaimedAtUtc, inserted.CompletedAtUtc, inserted.ExpiresAtUtc,
                   inserted.StatusCode, inserted.HeadersJson, inserted.ResponseBody;

            IF @@ROWCOUNT = 0
            BEGIN
                SELECT 'EXISTING' AS MergeAction,
                       [Key], Fingerprint, State, ClaimedAtUtc, CompletedAtUtc, ExpiresAtUtc,
                       StatusCode, HeadersJson, ResponseBody
                FROM {FullTableName}
                WHERE [Key] = @key;
            END";

        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@fingerprint", requestFingerprint);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@expires", expiresAt);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return new ClaimResult.Claimed();
        }

        var action = reader.GetString(reader.GetOrdinal("MergeAction"));

        if (action == "INSERT")
        {
            return new ClaimResult.Claimed();
        }

        var existingFingerprint = reader.GetString(reader.GetOrdinal("Fingerprint"));
        var state = reader.GetString(reader.GetOrdinal("State"));
        var existingExpiresAt = reader.GetDateTime(reader.GetOrdinal("ExpiresAtUtc"));

        if (state == "claimed" && existingExpiresAt < now)
        {
            await reader.CloseAsync();
            return await ReclaimExpiredAsync(conn, key, requestFingerprint, claimTtl, ct);
        }

        if (existingFingerprint != requestFingerprint)
        {
            return new ClaimResult.FingerprintMismatch(existingFingerprint, requestFingerprint);
        }

        if (state == "completed")
        {
            var entry = ReadEntry(reader, key);
            return new ClaimResult.Completed(entry);
        }

        var claimedEntry = ReadEntry(reader, key);
        return new ClaimResult.AlreadyClaimed(claimedEntry);
    }

    public async Task SetResponseAsync(
        string key,
        IdempotentResponse response,
        TimeSpan responseTtl,
        CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE {FullTableName}
            SET State = 'completed',
                CompletedAtUtc = @completed_at,
                ExpiresAtUtc = @expires_at,
                StatusCode = @status_code,
                HeadersJson = @headers,
                ResponseBody = @body
            WHERE [Key] = @key";

        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@completed_at", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("@expires_at", DateTime.UtcNow.Add(responseTtl));
        cmd.Parameters.AddWithValue("@status_code", response.StatusCode);
        cmd.Parameters.AddWithValue("@headers", JsonSerializer.Serialize(response.Headers));
        cmd.Parameters.AddWithValue("@body", response.Body);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReleaseClaimAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {FullTableName} WHERE [Key] = @key AND State = 'claimed'";
        cmd.Parameters.AddWithValue("@key", key);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT StatusCode, HeadersJson, ResponseBody
            FROM {FullTableName}
            WHERE [Key] = @key AND State = 'completed' AND ExpiresAtUtc > @now";

        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var statusCode = reader.GetInt32(0);
        var headersJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        var body = reader.IsDBNull(2) ? [] : (byte[])reader[2];

        return new IdempotentResponse
        {
            StatusCode = statusCode,
            Headers = headersJson is not null
                ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(headersJson)!
                : new Dictionary<string, string[]>(),
            Body = body
        };
    }

    private async Task<ClaimResult> ReclaimExpiredAsync(
        SqlConnection conn, string key, string fingerprint, TimeSpan claimTtl, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE {FullTableName}
            SET Fingerprint = @fingerprint, State = 'claimed',
                ClaimedAtUtc = @now, ExpiresAtUtc = @expires,
                CompletedAtUtc = NULL, StatusCode = NULL,
                HeadersJson = NULL, ResponseBody = NULL
            WHERE [Key] = @key AND State = 'claimed' AND ExpiresAtUtc < @now";

        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@fingerprint", fingerprint);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@expires", now.Add(claimTtl));

        var rows = await cmd.ExecuteNonQueryAsync(ct);

        if (rows > 0)
        {
            return new ClaimResult.Claimed();
        }

        return new ClaimResult.AlreadyClaimed(new IdempotencyEntry
        {
            Key = key,
            RequestFingerprint = fingerprint,
            State = IdempotencyState.Claimed,
            ClaimedAtUtc = now
        });
    }

    private async Task EnsureTableCreatedAsync(CancellationToken ct)
    {
        if (_tableCreated || !_options.AutoCreateTable)
        {
            return;
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '{_options.TableName}' AND schema_id = SCHEMA_ID('{_options.SchemaName}'))
            BEGIN
                CREATE TABLE {FullTableName} (
                    [Key]           NVARCHAR(256)   NOT NULL PRIMARY KEY,
                    Fingerprint     NVARCHAR(128)   NOT NULL,
                    State           NVARCHAR(20)    NOT NULL DEFAULT 'claimed',
                    ClaimedAtUtc    DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
                    CompletedAtUtc  DATETIME2       NULL,
                    ExpiresAtUtc    DATETIME2       NOT NULL,
                    StatusCode      INT             NULL,
                    HeadersJson     NVARCHAR(MAX)   NULL,
                    ResponseBody    VARBINARY(MAX)  NULL
                );

                CREATE NONCLUSTERED INDEX IX_{_options.TableName}_Expires
                    ON {FullTableName} (ExpiresAtUtc)
                    WHERE State = 'claimed';
            END";

        await cmd.ExecuteNonQueryAsync(ct);
        _tableCreated = true;
    }

    private static IdempotencyEntry ReadEntry(SqlDataReader reader, string key)
    {
        return new IdempotencyEntry
        {
            Key = key,
            RequestFingerprint = reader.GetString(reader.GetOrdinal("Fingerprint")),
            State = reader.GetString(reader.GetOrdinal("State")) == "completed"
                ? IdempotencyState.Completed
                : IdempotencyState.Claimed,
            ClaimedAtUtc = reader.GetDateTime(reader.GetOrdinal("ClaimedAtUtc")),
            CompletedAtUtc = reader.IsDBNull(reader.GetOrdinal("CompletedAtUtc"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("CompletedAtUtc")),
            StatusCode = reader.IsDBNull(reader.GetOrdinal("StatusCode"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("StatusCode")),
            ResponseHeaders = reader.IsDBNull(reader.GetOrdinal("HeadersJson"))
                ? null
                : reader.GetString(reader.GetOrdinal("HeadersJson")),
            ResponseBody = reader.IsDBNull(reader.GetOrdinal("ResponseBody"))
                ? null
                : (byte[])reader[reader.GetOrdinal("ResponseBody")]
        };
    }
}
