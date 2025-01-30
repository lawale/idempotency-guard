namespace IdempotencyGuard;

public abstract record ClaimResult
{
    private ClaimResult() { }

    public sealed record Claimed : ClaimResult;

    public sealed record AlreadyClaimed(IdempotencyEntry Entry) : ClaimResult;

    public sealed record Completed(IdempotencyEntry Entry) : ClaimResult;

    public sealed record FingerprintMismatch(string ExpectedFingerprint, string ActualFingerprint) : ClaimResult;
}
