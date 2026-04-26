using System.Diagnostics;

namespace IdempotencyGuard.Diagnostics;

internal static class IdempotencyActivitySource
{
    internal static readonly ActivitySource Source = new(IdempotencyDiagnostics.ActivitySourceName, "1.0.0");

    internal static Activity? StartStoreActivity(string operation, string? idempotencyKey = null)
    {
        var activity = Source.StartActivity($"idempotency.store {operation}", ActivityKind.Client);

        if (activity is not null)
        {
            activity.SetTag("idempotency.store.operation", operation);
            if (idempotencyKey is not null)
                activity.SetTag("idempotency.key", idempotencyKey);
        }

        return activity;
    }
}
