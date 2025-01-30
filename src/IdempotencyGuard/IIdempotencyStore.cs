namespace IdempotencyGuard;

public interface IIdempotencyStore
{
    Task<ClaimResult> TryClaimAsync(
        string key,
        string requestFingerprint,
        TimeSpan claimTtl,
        CancellationToken ct = default);

    Task SetResponseAsync(
        string key,
        IdempotentResponse response,
        TimeSpan responseTtl,
        CancellationToken ct = default);

    Task ReleaseClaimAsync(
        string key,
        CancellationToken ct = default);

    Task<IdempotentResponse?> GetResponseAsync(
        string key,
        CancellationToken ct = default);
}
