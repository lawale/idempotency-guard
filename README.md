# IdempotencyGuard.NET

A .NET middleware library that provides robust idempotency guarantees for payment APIs and financial transaction endpoints. Implements the claim-then-process pattern to prevent duplicate transaction processing across distributed systems, even under concurrent requests, network retries, and timeout scenarios.

> Companion project to [Payments Engineering](https://olawalelawal.dev) blog series.

## Why

Payment systems face a fundamental problem: when a client sends a request and doesn't receive a response (timeout, network failure), it must retry — but the original request may have succeeded. Without idempotency guarantees, retries cause duplicate charges.

Most existing idempotency implementations are naive:
- They don't handle the case where the first request is **still processing** when the duplicate arrives
- They don't propagate idempotency keys downstream to payment providers
- They don't handle the "claim expires but processing succeeded" race condition

This library solves all three.

## Installation

```bash
# Core + ASP.NET Core middleware
dotnet add package IdempotencyGuard.AspNetCore

# Choose a store
dotnet add package IdempotencyGuard.Redis        # Recommended for production
dotnet add package IdempotencyGuard.PostgreSql   # When Redis isn't available
dotnet add package IdempotencyGuard.SqlServer    # SQL Server environments
```

## Quick Start

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register idempotency guard
builder.Services.AddIdempotencyGuard(options =>
{
    options.HeaderName = "Idempotency-Key";
    options.ClaimTtl = TimeSpan.FromMinutes(5);
    options.ResponseTtl = TimeSpan.FromHours(24);
    options.MissingKeyPolicy = MissingKeyPolicy.Reject;
});

// Register a store (pick one)
builder.Services.AddIdempotencyGuardInMemoryStore();  // Dev/testing only

var app = builder.Build();
app.UseIdempotencyGuard();
```

### Making Requests

```bash
# First request — processed normally
curl -X POST /payments \
  -H "Idempotency-Key: abc-123" \
  -H "Content-Type: application/json" \
  -d '{"amount": 100, "currency": "USD"}'
# → 201 Created

# Duplicate request — returns cached response
curl -X POST /payments \
  -H "Idempotency-Key: abc-123" \
  -H "Content-Type: application/json" \
  -d '{"amount": 100, "currency": "USD"}'
# → 201 Created (with X-Idempotent-Replayed: true header)

# Same key, different payload — rejected
curl -X POST /payments \
  -H "Idempotency-Key: abc-123" \
  -H "Content-Type: application/json" \
  -d '{"amount": 200, "currency": "USD"}'
# → 422 Unprocessable Entity
```

## How It Works

```
Request arrives with Idempotency-Key header
    │
    ├── New key → Claim key → Execute request → Cache response
    │
    ├── Key exists (completed) → Return cached response
    │
    ├── Key exists (in-progress) → Wait or return 409
    │
    ├── Key exists (different payload) → Return 422
    │
    └── Processing fails → Release claim for retry
```

The middleware computes a SHA256 fingerprint of the request (method + path + body) to detect when clients reuse an idempotency key with different payloads.

## Configuration

```csharp
builder.Services.AddIdempotencyGuard(options =>
{
    // Name of the response header added to replayed responses (default: "X-Idempotent-Replayed")
    options.ReplayedHeaderName = "X-Idempotent-Replayed";

    // Header name (default: "Idempotency-Key")
    options.HeaderName = "Idempotency-Key";

    // How long a claim is held before expiring (protects against crashed processes)
    options.ClaimTtl = TimeSpan.FromMinutes(5);

    // How long completed responses are cached for replay
    options.ResponseTtl = TimeSpan.FromHours(24);

    // Behaviour when key is in-progress from another request
    options.ConcurrentRequestPolicy = ConcurrentRequestPolicy.WaitThenReplay;
    options.ConcurrentRequestTimeout = TimeSpan.FromSeconds(30);

    // Behaviour when no idempotency key is provided
    options.MissingKeyPolicy = MissingKeyPolicy.Allow; // or Reject

    // Which HTTP methods require idempotency (default: POST, PUT, PATCH)
    options.EnforcedMethods = ["POST", "PUT", "PATCH"];

    // Maximum response body size to cache (default: 1 MB)
    options.MaxResponseBodySize = 1_048_576;

    // Maximum request body bytes used for fingerprint hashing (default: 1 MB)
    // Bodies larger than this are fingerprinted using only the first N bytes.
    // Set to 0 to skip body hashing entirely.
    options.MaxFingerprintBodySize = 1_048_576;

    // Prefix prepended to every idempotency key before it reaches the store.
    // Useful for namespacing by environment or tenant.
    options.KeyPrefix = "production:";

    // Disable for testing
    options.Enabled = true;

    // Programmatic route-level opt-in/opt-out
    options.EndpointFilter = (method, path) => path.StartsWith("/api/");
});
```

### Per-Endpoint Configuration

Use the `RequireIdempotency()` extension method to override TTLs or require keys on specific endpoints:

```csharp
app.MapPost("/payments", (PaymentRequest request) =>
{
    // ...
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

### Selective Fingerprinting

By default, the middleware fingerprints the entire request body. If your requests contain fields that may legitimately differ across retries (timestamps, correlation IDs, descriptions), you can specify which properties define request identity:

```csharp
app.MapPost("/payments", (PaymentRequest request) =>
{
    // Only Amount and Currency are used for fingerprinting.
    // Different Description values with the same key will replay, not 422.
    return Results.Created($"/payments/{id}", result);
}).RequireIdempotency(options =>
{
    options.FingerprintProperties = [nameof(PaymentRequest.Amount), nameof(PaymentRequest.Currency)];
});
```

Property matching is case-insensitive — `nameof(PaymentRequest.Amount)` (`"Amount"`) matches the JSON key `"amount"` regardless of serializer casing. When `FingerprintProperties` is not set, the entire body is used (default behaviour).

You can also include query parameters and route values in the fingerprint:

```csharp
app.MapPost("/merchants/{merchantId}/payments", (string merchantId, PaymentRequest request) =>
{
    // Fingerprint includes: Amount (body) + version (query) + merchantId (route)
    return Results.Created($"/payments/{id}", result);
}).RequireIdempotency(options =>
{
    options.FingerprintProperties = [nameof(PaymentRequest.Amount)];
    options.FingerprintQueryParameters = ["version"];
    options.FingerprintRouteValues = ["merchantId"];
});
```

| Property | Source | Matching |
|----------|--------|----------|
| `FingerprintProperties` | JSON body (top-level) | Case-insensitive, supports `nameof()` |
| `FingerprintQueryParameters` | Query string (`?key=value`) | Case-insensitive |
| `FingerprintRouteValues` | Route parameters (`{id}`) | Case-insensitive |

## Error Response Customisation

By default, the middleware returns errors in a simple `{"error":"..."}` format. Use `ErrorResponseFactory` to customise error responses — for example, to match [RFC 7807 Problem Details](https://datatracker.ietf.org/doc/html/rfc7807) or your API's existing error contract:

```csharp
builder.Services.AddIdempotencyGuard(options =>
{
    options.ErrorResponseFactory = problem => new
    {
        type = $"https://docs.myapi.com/errors/idempotency/{problem.Kind}",
        title = problem.Kind switch
        {
            IdempotencyErrorKind.MissingKey => "Missing Idempotency Key",
            IdempotencyErrorKind.FingerprintMismatch => "Payload Mismatch",
            IdempotencyErrorKind.Conflict => "Request In Progress",
            IdempotencyErrorKind.Timeout => "Request Timed Out",
            _ => "Idempotency Error"
        },
        status = problem.StatusCode,
        detail = problem.Message,
        idempotencyKey = problem.IdempotencyKey
    };
});
```

The `IdempotencyProblem` passed to the factory contains:

| Property | Type | Description |
|----------|------|-------------|
| `StatusCode` | `int` | HTTP status code (400, 409, or 422) |
| `Kind` | `IdempotencyErrorKind` | `MissingKey`, `FingerprintMismatch`, `Conflict`, or `Timeout` |
| `Message` | `string` | Human-readable description of the error |
| `IdempotencyKey` | `string?` | The key from the request, when available |

## Response Header Filtering

When replaying cached responses, the middleware filters out headers that should not be stored or replayed (hop-by-hop headers per RFC 9110, `Set-Cookie`, `Date`, etc.). You can customise this behaviour:

```csharp
builder.Services.AddIdempotencyGuard(options =>
{
    // Add extra headers to exclude (on top of the built-in deny list)
    options.HeaderDenyList = ["X-Request-Id", "X-Correlation-Id"];

    // OR: use an allow list — ONLY these headers will be stored and replayed.
    // When set, HeaderDenyList and the built-in defaults are ignored entirely.
    options.HeaderAllowList = ["Content-Type", "Location", "X-Custom-Header"];
});
```

Built-in deny list: `Connection`, `Keep-Alive`, `Proxy-Authenticate`, `Proxy-Authorization`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade`, `Set-Cookie`, `WWW-Authenticate`, `Proxy-Connection`, `Alt-Svc`, `Server`, `Date`.

## Key Prefixing

Use `KeyPrefix` to namespace idempotency keys by environment or tenant. The prefix is applied at the middleware level before keys reach the store, so it works identically across all store implementations:

```csharp
// Per-environment
builder.Services.AddIdempotencyGuard(options =>
{
    options.KeyPrefix = builder.Environment.IsProduction() ? "prod:" : "staging:";
});

// Per-tenant (e.g. from a middleware that resolves tenant)
app.Use(async (context, next) =>
{
    var tenantId = context.GetTenantId();
    context.Items[IdempotencyOptions.DefaultKeyPrefixItemKey] = $"tenant-{tenantId}:";
    await next();
});
```

A key `abc-123` with prefix `prod:` is stored as `prod:abc-123`.

## Stores

| Store | Package | Cleanup | Use Case |
|-------|---------|---------|----------|
| In-Memory | `IdempotencyGuard` | Timer + purge | Testing and single-instance dev |
| Redis | `IdempotencyGuard.Redis` | Native key TTL | Production — distributed systems |
| PostgreSQL | `IdempotencyGuard.PostgreSql` | Background service | Production — when Redis isn't available |
| SQL Server | `IdempotencyGuard.SqlServer` | Background service | Production — SQL Server environments |

### Redis Store

```csharp
builder.Services.AddIdempotencyGuardRedisStore("localhost:6379");
```

Uses atomic Lua scripts for claim operations (SET NX) to guarantee consistency under concurrent access. Expired entries are cleaned up automatically by Redis key TTL — no background cleanup needed.

### PostgreSQL Store

```csharp
builder.Services.AddIdempotencyGuardPostgresStore(options =>
{
    options.ConnectionString = "Host=localhost;Database=myapp";
    options.TableName = "idempotency_entries";
    options.AutoCreateTable = true;  // Creates table on first use
});
```

Uses `INSERT ... ON CONFLICT DO NOTHING` for atomic claim operations. Expired entries are cleaned up by the background cleanup service using `DELETE ... FOR UPDATE SKIP LOCKED` for safe concurrent purging.

### SQL Server Store

```csharp
builder.Services.AddIdempotencyGuardSqlServerStore(options =>
{
    options.ConnectionString = "Server=localhost;Database=myapp;...";
    options.TableName = "IdempotencyEntries";
    options.AutoCreateTable = true;
});
```

Uses `MERGE` with `HOLDLOCK` for atomic claim operations. Expired entries are cleaned up by the background cleanup service using batched `DELETE TOP` operations.

## Expired Entry Cleanup

Stores that don't have native TTL support (PostgreSQL, SQL Server, In-Memory) implement `IPurgableIdempotencyStore`. A built-in background service sweeps expired entries on a configurable interval:

```csharp
builder.Services.AddIdempotencyGuard(options =>
{
    options.Cleanup.Enabled = true;                              // default: true
    options.Cleanup.Interval = TimeSpan.FromMinutes(5);          // default: 5 minutes
    options.Cleanup.BatchSize = 1_000;                           // default: 1000
    options.Cleanup.MaxIterationsPerSweep = 100;                 // default: 100
});
```

| Option | Default | Description |
|--------|---------|-------------|
| `Enabled` | `true` | Set to `false` to disable the background service (e.g. if using an external scheduler) |
| `Interval` | 5 minutes | Time between cleanup sweeps |
| `BatchSize` | 1,000 | Max entries deleted per database round-trip |
| `MaxIterationsPerSweep` | 100 | Caps iterations per sweep to prevent runaway deletes |

The cleanup service automatically detects whether the registered store supports purging. For Redis (which uses native key TTL), the service exits immediately without consuming resources.

### Custom Cleanup

If you prefer to run cleanup on your own schedule (e.g. via a cron job or external scheduler), disable the built-in service and call `PurgeExpiredAsync` directly:

```csharp
builder.Services.AddIdempotencyGuard(options =>
{
    options.Cleanup.Enabled = false;
});

// In your scheduler / job:
var store = serviceProvider.GetRequiredService<IIdempotencyStore>();
if (store is IPurgableIdempotencyStore purgable)
{
    var deleted = await purgable.PurgeExpiredAsync(batchSize: 5000);
}
```

## Downstream Key Generation

When your API calls downstream payment providers, you need idempotency at every layer. Use `IIdempotencyContext` to generate deterministic keys for downstream calls:

```csharp
app.MapPost("/payments", async (
    PaymentRequest request,
    IIdempotencyContext idempotency,
    IPaymentProvider provider) =>
{
    // Generate a deterministic key for the downstream Stripe call
    var stripeKey = idempotency.DownstreamKey("stripe", "charge");

    // This key is always the same for the same original request
    var result = await provider.Charge(request.Amount, stripeKey);

    return Results.Created($"/payments/{result.Id}", result);
});
```

## Edge Cases Handled

### Concurrent Requests
Two identical requests arrive simultaneously. The first claims the key and processes. The second waits (with exponential backoff) until the first completes, then replays the cached response.

### Crashed Process
A process claims a key, then crashes. The claim TTL expires, allowing retries to proceed with a new claim.

### Payload Mismatch
A client accidentally reuses an idempotency key with a different request body. The middleware detects the SHA256 fingerprint mismatch and returns 422.

### Upstream Timeout, Downstream Success
The client times out waiting for your API, but the payment provider call succeeds. The middleware stores the response despite the closed connection. When the client retries, it receives the correct cached response.

## Observability

Built-in [OpenTelemetry](https://opentelemetry.io/docs/languages/dotnet/) metrics under the `IdempotencyGuard` meter:

| Metric | Type | Description |
|--------|------|-------------|
| `idempotency.requests.total` | Counter | Total requests with idempotency keys |
| `idempotency.replays.total` | Counter | Requests served from cache |
| `idempotency.claims.total` | Counter | New claims created |
| `idempotency.claims.released` | Counter | Claims released due to failure |
| `idempotency.conflicts.total` | Counter | 409 responses (concurrent in-progress) |
| `idempotency.fingerprint_mismatches.total` | Counter | 422 responses (payload mismatch) |
| `idempotency.store.latency` | Histogram | Store operation latency (ms) |
| `idempotency.purge.total` | Counter | Expired entries purged by cleanup |
| `idempotency.purge.latency` | Histogram | Cleanup sweep latency (ms) |

Subscribe to the meter in your OpenTelemetry setup:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("IdempotencyGuard");
    });
```

## Health Check

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<IdempotencyStoreHealthCheck>("idempotency-store");
```

## License

MIT
