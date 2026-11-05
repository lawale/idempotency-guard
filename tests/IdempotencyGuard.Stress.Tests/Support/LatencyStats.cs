namespace IdempotencyGuard.Stress.Tests.Support;

/// <summary>
/// Latency percentiles for a single load run, in milliseconds.
/// </summary>
public sealed record LatencyStats(
    double MinMs,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs)
{
    public static LatencyStats FromSamples(IReadOnlyList<double> samplesMs)
    {
        if (samplesMs.Count == 0)
        {
            return new LatencyStats(0, 0, 0, 0, 0, 0);
        }

        var sorted = samplesMs.ToArray();
        Array.Sort(sorted);

        return new LatencyStats(
            sorted[0],
            sorted.Average(),
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1]);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        // Nearest-rank percentile: sufficient for reporting, and stable at small sample sizes.
        var rank = (int)Math.Ceiling(percentile * sorted.Length);
        var index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    public override string ToString() =>
        $"min={MinMs:F3}ms mean={MeanMs:F3}ms p50={P50Ms:F3}ms p95={P95Ms:F3}ms p99={P99Ms:F3}ms max={MaxMs:F3}ms";
}
