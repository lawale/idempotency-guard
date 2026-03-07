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
        description: "Requests served from cache");

    public static readonly Counter<long> ClaimsTotal = Meter.CreateCounter<long>(
        "idempotency.claims.total",
        description: "New claims created");

    public static readonly Counter<long> ClaimsReleased = Meter.CreateCounter<long>(
        "idempotency.claims.released",
        description: "Claims released due to failure");

    public static readonly Counter<long> ConflictsTotal = Meter.CreateCounter<long>(
        "idempotency.conflicts.total",
        description: "409 responses for concurrent in-progress requests");

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
