using System.Collections.Concurrent;
using System.Text.Json;

namespace IdempotencyGuard;

public class InMemoryIdempotencyStore : IIdempotencyStore, IPurgableIdempotencyStore, IDisposable
{
    private readonly ConcurrentDictionary<string, IdempotencyEntry> _entries = new();
    public InMemoryIdempotencyStore() { }

    public Task<ClaimResult> TryClaimAsync(
        string key,
        string requestFingerprint,
        TimeSpan claimTtl,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        IdempotencyEntry? newEntry = null;

        while (true)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.ExpiresAtUtc >= now)
                {
                    return Task.FromResult(Classify(existing, requestFingerprint));
                }

                newEntry ??= NewClaim(key, requestFingerprint, now, claimTtl);
                if (_entries.TryUpdate(key, newEntry, existing))
                {
                    return Task.FromResult<ClaimResult>(new ClaimResult.Claimed(newEntry.ClaimToken!));
                }
            }
            else
            {
                newEntry ??= NewClaim(key, requestFingerprint, now, claimTtl);
                if (_entries.TryAdd(key, newEntry))
                {
                    return Task.FromResult<ClaimResult>(new ClaimResult.Claimed(newEntry.ClaimToken!));
                }
            }
        }
    }

    public Task<bool> SetResponseAsync(
        string key,
        string claimToken,
        IdempotentResponse response,
        TimeSpan responseTtl,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        while (_entries.TryGetValue(key, out var existing) && IsHeldBy(existing, claimToken))
        {
            // Swapping the whole entry prevents torn reads from a concurrent GetResponseAsync.
            var completed = new IdempotencyEntry
            {
                Key = existing.Key,
                RequestFingerprint = existing.RequestFingerprint,
                State = IdempotencyState.Completed,
                ClaimedAtUtc = existing.ClaimedAtUtc,
                CompletedAtUtc = now,
                StatusCode = response.StatusCode,
                ResponseHeadersMap = CloneHeaders(response.Headers),
                ResponseBody = response.Body,
                ExpiresAtUtc = now.Add(responseTtl)
            };

            if (_entries.TryUpdate(key, completed, existing))
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<bool> ReleaseClaimAsync(string key, string claimToken, CancellationToken ct = default)
    {
        var released = _entries.TryGetValue(key, out var existing)
            && IsHeldBy(existing, claimToken)
            && _entries.TryRemove(new KeyValuePair<string, IdempotencyEntry>(key, existing));

        return Task.FromResult(released);
    }

    public Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(key, out var entry) || entry.State != IdempotencyState.Completed)
        {
            return Task.FromResult<IdempotentResponse?>(null);
        }

        if (entry.ExpiresAtUtc < DateTime.UtcNow)
        {
            // Compare-and-remove, so a request that just re-claimed the key isn't evicted.
            _entries.TryRemove(new KeyValuePair<string, IdempotencyEntry>(key, entry));
            return Task.FromResult<IdempotentResponse?>(null);
        }

        var response = new IdempotentResponse
        {
            StatusCode = entry.StatusCode!.Value,
            Headers = entry.ResponseHeadersMap is not null
                ? CloneHeaders(entry.ResponseHeadersMap)
                : entry.ResponseHeaders is not null
                    ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(entry.ResponseHeaders)!
                    : new Dictionary<string, string[]>(),
            Body = entry.ResponseBody.GetValueOrDefault()
        };

        return Task.FromResult<IdempotentResponse?>(response);
    }

    public bool HasKey(string key) => _entries.ContainsKey(key);

    public IdempotencyState? GetState(string key) =>
        _entries.TryGetValue(key, out var entry) ? entry.State : null;

    public Task<int> PurgeExpiredAsync(int batchSize, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var expired = new List<KeyValuePair<string, IdempotencyEntry>>(Math.Max(batchSize, 4));

        foreach (var kvp in _entries)
        {
            if (kvp.Value.ExpiresAtUtc >= now)
            {
                continue;
            }

            expired.Add(kvp);
            if (expired.Count >= batchSize)
            {
                break;
            }
        }

        var count = 0;
        foreach (var entry in expired)
        {
            // Compare-and-remove, so a request that just re-claimed the key isn't evicted.
            if (_entries.TryRemove(entry))
                count++;
        }

        return Task.FromResult(count);
    }

    private static ClaimResult Classify(IdempotencyEntry existing, string requestFingerprint)
    {
        if (existing.RequestFingerprint != requestFingerprint)
        {
            return new ClaimResult.FingerprintMismatch(existing.RequestFingerprint, requestFingerprint);
        }

        return existing.State == IdempotencyState.Completed
            ? new ClaimResult.Completed(existing)
            : new ClaimResult.AlreadyClaimed(existing);
    }

    private static IdempotencyEntry NewClaim(string key, string requestFingerprint, DateTime now, TimeSpan claimTtl) =>
        new()
        {
            Key = key,
            RequestFingerprint = requestFingerprint,
            State = IdempotencyState.Claimed,
            ClaimedAtUtc = now,
            ExpiresAtUtc = now.Add(claimTtl),
            ClaimToken = Guid.NewGuid().ToString("N")
        };

    private static bool IsHeldBy(IdempotencyEntry entry, string claimToken) =>
        entry.State == IdempotencyState.Claimed && entry.ClaimToken == claimToken;

    private static Dictionary<string, string[]> CloneHeaders(Dictionary<string, string[]> headers)
    {
        var clone = new Dictionary<string, string[]>(headers.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, values) in headers)
        {
            clone[key] = values.ToArray();
        }

        return clone;
    }

    public void Dispose() { }
}
