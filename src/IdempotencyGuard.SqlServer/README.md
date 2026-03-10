# IdempotencyGuard.SqlServer

SQL Server-backed idempotency store for the [IdempotencyGuard](https://github.com/lawale/idempotency-guard) library. Uses `MERGE` with `HOLDLOCK` for atomic claim operations and supports automatic schema provisioning.

## Installation

```bash
dotnet add package IdempotencyGuard.AspNetCore
dotnet add package IdempotencyGuard.SqlServer
```

## Setup

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdempotencyGuard(options =>
{
    options.MissingKeyPolicy = MissingKeyPolicy.Reject;
});

builder.Services.AddIdempotencyGuardSqlServerStore(options =>
{
    options.ConnectionString = "Server=localhost;Database=myapp;Trusted_Connection=true";
    options.TableName = "IdempotencyEntries";  // default
    options.AutoCreateTable = true;            // creates table on first use
});

var app = builder.Build();
app.UseIdempotencyGuard();
```

## How it works

- **Claim** — `MERGE` with `HOLDLOCK` for atomic upsert with fingerprint validation
- **Complete** — `UPDATE` to store the response body, headers, and status code
- **Release** — `DELETE` to remove the claim after processing failure
- **Cleanup** — Batched `DELETE TOP` operations via the background cleanup service

## Schema

When `AutoCreateTable = true`, the store creates the following table on first use:

```sql
CREATE TABLE [IdempotencyEntries] (
    [Key]            NVARCHAR(256)  NOT NULL PRIMARY KEY,
    RequestFingerprint NVARCHAR(128)  NOT NULL,
    State            NVARCHAR(20)   NOT NULL,
    ClaimedAtUtc     DATETIME2      NOT NULL,
    CompletedAtUtc   DATETIME2      NULL,
    ExpiresAtUtc     DATETIME2      NOT NULL,
    StatusCode       INT            NULL,
    HeadersJson      NVARCHAR(MAX)  NULL,
    ResponseBody     VARBINARY(MAX) NULL
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
