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

    // Maximum response body size to cache (default: 1MB)
    options.MaxResponseBodySize = 1_048_576;

    // Disable for testing
    options.Enabled = true;
});
```

## Stores

| Store | Package | Use Case |
|-------|---------|----------|
| In-Memory | `IdempotencyGuard` | Testing and single-instance dev |
| Redis | `IdempotencyGuard.Redis` | Production — distributed systems |
| PostgreSQL | `IdempotencyGuard.PostgreSql` | Production — when Redis isn't available |
| SQL Server | `IdempotencyGuard.SqlServer` | Production — SQL Server environments |

### Redis Store

```csharp
builder.Services.AddIdempotencyGuardRedisStore("localhost:6379");
```

Uses atomic Lua scripts for claim operations (SET NX) to guarantee consistency under concurrent access.

### PostgreSQL Store

```csharp
builder.Services.AddIdempotencyGuardPostgresStore(options =>
{
    options.ConnectionString = "Host=localhost;Database=myapp";
    options.TableName = "idempotency_entries";
    options.AutoCreateTable = true;  // Creates table on first use
});
```

Uses `INSERT ... ON CONFLICT DO NOTHING` for atomic claim operations.

### SQL Server Store

```csharp
builder.Services.AddIdempotencyGuardSqlServerStore(options =>
{
    options.ConnectionString = "Server=localhost;Database=myapp;...";
    options.TableName = "IdempotencyEntries";
    options.AutoCreateTable = true;
});
```

Uses `MERGE` with `HOLDLOCK` for atomic claim operations.

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

Built-in OpenTelemetry metrics:

| Metric | Type | Description |
|--------|------|-------------|
| `idempotency.requests.total` | Counter | Total requests with idempotency keys |
| `idempotency.replays.total` | Counter | Requests served from cache |
| `idempotency.claims.total` | Counter | New claims created |
| `idempotency.claims.released` | Counter | Claims released due to failure |
| `idempotency.conflicts.total` | Counter | 409 responses (concurrent in-progress) |
| `idempotency.fingerprint_mismatches.total` | Counter | 422 responses (payload mismatch) |
| `idempotency.store.latency` | Histogram | Store operation latency |

## Health Check

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<IdempotencyStoreHealthCheck>("idempotency-store");
```

## License

MIT
