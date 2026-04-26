using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IdempotencyGuard.PostgreSql;

public class PostgresIdempotencyStore : IIdempotencyStore, IPurgableIdempotencyStore
{
    private readonly string _connectionString;
    private readonly PostgresIdempotencyOptions _options;
    private bool _tableCreated;

    public PostgresIdempotencyStore(IOptions<PostgresIdempotencyOptions> options)
    {
        _options = options.Value;
        _connectionString = _options.ConnectionString;
        SqlIdentifierValidator.ThrowIfUnsafe(_options.SchemaName, nameof(_options.SchemaName));
        SqlIdentifierValidator.ThrowIfUnsafe(_options.TableName, nameof(_options.TableName));
    }

    private string FullTableName => $"\"{_options.SchemaName}\".\"{_options.TableName}\"";

    public async Task<ClaimResult> TryClaimAsync(
        string key,
        string requestFingerprint,
        TimeSpan claimTtl,
        CancellationToken ct = default)
    {
        await EnsureTableCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(claimTtl);

        // Try to insert. If key exists, do nothing and return existing.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO {FullTableName} (key, fingerprint, state, claimed_at, expires_at)
            VALUES (@key, @fingerprint, 'claimed', @now, @expires)
            ON CONFLICT (key) DO UPDATE
                SET key = {FullTableName}.key  -- no-op update to return row
            RETURNING key, fingerprint, state, claimed_at, completed_at, expires_at,
                      status_code, headers_json, response_body,
                      (xmax = 0) AS is_inserted";

        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("fingerprint", requestFingerprint);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("expires", expiresAt);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return new ClaimResult.Claimed();
        }

        var isInserted = reader.GetBoolean(reader.GetOrdinal("is_inserted"));

        if (isInserted)
        {
            return new ClaimResult.Claimed();
        }

        var existingFingerprint = reader.GetString(reader.GetOrdinal("fingerprint"));
        var state = reader.GetString(reader.GetOrdinal("state"));
        var existingExpiresAt = reader.GetDateTime(reader.GetOrdinal("expires_at"));

        // Handle expired entries — treat as if the key doesn't exist
        if (existingExpiresAt < now)
        {
            await reader.CloseAsync();
            return await ReclaimExpiredAsync(conn, key, requestFingerprint, state, claimTtl, ct);
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
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE {FullTableName}
            SET state = 'completed',
                completed_at = @completed_at,
                expires_at = @expires_at,
                status_code = @status_code,
                headers_json = @headers,
                response_body = @body
            WHERE key = @key";

        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("completed_at", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("expires_at", DateTime.UtcNow.Add(responseTtl));
        cmd.Parameters.AddWithValue("status_code", response.StatusCode);
        cmd.Parameters.AddWithValue("headers", JsonSerializer.Serialize(response.Headers));
        cmd.Parameters.AddWithValue("body", GetBodyBytes(response.Body));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ReleaseClaimAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {FullTableName} WHERE key = @key AND state = 'claimed'";
        cmd.Parameters.AddWithValue("key", key);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT status_code, headers_json, response_body
            FROM {FullTableName}
            WHERE key = @key AND state = 'completed' AND expires_at > @now";

        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var statusCode = reader.GetInt32(0);
        var headersJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        ReadOnlyMemory<byte> body = reader.IsDBNull(2) ? default : (byte[])reader[2];

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
        NpgsqlConnection conn, string key, string fingerprint, string previousState, TimeSpan claimTtl, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE {FullTableName}
            SET fingerprint = @fingerprint, state = 'claimed',
                claimed_at = @now, expires_at = @expires,
                completed_at = NULL, status_code = NULL,
                headers_json = NULL, response_body = NULL
            WHERE key = @key AND state = @previous_state AND expires_at < @now";

        cmd.Parameters.AddWithValue("key", key);
        cmd.Parameters.AddWithValue("fingerprint", fingerprint);
        cmd.Parameters.AddWithValue("previous_state", previousState);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("expires", now.Add(claimTtl));

        var rows = await cmd.ExecuteNonQueryAsync(ct);

        if (rows > 0)
        {
            return new ClaimResult.Claimed();
        }

        // Someone else reclaimed it between our read and update — re-read to check fingerprint
        await using var readCmd = conn.CreateCommand();
        readCmd.CommandText = $"SELECT fingerprint, state FROM {FullTableName} WHERE key = @key";
        readCmd.Parameters.AddWithValue("key", key);

        await using var reader = await readCmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var winnerFingerprint = reader.GetString(0);
            var winnerState = reader.GetString(1);

            if (winnerFingerprint != fingerprint)
            {
                return new ClaimResult.FingerprintMismatch(winnerFingerprint, fingerprint);
            }

            var entry = new IdempotencyEntry
            {
                Key = key,
                RequestFingerprint = winnerFingerprint,
                State = winnerState == "completed" ? IdempotencyState.Completed : IdempotencyState.Claimed,
                ClaimedAtUtc = now
            };

            return winnerState == "completed"
                ? new ClaimResult.Completed(entry)
                : new ClaimResult.AlreadyClaimed(entry);
        }

        // Key was deleted between our update attempt and re-read — insert a fresh claim
        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = $@"
            INSERT INTO {FullTableName} (key, fingerprint, state, claimed_at, expires_at)
            VALUES (@key, @fingerprint, 'claimed', @now, @expires)
            ON CONFLICT (key) DO NOTHING";
        insertCmd.Parameters.AddWithValue("key", key);
        insertCmd.Parameters.AddWithValue("fingerprint", fingerprint);
        insertCmd.Parameters.AddWithValue("now", now);
        insertCmd.Parameters.AddWithValue("expires", now.Add(claimTtl));

        var inserted = await insertCmd.ExecuteNonQueryAsync(ct);
        if (inserted > 0)
        {
            return new ClaimResult.Claimed();
        }

        // Another request beat us to the insert — re-read to check fingerprint
        await using var rereadCmd = conn.CreateCommand();
        rereadCmd.CommandText = $"SELECT fingerprint, state FROM {FullTableName} WHERE key = @key";
        rereadCmd.Parameters.AddWithValue("key", key);

        await using var rereadReader = await rereadCmd.ExecuteReaderAsync(ct);
        if (await rereadReader.ReadAsync(ct))
        {
            var actualFingerprint = rereadReader.GetString(0);
            var actualState = rereadReader.GetString(1);

            if (actualFingerprint != fingerprint)
            {
                return new ClaimResult.FingerprintMismatch(actualFingerprint, fingerprint);
            }

            var rereadEntry = new IdempotencyEntry
            {
                Key = key,
                RequestFingerprint = actualFingerprint,
                State = actualState == "completed" ? IdempotencyState.Completed : IdempotencyState.Claimed,
                ClaimedAtUtc = now
            };

            return actualState == "completed"
                ? new ClaimResult.Completed(rereadEntry)
                : new ClaimResult.AlreadyClaimed(rereadEntry);
        }

        // Extremely unlikely: key disappeared again — give up with conflict
        return new ClaimResult.AlreadyClaimed(new IdempotencyEntry
        {
            Key = key,
            RequestFingerprint = fingerprint,
            State = IdempotencyState.Claimed,
            ClaimedAtUtc = now
        });
    }

