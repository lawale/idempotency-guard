namespace IdempotencyGuard.AspNetCore;

internal sealed class NullIdempotencyContext : IIdempotencyContext
{
    public static readonly NullIdempotencyContext Instance = new();

    public string Key =>
        throw new InvalidOperationException(
            "No idempotency context is available for this request. " +
            "Ensure the request includes an idempotency key header and is routed through IdempotencyMiddleware.");

    public bool IsReplay => false;

    public string RequestFingerprint =>
        throw new InvalidOperationException(
            "No idempotency context is available for this request. " +
            "Ensure the request includes an idempotency key header and is routed through IdempotencyMiddleware.");

    public string DownstreamKey(string provider, string operation) =>
        throw new InvalidOperationException(
            "No idempotency context is available for this request. " +
            "Ensure the request includes an idempotency key header and is routed through IdempotencyMiddleware.");
}
