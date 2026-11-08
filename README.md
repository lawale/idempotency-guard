# IdempotencyGuard.NET

[![CI](https://github.com/lawale/idempotency-guard/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/lawale/idempotency-guard/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/IdempotencyGuard.AspNetCore.svg)](https://www.nuget.org/packages/IdempotencyGuard.AspNetCore)

Idempotency-Key middleware for ASP.NET Core. A client retries, or sends the same request twice at once, and your handler still runs one time: the first request claims the key and later ones get its stored response. Claims stay atomic across instances on Redis, PostgreSQL or SQL Server.

It started with a need for this in production, and I built it as something I could share publicly. It works on any endpoint where running a request twice does damage, in controllers and minimal APIs alike. The [stress suite and overhead numbers](docs/stability-and-performance.md) show what's been tested, and what hasn't.

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
    options.UnmarkedEndpoints = UnmarkedEndpointBehavior.RequireKey;
});

// Register a store (pick one)
builder.Services.AddIdempotencyGuardInMemoryStore();  // Dev/testing only

var app = builder.Build();

// The guard must come after authorization, or it replays stored responses to unauthorized callers.
app.UseAuthentication();
app.UseAuthorization();
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

The middleware computes a SHA256 fingerprint of the request (method + path, including any `PathBase` + the hosts the endpoint declares with `RequireHost` or `[Host]` + body) to detect when clients reuse an idempotency key with different payloads.

The `Host` header itself isn't included, because proxies often rewrite it and a retry has to replay wherever it lands. If you tell tenants apart by hostname without declaring hosts on your endpoints, or branch on the host with `MapWhen`, use `KeyPrefix` or `KeyScope` to keep their keys apart.

Declared hosts come from the matched endpoint, so they're only included if routing has run before the guard. A custom `IFingerprintBuilder` replaces all of this: include whatever separates your endpoints in the fingerprint it returns, or a key reused on another endpoint can replay that endpoint's response.

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

    // What happens on endpoints without [Idempotent] or RequireIdempotency()
    options.UnmarkedEndpoints = UnmarkedEndpointBehavior.GuardWhenKeyPresent; // or RequireKey, Ignore

    // Which HTTP methods require idempotency (default: POST, PUT, PATCH)
    options.EnforcedMethods = ["POST", "PUT", "PATCH"];

    // Maximum response body size to cache (default: 1 MB)
    // A larger response isn't stored, and a retry gets a 410. See "Response Too Large to Store".
    options.MaxResponseBodySize = 1_048_576;

    // Maximum request body bytes used for fingerprint hashing (default: 1 MB)
    // Bodies larger than this are fingerprinted using only the first N bytes.
    // Set to 0 to skip body hashing entirely.
    options.MaxFingerprintBodySize = 1_048_576;

    // Longest key accepted from a client (default: 1024). Longer keys get a 400. Set to 0 to disable.
    options.MaxKeyLength = 1024;

    // Who a key belongs to. Set this on any API with more than one caller.
    options.KeyScope = context => context.User.FindFirstValue("sub");

    // Whether to store a response, by status code (default: store everything, including 5xx)
    options.ShouldStoreResponse = status => status < 500;

    // Prefix prepended to every idempotency key before it reaches the store.
    // Useful for namespacing by environment or tenant.
    options.KeyPrefix = "production:";

    // Skip the guard by method and path. Returning false skips it even on marked endpoints.
    // The path leaves out PathBase, so after UsePathBase("/api") this sees "/orders", not "/api/orders".
    options.EndpointFilter = (method, path) => path.StartsWithSegments("/orders");
});
```

### Choosing Which Endpoints Are Guarded

`UnmarkedEndpoints` decides what happens on endpoints without `[Idempotent]` or `RequireIdempotency()`, and on requests that match no route. A marked endpoint is unaffected, and its `Required` setting (default `true`) decides whether it needs a key.

| `UnmarkedEndpoints` | Unmarked endpoint, key sent | Unmarked endpoint, no key |
|---|---|---|
| `GuardWhenKeyPresent` (default) | Guarded | Passes through |
| `RequireKey` | Guarded | 400, and that includes routes that don't exist |
| `Ignore` | Passes through, nothing stored | Passes through |

So what a marker does depends on this setting:

| `UnmarkedEndpoints` | What marking an endpoint adds |
|---|---|
| `GuardWhenKeyPresent` | Its own settings, and makes the key mandatory |
| `RequireKey` | Its own settings, and `Required = false` makes the key optional |
| `Ignore` | Turns the guard on for that endpoint, as well as its own settings |

Under the first two, a client that sends a key on every request, say from a shared HTTP interceptor, has responses from unmarked endpoints stored and replayed too. A key reused across endpoints gets a 422, and even a request to a route that doesn't exist has its 404 stored. `Ignore` stops that:

```csharp
options.UnmarkedEndpoints = UnmarkedEndpointBehavior.Ignore;

