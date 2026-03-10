# IdempotencyGuard.AspNetCore

ASP.NET Core middleware for automatic idempotent request handling. Part of the [IdempotencyGuard](https://github.com/lawale/idempotency-guard) library.

Processes `Idempotency-Key` headers, fingerprints requests, caches responses, and replays them on duplicate requests — preventing double processing from client retries, network failures, and timeout scenarios.

## Installation

```bash
dotnet add package IdempotencyGuard.AspNetCore

# Choose a store for production
dotnet add package IdempotencyGuard.Redis        # Recommended
dotnet add package IdempotencyGuard.PostgreSql
dotnet add package IdempotencyGuard.SqlServer
```

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdempotencyGuard(options =>
{
    options.HeaderName = "Idempotency-Key";
    options.ClaimTtl = TimeSpan.FromMinutes(5);
    options.ResponseTtl = TimeSpan.FromHours(24);
    options.MissingKeyPolicy = MissingKeyPolicy.Reject;
});

builder.Services.AddIdempotencyGuardInMemoryStore(); // Use Redis/SQL in production

var app = builder.Build();
app.UseIdempotencyGuard();
```

## How It Works

```
Request with Idempotency-Key header
    |
    +-- New key         --> Claim --> Execute --> Cache response
    +-- Completed key   --> Replay cached response
    +-- In-progress key --> Wait or return 409
    +-- Different body  --> Return 422 (fingerprint mismatch)
    +-- Processing fail --> Release claim for retry
```

## Per-Endpoint Configuration

Use `RequireIdempotency()` for minimal APIs:

```csharp
app.MapPost("/payments", (PaymentRequest request) =>
{
    // Per-endpoint TTL overrides
}).RequireIdempotency(options =>
{
    options.ClaimTtlSeconds = 120;
    options.ResponseTtlSeconds = 86400;
});
```

Or use the `[Idempotent]` attribute for controller-based APIs:

```csharp
[Idempotent(ClaimTtlSeconds = 120, ResponseTtlSeconds = 86400)]
public IActionResult CreatePayment(PaymentRequest request) { ... }
```

## Selective Fingerprinting

Specify which properties define request identity, so non-business fields (timestamps, correlation IDs) don't cause fingerprint mismatches:

```csharp
app.MapPost("/payments", (PaymentRequest request) =>
{
    // Only specified fields are fingerprinted
}).RequireIdempotency(options =>
{
    options.FingerprintProperties = [nameof(PaymentRequest.Amount), nameof(PaymentRequest.Currency)];
    options.FingerprintQueryParameters = ["version"];
    options.FingerprintRouteValues = ["merchantId"];
});
```

## Key Features

- **Claim-then-process pattern** with configurable TTLs
- **SHA256 fingerprinting** with JSON normalisation to detect payload mismatches
- **Selective fingerprinting** via `nameof()` for body properties, query parameters, and route values
- **Concurrent request handling** — wait-then-replay or return 409
- **Custom error responses** via `ErrorResponseFactory` (RFC 7807 compatible)
- **Response header filtering** with allow/deny lists
- **Key prefixing** for multi-tenant or multi-environment setups
- **Downstream key generation** for propagating idempotency to external services
- **Built-in metrics** via OpenTelemetry (`IdempotencyGuard` meter)
- **Expired entry cleanup** via background service with configurable batch size

## Documentation

See the [full documentation](https://github.com/lawale/idempotency-guard) on GitHub for complete configuration reference, store setup guides, and production examples.

## License

MIT
