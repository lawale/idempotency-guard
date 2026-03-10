# IdempotencyGuard

Core abstractions and in-memory store for the [IdempotencyGuard](https://github.com/lawale/idempotency-guard) library. This package provides the foundational types, interfaces, and an in-memory store implementation for idempotent HTTP API request handling.

## What's in this package

- **`IIdempotencyStore`** — store interface for claim/response lifecycle
- **`IPurgableIdempotencyStore`** — extension interface for stores that support expired entry cleanup
- **`IDownstreamKeyGenerator`** / **`IIdempotencyContext`** — deterministic key generation for downstream service calls
- **`InMemoryIdempotencyStore`** — in-memory store for development and testing
- **`RequestFingerprint`** — SHA256-based request fingerprinting with JSON normalisation
- **`IdempotencyOptions`** — full configuration model
- **Built-in metrics** via `System.Diagnostics.Metrics` under the `IdempotencyGuard` meter

## When to use this package directly

Most applications should install **[IdempotencyGuard.AspNetCore](https://www.nuget.org/packages/IdempotencyGuard.AspNetCore)** instead, which includes this package as a dependency and adds the ASP.NET Core middleware.

Install this package directly if you are:
- Building a custom store implementation
- Using the fingerprinting or key generation utilities outside of ASP.NET Core
- Writing a non-ASP.NET Core host that needs the core abstractions

## Installation

```bash
dotnet add package IdempotencyGuard
```

## Implementing a custom store

```csharp
public class MyCustomStore : IIdempotencyStore
{
    public Task<ClaimResult> TryClaimAsync(string key, string fingerprint, TimeSpan claimTtl, CancellationToken ct = default)
    {
        // Atomically claim the key or return the existing state
    }

    public Task SetResponseAsync(string key, IdempotentResponse response, TimeSpan responseTtl, CancellationToken ct = default)
    {
        // Store the response for replay
    }

    public Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default)
    {
        // Retrieve a cached response
    }

    public Task ReleaseClaimAsync(string key, CancellationToken ct = default)
    {
        // Release a claim after processing failure
    }
}
```

## Available store packages

| Store | Package |
|-------|---------|
| Redis | [IdempotencyGuard.Redis](https://www.nuget.org/packages/IdempotencyGuard.Redis) |
| PostgreSQL | [IdempotencyGuard.PostgreSql](https://www.nuget.org/packages/IdempotencyGuard.PostgreSql) |
| SQL Server | [IdempotencyGuard.SqlServer](https://www.nuget.org/packages/IdempotencyGuard.SqlServer) |

## Documentation

See the [full documentation](https://github.com/lawale/idempotency-guard) on GitHub for configuration, usage guides, and examples.

## License

MIT