app.MapPost("/payments", CreatePayment).RequireIdempotency();  // guarded
app.MapPost("/search", Search);                                // key ignored, nothing stored
```

With `Ignore`, routing has to run before the guard, or no endpoint looks marked. `WebApplication` does this for you. If you call `UseRouting()` yourself, call it first, because calling it after `UseIdempotencyGuard()` fails at startup.

Before turning it on for an existing API, check that every endpoint you rely on is marked. An unmarked one loses its duplicate protection, and nothing reports that.

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

Settings merge across a controller and its actions, and the most specific value wins. An action can change a TTL without losing the controller's policy.

A marked endpoint needs a key by default. `Required = false` makes it optional, which is for moving existing clients over to sending keys:

```csharp
app.MapPost("/payments", CreatePayment).RequireIdempotency(options =>
{
    options.Policy = "user";
    options.Required = false;   // remove once every client sends a key
});
```

Requests with a key are fully guarded. Requests without one run the handler with no duplicate protection, because the guard never generates a key: it wouldn't match on a retry. You only need this when the endpoint has its own settings, or under `RequireKey` or `Ignore`. Under the default `GuardWhenKeyPresent`, an unmarked endpoint already treats keys as optional. Set a policy's `KeyScope` from the start of the move, because adding one later changes the endpoint's store keys, so a retry in flight at that moment won't find its stored response.

### Named Policies

Attributes only accept constants, so anything that needs a delegate lives in a named policy. Anything a policy leaves unset falls back to the global value:

```csharp
builder.Services.AddIdempotencyGuard(options =>
{
    options.ShouldStoreResponse = status => status < 500;   // global default
    options.AddPolicy("cache-all", p => p.ShouldStoreResponse = _ => true);
});

[Idempotent(Policy = "cache-all")]                                  // controller or action
app.MapPost(...).RequireIdempotency(o => o.Policy = "cache-all");   // minimal API
```

An endpoint that names an unregistered policy fails at startup in Development, and otherwise fails each request that uses it before the handler runs. See [Startup Validation](#startup-validation).

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
| `StatusCode` | `int` | HTTP status code (400, 403, 409, 410 or 422) |
| `Kind` | `IdempotencyErrorKind` | `MissingKey`, `FingerprintMismatch`, `Conflict`, `Timeout`, `MissingScope`, `InvalidKey` or `ResponseNotReplayable` |
| `Message` | `string` | Human-readable description of the error |
| `IdempotencyKey` | `string?` | The key from the request, when available |
| `OriginalStatusCode` | `int?` | The original request's status, on `ResponseNotReplayable` |

## Response Header Filtering

When replaying cached responses, the middleware filters out headers that should not be stored or replayed (hop-by-hop headers per RFC 9110, `Set-Cookie`, `Date`, etc.). You can customise this behaviour:

```csharp
builder.Services.AddIdempotencyGuard(options =>
{
    // Add extra headers to exclude (on top of the built-in deny list)
    options.HeaderDenyList = ["X-Request-Id", "X-Correlation-Id"];

    // OR: use an allow list — ONLY these headers will be stored and replayed.
    // When it has entries, HeaderDenyList and the built-in defaults are ignored entirely.
    // An empty allow list counts as not set.
    options.HeaderAllowList = ["Content-Type", "Location", "X-Custom-Header"];
});
```

Built-in deny list: `Connection`, `Keep-Alive`, `Proxy-Authenticate`, `Proxy-Authorization`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade`, `Set-Cookie`, `WWW-Authenticate`, `Proxy-Connection`, `Alt-Svc`, `Server`, `Date`.

## Scoping Keys to Callers

**By default, every caller shares one key space.** Any caller who presents a key gets the response stored under it, so on an API with more than one caller you should set a scope. The app logs a warning at startup until you do, which `SuppressUnscopedKeysWarning` turns off if a single trusted caller owns the key space. If some routes have a scope and others don't, [startup validation](#startup-validation) lists the ones that don't.

The library doesn't guess the identifier, because that depends on your system: `sub` is meaningless for API keys, and a tenant may be the right boundary rather than a user.

```csharp
options.KeyScope = context => context.User.FindFirstValue("sub");
```

