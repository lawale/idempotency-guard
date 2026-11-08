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
    options.UnmarkedEndpoints = UnmarkedEndpointBehavior.RequireKey;
});

builder.Services.AddIdempotencyGuardInMemoryStore(); // Use Redis/SQL in production

var app = builder.Build();

// The guard must come after authorization, or it replays stored responses to unauthorized callers.
app.UseAuthentication();
app.UseAuthorization();
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

## Choosing Which Endpoints Are Guarded

`UnmarkedEndpoints` decides what happens on endpoints without `RequireIdempotency()` or `[Idempotent]`, and on requests that match no route:

- `GuardWhenKeyPresent` (default): a request with a key is guarded, and one without passes through.
- `RequireKey`: a request without a key gets a 400, and one with a key is guarded.
- `Ignore`: the guard skips the request, even with a key. `UseRouting()`, if you call it, must come before `UseIdempotencyGuard()`.

`MissingKeyPolicy` and `Enabled` are deprecated and will be removed in 3.0, and using them gives a build warning, `IDEMP001`.

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

Settings merge across a controller and its actions, and the most specific value wins.

## Scoping and Policies

By default every caller shares one key space, so any caller who presents a key gets the response stored under it. Set a scope on any API with more than one caller. Use named policies when it differs by endpoint, or to control per endpoint whether 5xx responses are stored:

```csharp
builder.Services.AddIdempotencyGuard(options =>
{
    options.AddPolicy("user", p => p.KeyScope = ctx => ctx.User.FindFirstValue("sub"));
    options.AddPolicy("service", p =>
    {
        p.KeyScope = ctx => ctx.User.FindFirstValue("client_id");
        p.ShouldStoreResponse = status => status < 500;
    });
});

[Idempotent(Policy = "user")]
app.MapPost(...).RequireIdempotency(o => o.Policy = "service");
```

See the [full documentation](https://github.com/lawale/idempotency-guard#scoping-keys-to-callers) for the details and the pitfalls.

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
