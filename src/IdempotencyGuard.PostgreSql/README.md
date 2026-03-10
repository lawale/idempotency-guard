# IdempotencyGuard.PostgreSql

PostgreSQL-backed idempotency store for the [IdempotencyGuard](https://github.com/lawale/idempotency-guard) library. Uses `INSERT ... ON CONFLICT DO NOTHING` for atomic claim operations and supports automatic schema provisioning.

## Installation

```bash
dotnet add package IdempotencyGuard.AspNetCore
dotnet add package IdempotencyGuard.PostgreSql
```

## Setup

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdempotencyGuard(options =>
{
    options.MissingKeyPolicy = MissingKeyPolicy.Reject;
});

builder.Services.AddIdempotencyGuardPostgresStore(options =>
{
    options.ConnectionString = "Host=localhost;Database=myapp";
    options.TableName = "idempotency_entries";  // default
    options.AutoCreateTable = true;             // creates table on first use
});

var app = builder.Build();
app.UseIdempotencyGuard();
```

## How it works

- **Claim** — `INSERT ... ON CONFLICT DO NOTHING` for atomic key acquisition with fingerprint validation
- **Complete** — `UPDATE` to store the response body, headers, and status code
- **Release** — `DELETE` to remove the claim after processing failure
- **Cleanup** — `DELETE ... FOR UPDATE SKIP LOCKED` via the background cleanup service for safe concurrent purging

## Schema

When `AutoCreateTable = true`, the store creates the following table on first use:

```sql
CREATE TABLE idempotency_entries (
    key                TEXT        NOT NULL PRIMARY KEY,
    request_fingerprint TEXT       NOT NULL,
    state              TEXT        NOT NULL,
    claimed_at         TIMESTAMPTZ NOT NULL,
    completed_at       TIMESTAMPTZ,
    expires_at         TIMESTAMPTZ NOT NULL,
    status_code        INTEGER,
    headers_json       TEXT,
    response_body      BYTEA
);
```

## Expired entry cleanup

Expired entries are purged by the built-in background service. Configure the cleanup interval and batch size:

```csharp
builder.Services.AddIdempotencyGuard(options =>
{
    options.Cleanup.Interval = TimeSpan.FromMinutes(5);
    options.Cleanup.BatchSize = 1_000;
});
```

## Documentation

See the [full documentation](https://github.com/lawale/idempotency-guard) on GitHub for middleware configuration, selective fingerprinting, and production deployment guides.

## License

MIT