A scope of `null` rejects the request with a 403 rather than falling back to the shared key space. The scope is hashed before it reaches the store, so caller identifiers stay out of store keys.

**Scope by whoever generates and retries the key.** When that differs by endpoint, as in an app serving both users and other services, use one policy for each:

```csharp
options.AddPolicy("user",    p => p.KeyScope = ctx => ctx.User.FindFirstValue("sub"));
options.AddPolicy("service", p => p.KeyScope = ctx => ctx.User.FindFirstValue("client_id"));
options.AddPolicy("webhook", p => p.KeyScope = _ => "stripe");
```

The same identifier stays separate under different policies. If one endpoint accepts both kinds of caller, use a single resolver and prefix the values so user `abc` and client `abc` can't collide:

```csharp
p.KeyScope = ctx =>
    ctx.User.FindFirstValue("sub") is { } sub ? $"user:{sub}"
    : ctx.User.FindFirstValue("client_id") is { } id ? $"client:{id}"
    : null;
```

Things to watch for:

- **API keys:** scope by the client or account ID, not the key itself. Otherwise a retry that spans a key rotation runs the handler again.
- **A service acting for a user:** scope by the service, because it's the one retrying.
- **Webhooks:** verify signatures before the guard runs. If the check is in the handler, anyone can replay a stored response without a valid signature.
- **Endpoints with no policy:** these use the global `KeyScope`. In a mixed app, set a global resolver so a forgotten attribute is rejected rather than left unscoped.
- **Enabling scoping on a live system:** this changes every key, including downstream keys. See the [upgrade guide](docs/upgrading-to-2.0.md).

## Middleware Order

The guard must run after authorization:

```csharp
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseIdempotencyGuard();
```

Run earlier than that, it would replay stored responses to callers who should be rejected, and it couldn't see identities from a non-default authentication scheme, which only `UseAuthorization` resolves. On an endpoint that requires authorization, the guard throws if authorization hasn't run yet.

## Server Errors

