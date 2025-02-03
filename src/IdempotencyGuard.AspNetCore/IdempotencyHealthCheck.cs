using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IdempotencyGuard.AspNetCore;

public class IdempotencyStoreHealthCheck : IHealthCheck
{
    private readonly IIdempotencyStore _store;

    public IdempotencyStoreHealthCheck(IIdempotencyStore store)
    {
        _store = store;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var testKey = $"__healthcheck_{Guid.NewGuid():N}";
            var result = await _store.TryClaimAsync(testKey, "healthcheck", TimeSpan.FromSeconds(5), cancellationToken);

            if (result is ClaimResult.Claimed)
            {
                await _store.ReleaseClaimAsync(testKey, cancellationToken);
                return HealthCheckResult.Healthy("Idempotency store is accessible");
            }

            return HealthCheckResult.Degraded("Unexpected claim result during health check");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Idempotency store is unavailable", ex);
        }
    }
}
