using System.Globalization;

namespace IdempotencyGuard.Stress.Tests.Support;

/// <summary>
/// Sizing for the stress runs. The defaults are chosen to be meaningful on a
/// laptop and on CI without turning the build into a soak test; set
/// IDEMPOTENCY_STRESS_SCALE (e.g. 10) to push the same scenarios harder before
/// a release.
/// </summary>
public static class StressProfile
{
    public static int Scale { get; } = ReadScale();

    /// <summary>Concurrent callers hitting a single idempotency key.</summary>
    public static int HerdSize => 128 * Scale;

    /// <summary>Independent keys used by the sustained mixed-load scenario.</summary>
    public static int KeyCount => 250 * Scale;

    /// <summary>Requests sent per key in the sustained mixed-load scenario.</summary>
    public const int DuplicatesPerKey = 4;

    /// <summary>Workers used for sustained throughput runs.</summary>
    public static int Workers => Math.Max(8, Environment.ProcessorCount * 4);

    /// <summary>Rounds of the single-key herd scenario, each with a fresh key.</summary>
    public static int HerdRounds => 10 * Scale;

    private static int ReadScale()
    {
        var raw = Environment.GetEnvironmentVariable("IDEMPOTENCY_STRESS_SCALE");

        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            return parsed;
        }

        return 1;
    }
}
