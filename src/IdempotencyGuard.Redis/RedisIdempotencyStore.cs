using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IdempotencyGuard.Redis;

public class RedisIdempotencyStore : IIdempotencyStore
{
    private readonly RedisConnectionManager _redis;
    private readonly RedisIdempotencyOptions _options;

    private static readonly LuaScript ClaimScript = LuaScript.Prepare(LoadScript("claim.lua"));
    private static readonly LuaScript CompleteScript = LuaScript.Prepare(LoadScript("complete.lua"));

    internal RedisIdempotencyStore(RedisConnectionManager redis, IOptions<RedisIdempotencyOptions> options)
    {
        _redis = redis;
        _options = options.Value;
    }

    public async Task<ClaimResult> TryClaimAsync(
        string key,
        string requestFingerprint,
        TimeSpan claimTtl,
        CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var redisKey = $"{_options.KeyPrefix}{key}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        var result = await db.ScriptEvaluateAsync(ClaimScript, new
        {
            key = (RedisKey)redisKey,
            fingerprint = requestFingerprint,
            claim_ttl_ms = (long)claimTtl.TotalMilliseconds,
            current_timestamp = timestamp
        });

        if (result.IsNull)
        {
            return new ClaimResult.Claimed();
        }

        var existingJson = (string)result!;
        var existing = JsonSerializer.Deserialize<RedisEntry>(existingJson);

        if (existing is null)
        {
            return new ClaimResult.Claimed();
        }

        if (existing.State == "completed")
        {
            if (existing.Fingerprint != requestFingerprint)
            {
                return new ClaimResult.FingerprintMismatch(existing.Fingerprint, requestFingerprint);
            }

            var entry = ToIdempotencyEntry(key, existing);
            return new ClaimResult.Completed(entry);
        }

        if (existing.Fingerprint != requestFingerprint)
        {
            return new ClaimResult.FingerprintMismatch(existing.Fingerprint, requestFingerprint);
        }

        var claimedEntry = ToIdempotencyEntry(key, existing);
        return new ClaimResult.AlreadyClaimed(claimedEntry);
    }

    public async Task SetResponseAsync(
        string key,
        IdempotentResponse response,
        TimeSpan responseTtl,
        CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var redisKey = $"{_options.KeyPrefix}{key}";

        var entry = new RedisEntry
        {
            State = "completed",
            Fingerprint = "", // Overwritten by complete.lua with the original claim fingerprint
            ClaimedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            StatusCode = response.StatusCode,
            Headers = JsonSerializer.Serialize(response.Headers),
            Body = Convert.ToBase64String(response.Body.Span)
        };

        var json = JsonSerializer.Serialize(entry);

        await db.ScriptEvaluateAsync(CompleteScript, new
        {
            key = (RedisKey)redisKey,
            response = json,
            response_ttl_ms = (long)responseTtl.TotalMilliseconds
        });
    }

    public async Task ReleaseClaimAsync(string key, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var redisKey = $"{_options.KeyPrefix}{key}";
        await db.KeyDeleteAsync(redisKey);
    }

    public async Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var redisKey = $"{_options.KeyPrefix}{key}";

        var value = await db.StringGetAsync(redisKey);

        if (value.IsNullOrEmpty)
        {
            return null;
        }

        var entry = JsonSerializer.Deserialize<RedisEntry>((string)value!);

        if (entry is null || entry.State != "completed")
        {
            return null;
        }

        return new IdempotentResponse
        {
            StatusCode = entry.StatusCode,
            Headers = entry.Headers is not null
                ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(entry.Headers)!
                : new Dictionary<string, string[]>(),
            Body = entry.Body is not null ? Convert.FromBase64String(entry.Body) : default
        };
    }

    private static IdempotencyEntry ToIdempotencyEntry(string key, RedisEntry redis)
    {
        return new IdempotencyEntry
        {
            Key = key,
            RequestFingerprint = redis.Fingerprint,
            State = redis.State == "completed" ? IdempotencyState.Completed : IdempotencyState.Claimed,
            ClaimedAtUtc = long.TryParse(redis.ClaimedAt, out var ts)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime
                : DateTime.UtcNow,
            StatusCode = redis.StatusCode,
            ResponseHeaders = redis.Headers,
            ResponseBody = redis.Body is not null ? (ReadOnlyMemory<byte>)Convert.FromBase64String(redis.Body) : null
        };
    }

    private static string LoadScript(string scriptName)
    {
        var assembly = typeof(RedisIdempotencyStore).Assembly;
        var resourceName = $"IdempotencyGuard.Redis.Scripts.{scriptName}";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded Lua script not found: {resourceName}");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private class RedisEntry
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = "";

        [JsonPropertyName("fingerprint")]
        public string Fingerprint { get; set; } = "";

        [JsonPropertyName("claimed_at")]
        public string ClaimedAt { get; set; } = "";

        [JsonPropertyName("status_code")]
        public int StatusCode { get; set; }

        [JsonPropertyName("headers")]
        public string? Headers { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }
    }
}
