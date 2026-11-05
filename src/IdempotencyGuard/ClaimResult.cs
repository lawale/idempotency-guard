namespace IdempotencyGuard;

public abstract record ClaimResult
{
    private ClaimResult() { }

    /// <summary>
    /// The caller now holds the claim. <paramref name="ClaimToken"/> identifies this
    /// particular hold, and must be presented to complete or release it.
    /// </summary>
    public sealed record Claimed(string ClaimToken) : ClaimResult;

    public sealed record AlreadyClaimed(IdempotencyEntry Entry) : ClaimResult;

    public sealed record Completed(IdempotencyEntry Entry) : ClaimResult;

    public sealed record FingerprintMismatch(string ExpectedFingerprint, string ActualFingerprint) : ClaimResult;
}