    public async Task<int> PurgeExpiredAsync(int batchSize, CancellationToken ct = default)
    {
        await EnsureTableCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            DELETE FROM {FullTableName}
            WHERE key IN (
                SELECT key FROM {FullTableName}
                WHERE expires_at < @now
                LIMIT @batchSize
                FOR UPDATE SKIP LOCKED
            )";
        cmd.Parameters.AddWithValue("now", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("batchSize", batchSize);

        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureTableCreatedAsync(CancellationToken ct)
    {
        if (_tableCreated || !_options.AutoCreateTable)
        {
            return;
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS {FullTableName} (
                key          TEXT PRIMARY KEY,
                fingerprint  TEXT NOT NULL,
                state        TEXT NOT NULL DEFAULT 'claimed',
                claimed_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                completed_at TIMESTAMPTZ,
                expires_at   TIMESTAMPTZ NOT NULL,
                status_code  INT,
                headers_json TEXT,
                response_body BYTEA
            );

            CREATE INDEX IF NOT EXISTS ""idx_{_options.TableName}_expires""
                ON {FullTableName} (expires_at);";

        await cmd.ExecuteNonQueryAsync(ct);
        _tableCreated = true;
    }

    private static IdempotencyEntry ReadEntry(NpgsqlDataReader reader, string key)
    {
        return new IdempotencyEntry
        {
            Key = key,
            RequestFingerprint = reader.GetString(reader.GetOrdinal("fingerprint")),
            State = reader.GetString(reader.GetOrdinal("state")) == "completed"
                ? IdempotencyState.Completed
                : IdempotencyState.Claimed,
            ClaimedAtUtc = reader.GetDateTime(reader.GetOrdinal("claimed_at")),
            CompletedAtUtc = reader.IsDBNull(reader.GetOrdinal("completed_at"))
                ? null
                : reader.GetDateTime(reader.GetOrdinal("completed_at")),
            StatusCode = reader.IsDBNull(reader.GetOrdinal("status_code"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("status_code")),
            ResponseHeaders = reader.IsDBNull(reader.GetOrdinal("headers_json"))
                ? null
                : reader.GetString(reader.GetOrdinal("headers_json")),
            ResponseBody = reader.IsDBNull(reader.GetOrdinal("response_body"))
                ? null
                : (ReadOnlyMemory<byte>)(byte[])reader[reader.GetOrdinal("response_body")]
        };
    }

    private static byte[] GetBodyBytes(ReadOnlyMemory<byte> body)
    {
        if (MemoryMarshal.TryGetArray(body, out var segment)
            && segment.Offset == 0
            && segment.Count == segment.Array!.Length)
        {
            return segment.Array;
        }

        return body.ToArray();
    }
}