A handler that returns a 5xx without throwing has its response stored and replayed, like any other response. To let retries run the handler again instead, set `ShouldStoreResponse`, globally or in a [policy](#named-policies):

```csharp
options.ShouldStoreResponse = status => status < 500;
```

When it returns `false`, the claim is released and the client still gets the original response.

**Be careful with this on endpoints that have side effects.** If the handler charged a customer and then returned a 500, the retry charges them again, unless the downstream call used a [downstream key](#downstream-key-generation).

## Startup Validation

`ValidateEndpointsOnStartup` checks your endpoints when the app starts: that every named policy is registered, and that no route is left unscoped while others have a scope. Like `ValidateOnBuild`, it's on in Development and off elsewhere unless you set it:

```csharp
options.ValidateEndpointsOnStartup = true; // Also check outside Development
```

It's off by default in production because of the cost. Reading the endpoints makes ASP.NET Core build them, and routing builds its own copy on the first request regardless, so this is added startup time rather than moved time, and it grows with the number of endpoints, especially minimal APIs with typed parameters.

With it off, a misspelled policy name fails each request that uses it with an `InvalidOperationException` before the handler runs, so it can't bypass the guard. You just find out later. The route warning isn't logged if you set `EndpointFilter`, since a filter takes request paths rather than route patterns.

It also warns about marked endpoints whose methods aren't in `EnforcedMethods`, because the guard skips those. A `[Idempotent]` `DELETE` is a common example. `GET`, `HEAD`, `OPTIONS` and `TRACE` are left out, since `[Idempotent]` on a controller marks its read actions too. And it fails if an endpoint sets `FingerprintProperties` while `MaxFingerprintBodySize` is 0, because the body is never read and a changed payload goes undetected. A custom `IFingerprintBuilder` is exempt from that one. Without validation, the default builder still catches it: each request to that endpoint fails with an `InvalidOperationException` before anything is stored or the handler runs.

The checks on option values don't read endpoints, so they always run, and the app fails to start if any of them fails:

| Setting | Must be |
|---|---|
| `ClaimTtl`, `ResponseTtl` | Greater than zero. At zero, the same key runs the handler again |
| `MaxResponseBodySize` | Greater than zero. At zero, every retry gets a 410 |
| `HeaderName`, `ReplayedHeaderName` | Not empty |
| `EnforcedMethods` | Not null |
| `Cleanup`, `Cleanup.Interval`, `Cleanup.BatchSize`, `Cleanup.MaxIterationsPerSweep` | Not null, and greater than zero |
| `KeyPrefix` | Within the length limit |
| `MissingKeyPolicy` and `UnmarkedEndpoints` | Not set to different behaviours |

The warning about having no scope at all doesn't read endpoints either, so it always runs.

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

The prefix is limited to 128 characters. End it with a delimiter, as above. Without one, `tenant-1` with the key `2abc` and `tenant-12` with the key `abc` get the same downstream key. Prefixes can't protect tenants from each other on their own, so scope keys as well if callers aren't trusted.

A store with its own prefix adds it in front. The Redis store's `KeyPrefix` defaults to `idempotency:`, so with `options.KeyPrefix = "prod:"` its keys start with `idempotency:prod:`.

## Store Keys

Stores never see the client's key. An entry is stored under the prefix followed by a 32-character digest (the first 16 bytes of a SHA-256) of the scope and the client's key:

```
prod:9f86d081884c7d659a2feaa0c55ad015
```

The length is fixed, so a key can't overflow a column or an index, whichever store you use. Keys are also compared the same way on every store: SQL Server's default collation would otherwise treat `abc` and `ABC` as the same key. The prefix stays readable, so `SCAN idempotency:prod:*` and `WHERE [Key] LIKE 'tenant-42:%'` still work.

To find the entry for a key while debugging, compute its store key, or take the `KeyHash` from a log line, which is the same digest:

```csharp
var storeKey = IdempotencyStoreKey.For("abc-123", scope: "alice", policyName: "user", prefix: "prod:");
```

Pass `policyName` only if that policy defines its own `KeyScope`.

`IIdempotencyContext.Key` and downstream keys don't use this format. They still derive from the prefix, scope and client's key. On a scoped request, `Key` contains a control character that separates the scope from the client's key, so a client can't send a key that imitates a scoped one. That makes it invalid in an HTTP header, so use `DownstreamKey` to call another service.

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

Uses atomic Lua scripts for claim operations (SET NX) to guarantee consistency under concurrent access. The Redis package owns the connection lifecycle internally, applies `AbortOnConnectFail=false`, and reconnects lazily with throttling via `RedisIdempotencyOptions.MinimumReconnectInterval`. Expired entries are cleaned up automatically by Redis key TTL — no background cleanup needed.

### PostgreSQL Store

```csharp
builder.Services.AddIdempotencyGuardPostgresStore(options =>
{
    options.ConnectionString = "Host=localhost;Database=myapp";
    options.TableName = "idempotency_entries";
    options.AutoCreateTable = true;  // Creates table on first use
});
```

Uses `INSERT ... ON CONFLICT` for atomic claim operations. Expired entries are cleaned up by the background cleanup service using `DELETE ... FOR UPDATE SKIP LOCKED` for safe concurrent purging.

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

Keep `:` out of your provider and operation names. The key, provider and operation are joined with `:` before hashing, so key `k:stripe` with `("charge", "x")` produces the same downstream key as key `k` with `("stripe", "charge:x")`. A client chooses the key, so a name containing `:` could let one operation's key match another's.

## Edge Cases Handled

### Concurrent Requests
Two identical requests arrive simultaneously. The first claims the key and processes. The second waits (with exponential backoff) until the first completes, then replays the cached response.

### Crashed Process
A process claims a key, then crashes. The claim TTL expires, allowing retries to proceed with a new claim.

### Handler Outlives Its Claim
A handler runs past `ClaimTtl` and a retry takes over the key. Each claim carries a token, so when the first handler finishes it can't overwrite or release the new claim, and its response isn't stored. Both handlers will have run, so use a [downstream key](#downstream-key-generation) for anything with side effects, and raise `ClaimTtl` if `idempotency.claims.lost` is above zero.

### Original Request Fails
A request is waiting on a concurrent request that fails and releases its claim. The waiting request takes over and runs the handler rather than timing out.

### Response Too Large to Store
A response larger than `MaxResponseBodySize` (1 MB by default) still reaches the client, but it can't be stored. A retry gets a 410 rather than running the handler again, because the handler's side effects have already happened:

```
HTTP/1.1 410 Gone
X-Idempotent-Original-Status: 201
X-Idempotent-Replayed: true

{"error":"The original request completed with status 201, but its response was too large to store, so it can't be replayed"}
```

**A client that lost the response can't get it back with this key.** Once `ResponseTtl` passes, the key is free again, and a retry runs the handler. If responses this large are normal for you, raise the limit, which applies to every endpoint. For an endpoint that's safe to rerun, such as generating a report, let retries run the handler again:

```csharp
options.AddPolicy("rerunnable", p => p.OversizedResponse = OversizedResponseBehavior.RunHandlerAgain);
```

`idempotency.responses.oversized` shows when this happens, and `idempotency.replays.rejected` counts the 410s.

The limit is checked after the handler finishes, so it protects the store rather than the process: the whole response is held in memory first, however large it is. Responses also don't reach the client until the handler finishes, so streaming endpoints shouldn't go through the guard.

### Payload Mismatch
A client accidentally reuses an idempotency key with a different request body. The middleware detects the SHA256 fingerprint mismatch and returns 422.

### Upstream Timeout, Downstream Success
The client times out waiting for your API, but the payment provider call succeeds. The middleware stores the response despite the closed connection, because the write isn't tied to the request's cancellation token. When the client retries, it receives the correct cached response.

## Observability

Built-in [OpenTelemetry](https://opentelemetry.io/docs/languages/dotnet/) metrics under the `IdempotencyGuard` meter:

| Metric | Type | Description |
|--------|------|-------------|
| `idempotency.requests.total` | Counter | Total requests with idempotency keys |
| `idempotency.replays.total` | Counter | Stored responses replayed to a client |
| `idempotency.replays.rejected` | Counter | 410 responses for retries whose original response was too large to store |
| `idempotency.responses.oversized` | Counter | Responses too large to store. The `behavior` tag is `reject_retries` or `run_handler_again` |
| `idempotency.claims.total` | Counter | New claims created |
| `idempotency.claims.released` | Counter | Claims released without storing a response. The `reason` tag is `failure`, `response_too_large` or `should_store_response` |
| `idempotency.claims.lost` | Counter | Responses not stored because the handler outlived `ClaimTtl` and the claim was taken over |
| `idempotency.conflicts.total` | Counter | 409 responses for a key in progress, including waiters that timed out |
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

Logs and spans identify a request by `KeyHash` / `idempotency.key_hash`, the digest from the [store key](#store-keys). The raw key can replay a stored response, so it isn't recorded.

## Health Check

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<IdempotencyStoreHealthCheck>("idempotency-store");
```

## Upgrading

See the [2.0 upgrade guide](docs/upgrading-to-2.0.md). Some of its changes can affect a live system during the rollout.

Two settings are deprecated and will be removed in 3.0. Using either gives a build warning with the ID `IDEMP001`, which is an error under `TreatWarningsAsErrors`. Add `<NoWarn>$(NoWarn);IDEMP001</NoWarn>` to your project if you can't move yet.

| Deprecated | Use instead |
|---|---|
| `MissingKeyPolicy.Allow` | `UnmarkedEndpoints = UnmarkedEndpointBehavior.GuardWhenKeyPresent` |
| `MissingKeyPolicy.Reject` | `UnmarkedEndpoints = UnmarkedEndpointBehavior.RequireKey` |
| `Enabled = false` | Don't call `UseIdempotencyGuard()` |

Setting `MissingKeyPolicy` and `UnmarkedEndpoints` to different behaviours fails at startup.

## Stability and Performance

The repo carries a stress suite that tries to break the idempotency guarantee under load.

`tests/IdempotencyGuard.Stress.Tests` runs six concurrency scenarios against every store (in-memory, Redis, PostgreSQL, SQL Server) on real Testcontainers instances, plus six end-to-end scenarios through the ASP.NET Core pipeline: thundering herds on one key, interleaved multi-key load, mutual exclusion on claim/release cycles, racing fingerprints, read storms on a completed entry, and expired-claim takeover. 36 tests, all green, run before every release.

```bash
dotnet test tests/IdempotencyGuard.Stress.Tests/

# ten times the load
IDEMPOTENCY_STRESS_SCALE=10 dotnet test tests/IdempotencyGuard.Stress.Tests/
```

Measured overhead with the in-memory store, via BenchmarkDotNet on .NET 10 and a GitHub Actions runner (4 vCPU):

| Path | Mean | Allocated |
|---|---:|---:|
| Handler with no guard | 13.8 µs | 9.09 KB |
| Guard, new key | 62.9 µs | 26.77 KB |
| Guard, replayed response | 52.0 µs | 25.3 KB |

Around 49 µs per first request and 38 µs per replay on that runner, on top of whatever your store costs. One caveat worth reading before you send large payloads through it: fingerprint hashing normalises JSON, so a 64 KB body costs about 1.4 ms unless you narrow it with `FingerprintProperties` or `MaxFingerprintBodySize`.

Full numbers, methodology and limitations: [docs/stability-and-performance.md](docs/stability-and-performance.md).

## License

MIT
