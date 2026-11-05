using System.Diagnostics.Metrics;

namespace IdempotencyGuard.Diagnostics;

public static class IdempotencyMetrics
{
    public const string MeterName = "IdempotencyGuard";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    public static readonly Counter<long> RequestsTotal = Meter.CreateCounter<long>(
        "idempotency.requests.total",
        description: "Total requests with idempotency keys");

    public static readonly Counter<long> ReplaysTotal = Meter.CreateCounter<long>(
        "idempotency.replays.total",
        description: "Stored responses replayed to a client");

    public static readonly Counter<long> ClaimsTotal = Meter.CreateCounter<long>(
        "idempotency.claims.total",
        description: "New claims created");

    public static readonly Counter<long> ClaimsReleased = Meter.CreateCounter<long>(
        "idempotency.claims.released",
        description: "Claims released without storing a response, tagged with a reason: failure, response_too_large or should_store_response");

    public static readonly Counter<long> ClaimsLost = Meter.CreateCounter<long>(
        "idempotency.claims.lost",
        description: "Responses not stored because the claim expired and was taken over before the handler finished");

    public static readonly Counter<long> ConflictsTotal = Meter.CreateCounter<long>(
        "idempotency.conflicts.total",
        description: "409 responses for requests that found the key in progress, including waiters that timed out");

    public static readonly Counter<long> FingerprintMismatchesTotal = Meter.CreateCounter<long>(
        "idempotency.fingerprint_mismatches.total",
        description: "422 responses for payload mismatches");

    public static readonly Histogram<double> StoreLatency = Meter.CreateHistogram<double>(
        "idempotency.store.latency",
        unit: "ms",
        description: "Store operation latency");

    public static readonly Counter<long> PurgedTotal = Meter.CreateCounter<long>(
        "idempotency.purge.total",
        description: "Total expired entries purged by cleanup");

    public static readonly Histogram<double> PurgeLatency = Meter.CreateHistogram<double>(
        "idempotency.purge.latency",
        unit: "ms",
        description: "Cleanup sweep latency");
}
