using System.Diagnostics;
using IdempotencyGuard.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore;

internal sealed class IdempotencyCleanupService : BackgroundService
{
    private readonly IIdempotencyStore _store;
    private readonly IOptionsMonitor<IdempotencyOptions> _optionsMonitor;
    private readonly ILogger<IdempotencyCleanupService> _logger;

    public IdempotencyCleanupService(
        IIdempotencyStore store,
        IOptionsMonitor<IdempotencyOptions> optionsMonitor,
        ILogger<IdempotencyCleanupService> logger)
    {
        _store = store;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_store is not IPurgableIdempotencyStore purgable)
        {
            _logger.LogDebug(
                "Idempotency store {StoreType} does not support purging; cleanup service will not run",
                _store.GetType().Name);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _optionsMonitor.CurrentValue.Cleanup;

            if (!options.Enabled)
            {
                await Task.Delay(options.Interval, stoppingToken);
                continue;
            }

            try
            {
                var sweepTs = Stopwatch.GetTimestamp();
                var totalDeleted = 0;
                var iterations = 0;

                while (iterations < options.MaxIterationsPerSweep)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    var deleted = await purgable.PurgeExpiredAsync(options.BatchSize, stoppingToken);
                    totalDeleted += deleted;
                    iterations++;

                    if (deleted < options.BatchSize)
                        break;
                }

                if (totalDeleted > 0)
                {
                    IdempotencyMetrics.PurgedTotal.Add(totalDeleted);
                    _logger.LogInformation(
                        "Idempotency cleanup: purged {DeletedCount} expired entries in {Iterations} iteration(s)",
                        totalDeleted, iterations);
                }

                IdempotencyMetrics.PurgeLatency.Record(
                    Stopwatch.GetElapsedTime(sweepTs).TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Idempotency cleanup sweep failed");
            }

            await Task.Delay(_optionsMonitor.CurrentValue.Cleanup.Interval, stoppingToken);
        }
    }
}
