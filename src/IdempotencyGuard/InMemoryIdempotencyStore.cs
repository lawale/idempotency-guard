using System.Collections.Concurrent;
using System.Text.Json;

namespace IdempotencyGuard;

public class InMemoryIdempotencyStore : IIdempotencyStore, IDisposable
{
    private readonly ConcurrentDictionary<string, IdempotencyEntry> _entries = new();
    private readonly Timer _cleanupTimer;

    public InMemoryIdempotencyStore()
    {
        _cleanupTimer = new Timer(
            _ => CleanupExpiredEntries(),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    public Task<ClaimResult> TryClaimAsync(
        string key,
        string requestFingerprint,
        TimeSpan claimTtl,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var newEntry = new IdempotencyEntry
        {
            Key = key,
            RequestFingerprint = requestFingerprint,
            State = IdempotencyState.Claimed,
            ClaimedAtUtc = now,
            ExpiresAtUtc = now.Add(claimTtl)
        };

        var existing = _entries.GetOrAdd(key, newEntry);

        if (ReferenceEquals(existing, newEntry))
        {
            return Task.FromResult<ClaimResult>(new ClaimResult.Claimed());
        }

        if (existing.ExpiresAtUtc < now && existing.State == IdempotencyState.Claimed)
        {
            if (_entries.TryUpdate(key, newEntry, existing))
            {
                return Task.FromResult<ClaimResult>(new ClaimResult.Claimed());
            }
        }

        if (existing.RequestFingerprint != requestFingerprint)
        {
            return Task.FromResult<ClaimResult>(
                new ClaimResult.FingerprintMismatch(existing.RequestFingerprint, requestFingerprint));
        }

        if (existing.State == IdempotencyState.Completed)
        {
            return Task.FromResult<ClaimResult>(new ClaimResult.Completed(existing));
        }

        return Task.FromResult<ClaimResult>(new ClaimResult.AlreadyClaimed(existing));
    }

    public Task SetResponseAsync(
        string key,
        IdempotentResponse response,
        TimeSpan responseTtl,
        CancellationToken ct = default)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.State = IdempotencyState.Completed;
            entry.CompletedAtUtc = DateTime.UtcNow;
            entry.StatusCode = response.StatusCode;
            entry.ResponseHeaders = JsonSerializer.Serialize(response.Headers);
            entry.ResponseBody = response.Body;
            entry.ExpiresAtUtc = DateTime.UtcNow.Add(responseTtl);
        }

        return Task.CompletedTask;
    }

    public Task ReleaseClaimAsync(string key, CancellationToken ct = default)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default)
    {
        if (!_entries.TryGetValue(key, out var entry) || entry.State != IdempotencyState.Completed)
        {
            return Task.FromResult<IdempotentResponse?>(null);
        }

        if (entry.ExpiresAtUtc < DateTime.UtcNow)
        {
            _entries.TryRemove(key, out _);
            return Task.FromResult<IdempotentResponse?>(null);
        }

        var response = new IdempotentResponse
        {
            StatusCode = entry.StatusCode!.Value,
            Headers = entry.ResponseHeaders is not null
                ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(entry.ResponseHeaders)!
                : new Dictionary<string, string[]>(),
            Body = entry.ResponseBody ?? []
        };

        return Task.FromResult<IdempotentResponse?>(response);
    }

    public bool HasKey(string key) => _entries.ContainsKey(key);

    public IdempotencyState? GetState(string key) =>
        _entries.TryGetValue(key, out var entry) ? entry.State : null;

    private void CleanupExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var expiredKeys = _entries
            .Where(kvp => kvp.Value.ExpiresAtUtc < now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _entries.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}
