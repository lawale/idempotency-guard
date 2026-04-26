namespace IdempotencyGuard.Diagnostics;

/// <summary>
/// Exposes diagnostic source names for OpenTelemetry subscription.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(tracing => tracing.AddSource(IdempotencyDiagnostics.ActivitySourceName))
///     .WithMetrics(metrics => metrics.AddMeter(IdempotencyDiagnostics.MeterName));
/// </code>
/// </example>
public static class IdempotencyDiagnostics
{
    /// <summary>
    /// The name of the <see cref="System.Diagnostics.ActivitySource"/> used by IdempotencyGuard.
    /// Pass this to <c>TracerProviderBuilder.AddSource()</c> to capture spans.
    /// </summary>
    public const string ActivitySourceName = "IdempotencyGuard";

    /// <summary>
    /// The name of the <see cref="System.Diagnostics.Metrics.Meter"/> used by IdempotencyGuard.
    /// Pass this to <c>MeterProviderBuilder.AddMeter()</c> to capture metrics.
    /// </summary>
    public const string MeterName = IdempotencyMetrics.MeterName;
}
