# IdempotencyGuard.Redis

Redis-backed idempotency store for the [IdempotencyGuard](https://github.com/lawale/idempotency-guard) library. Uses atomic Lua scripts for claim coordination and native Redis key TTL for automatic expiry.

## Installation

```bash
dotnet add package IdempotencyGuard.AspNetCore
dotnet add package IdempotencyGuard.Redis
```

## Setup

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdempotencyGuard(options =>
{
    options.MissingKeyPolicy = MissingKeyPolicy.Reject;
});

builder.Services.AddIdempotencyGuardRedisStore("localhost:6379");

var app = builder.Build();
app.UseIdempotencyGuard();
```

## How it works

All claim operations use atomic Lua scripts evaluated on the Redis server:

- **Claim** — `SET NX` with TTL to atomically acquire a key
- **Complete** — Stores the response and updates the entry state in a single script
- **Release** — Removes the key to allow retries after failure

Expired entries are cleaned up automatically by Redis key TTL — no background cleanup service needed.

## Connection options

The store accepts either a connection string or an existing `IConnectionMultiplexer`:

```csharp
// Connection string
builder.Services.AddIdempotencyGuardRedisStore("localhost:6379,abortConnect=false");

// Existing multiplexer (shared with other Redis consumers)
builder.Services.AddIdempotencyGuardRedisStore(existingMultiplexer);
```

## Why Redis

- **Atomic operations** — Lua scripts guarantee consistency under concurrent access
- **Native TTL** — No background cleanup needed; Redis handles expiry
- **Distributed** — Works across multiple application instances out of the box
- **Low latency** — Sub-millisecond claim/response operations

## Documentation

See the [full documentation](https://github.com/lawale/idempotency-guard) on GitHub for middleware configuration, selective fingerprinting, and production deployment guides.

## License

MIT
