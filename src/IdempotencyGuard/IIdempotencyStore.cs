namespace IdempotencyGuard;

public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims <paramref name="key"/>, or reports who already holds it.
    /// An expired entry, claimed or completed, counts as absent and can be claimed.
    /// A successful claim must generate a token unique to this hold and persist it
    /// with the entry.
    /// </summary>
    Task<ClaimResult> TryClaimAsync(
        string key,
        string requestFingerprint,
        TimeSpan claimTtl,
        CancellationToken ct = default);

    /// <summary>
    /// Stores the response and marks the entry completed, but only if the entry is
    /// still claimed under <paramref name="claimToken"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the response was stored. <c>false</c> if the claim was lost, for
    /// example because it expired and another request took over; the store must then
    /// leave the entry untouched.
    /// </returns>
    Task<bool> SetResponseAsync(
        string key,
        string claimToken,
        IdempotentResponse response,
        TimeSpan responseTtl,
        CancellationToken ct = default);

    /// <summary>
    /// Removes the claim, but only if the entry is still claimed under
    /// <paramref name="claimToken"/>.
    /// </summary>
    /// <returns><c>true</c> if the claim was removed; <c>false</c> if it was no longer held.</returns>
    Task<bool> ReleaseClaimAsync(
        string key,
        string claimToken,
        CancellationToken ct = default);

    Task<IdempotentResponse?> GetResponseAsync(
        string key,
        CancellationToken ct = default);
}
