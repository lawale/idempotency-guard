namespace IdempotencyGuard;

/// <summary>
/// Optional interface for stores that support proactive removal of expired entries.
/// Stores that handle expiry natively (e.g. Redis key TTL) do not need to implement this.
/// </summary>
public interface IPurgableIdempotencyStore
{
    /// <summary>
    /// Deletes up to <paramref name="batchSize"/> expired entries from the store.
    /// </summary>
    /// <param name="batchSize">Maximum number of entries to delete in this invocation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The number of entries actually deleted. A return value less than
    /// <paramref name="batchSize"/> indicates no more expired entries remain.
    /// </returns>
    Task<int> PurgeExpiredAsync(int batchSize, CancellationToken ct = default);
}
