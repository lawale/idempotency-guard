# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]
**Breaking release. See the [2.0 upgrade guide](docs/upgrading-to-2.0.md); some of these changes can affect a live system during the rollout.**

### Security
- A guard placed before `UseAuthorization()` served stored responses to unauthorized callers, including anonymous ones, and stored their 401s for the rightful caller's retry to receive. It now throws on endpoints that require authorization (`SuppressAuthorizationOrderCheck` turns this off), and the docs give the required order
- Keys can now be scoped to a caller with `IdempotencyOptions.KeyScope`, so one caller can't replay another's response. This is opt-in, because the right identifier depends on your system. A startup warning fires until it's configured (`SuppressUnscopedKeysWarning` turns this off), and a `null` scope is rejected with a 403
- Raw idempotency keys no longer reach logs or spans, which now carry a truncated hash (`KeyHash`, `idempotency.key_hash`)

### Changed
- **Breaking:** `IIdempotencyStore.SetResponseAsync` and `ReleaseClaimAsync` take a claim token and return whether they applied, and `ClaimResult.Claimed` carries the token. The SQL stores add a nullable claim token column, created automatically when `AutoCreateTable` is on
- **Breaking:** `IdempotencyOptions` and related types moved to `IdempotencyGuard.AspNetCore`, keeping the `IdempotencyGuard` namespace
- **Breaking:** numbers now hash as numbers, which changes the fingerprint of any JSON body containing one
- `IdempotencyProblem.IdempotencyKey` now holds the key the client sent, without any prefix or scope

### Fixed
- A request that outlived its claim could overwrite or release the claim of the request that took over, on every store. Its response could then be replayed for a different payload
- A client disconnecting after the handler ran could discard the response and release the claim on the SQL stores, so the retry ran the handler again
- JSON bodies with duplicate property names failed the request with a 500. A repeated name now hashes as its last value, which is what the handler deserializes
- `{"amount":1}` and `{"amount":"1"}` shared a fingerprint
- Requests waiting under `WaitThenReplay` timed out with a 409 when the original failed. They now take over
- In-memory store: an entry past its `ResponseTtl` returned a 409 or 422 instead of being claimable, and a response stored after its claim was purged made every retry return a 422
- An action's `[Idempotent]` attribute discarded its controller's settings. They now merge, and the most specific value wins
- Concurrent first requests no longer race to create the SQL stores' tables within an instance
- The PostgreSQL and SQL Server READMEs listed the wrong name for the fingerprint column

### Added
- Named policies (`IdempotencyOptions.AddPolicy`, `[Idempotent(Policy = "...")]`) for per-endpoint scoping and response storage. Unknown names fail at startup
- `ShouldStoreResponse` decides by status code whether a response is stored, globally or per policy. By default every response is stored, including 5xx
- `idempotency.claims.lost` counter for handlers that outlived `ClaimTtl` and lost their claim
- Stress test suite (`tests/IdempotencyGuard.Stress.Tests`) covering thundering herds, interleaved multi-key load, claim/release mutual exclusion, racing fingerprints, read storms and expired-claim takeover against every store, plus end-to-end retry storms through the ASP.NET Core pipeline. Load is tunable via the `IDEMPOTENCY_STRESS_SCALE` environment variable
- BenchmarkDotNet suite (`benchmarks/IdempotencyGuard.Benchmarks`) measuring per-request middleware overhead, store operation cost and fingerprint hashing cost by body size
- `docs/stability-and-performance.md` documenting the measured overhead and the limits of what the stress suite proves
- Stress suite runs in the release workflow, before packages publish. A manual `stress` workflow reruns it at ten times the load and publishes benchmark results

### Changed
- NuGet packages now carry an icon, link to the GitHub repo instead of the author's site, and point their release notes at this changelog
- Package descriptions lead with "Idempotency-Key middleware for ASP.NET Core", and tags now include `idempotency-key` (plus `minimal-api` on `IdempotencyGuard.AspNetCore`)

## [1.3.0] - 2026-04-26
### Added
- OpenTelemetry tracing for store operations and cleanup through the `IdempotencyGuard` activity source (`IdempotencyDiagnostics.ActivitySourceName`)

### Changed
- `IdempotencyGuard.Redis` now owns Redis connection lifecycle internally instead of registering or reusing `IConnectionMultiplexer`
- Redis connections are created lazily, reconnect on demand, force `AbortOnConnectFail=false`, and throttle reconnect attempts via `RedisIdempotencyOptions.MinimumReconnectInterval`
- The middleware passes `HttpContext.RequestAborted` to store claim, read and write calls; claim release deliberately ignores it so a disconnect can't strand a claim
- Redis connections now open asynchronously

### Fixed
- `InMemoryIdempotencyStore.SetResponseAsync` now swaps the entry atomically, preventing torn reads and an overwrite race with expired-claim takeover

### Performance
- The in-memory store dropped its own once-a-minute scan for expired entries; `IdempotencyCleanupService` handles expiry instead
- The in-memory store keeps response headers as a dictionary rather than round-tripping them through JSON
- Fewer allocations when storing responses: bodies skip an extra copy, and error messages skip an intermediate string conversion

## [1.2.4] - 2026-04-03
### Added
- `ReplayedHeaderName` option on `IdempotencyOptions` to customise the response header name added to replayed responses (default: `X-Idempotent-Replayed`)
- `KeyPrefixItemKey` option on `IdempotencyOptions` for per-request key prefix overrides via `HttpContext.Items` (default: `IdempotencyKeyPrefix`)

### Fixed
- Completed responses could be deleted from Redis and in-memory stores when the client disconnects after the response is stored but before it is flushed
- Responses exceeding `MaxResponseBodySize` left idempotency keys stuck in claimed state until claim TTL expired
- Request body memory is now capped by `MaxFingerprintBodySize` during the read, rather than buffering the full body before truncation
- Extra fingerprint segments (query/route values) are now URI-encoded before joining, preventing ambiguous collisions when values contain delimiter characters

## [1.2.3] - 2026-03-18
### Fixed
- Bug fix in `IdempotencyGuard.Redis` where LuaScript.Prepare() in `StackExchange.Redis` works by replacing @paramName tokens in the script with the appropriate KEYS[n] or ARGV[n] references

## [1.2.2] - 2026-03-10
### Added
- `RequireIdempotency()` extension for configuring idempotency on minimal API endpoints

## [1.2.1] - 2026-03-10
### Added
- Package-specific READMEs on NuGet

## [1.2.0] - 2026-03-10

### Added
- `FingerprintQueryParameters` on `[Idempotent]` attribute to include query string values in the fingerprint
- `FingerprintRouteValues` on `[Idempotent]` attribute to include route parameter values in the fingerprint

## [1.1.0] - 2026-03-09

### Added
- `FingerprintProperties` on `[Idempotent]` attribute for selective property-based fingerprinting via `nameof()` (case-insensitive matching)

## [1.0.0] - 2026-03-07

### Added
- Core idempotency models: `IdempotencyEntry`, `IdempotentResponse`, `ClaimResult` discriminated union
- `IIdempotencyStore` interface with claim-then-process pattern
- SHA256 request fingerprinting with JSON property normalisation
- In-memory store with TTL-based expiration for development and testing
- ASP.NET Core middleware with full request lifecycle handling
- `IIdempotencyContext` for downstream key generation
- `[Idempotent]` attribute for per-endpoint configuration (TTL overrides, required flag)
- Configurable `MissingKeyPolicy` (Allow/Reject) and `ConcurrentRequestPolicy` (WaitThenReplay/ReturnConflict)
- Request payload mismatch detection (422 Unprocessable Entity)
- Concurrent request handling with wait-and-replay support
- `X-Idempotent-Replayed: true` header on cached responses
- Redis store with atomic Lua scripts (SET NX) for distributed environments
- PostgreSQL store with `INSERT ... ON CONFLICT DO NOTHING` for atomic claims
- SQL Server store with `MERGE` and `HOLDLOCK` for atomic claims
- `EndpointFilter` callback for programmatic route-level opt-in/opt-out
- `ErrorResponseFactory` with `IdempotencyProblem` and `IdempotencyErrorKind` for custom error response formatting
- Response header filtering with `HeaderAllowList`, `HeaderDenyList`, and built-in hop-by-hop defaults
- `MaxFingerprintBodySize` option to cap request body bytes used for fingerprint hashing (default 1 MB)
- `KeyPrefix` option for environment or tenant namespacing of idempotency keys
- `IPurgableIdempotencyStore` interface (ISP-compliant) for stores that need active expired-entry removal
- Background cleanup service (`IdempotencyCleanupService`) with configurable interval, batch size, and iteration limits
- `CleanupOptions` for tuning cleanup behaviour (enabled flag, interval, batch size, max iterations per sweep)
- In-memory, PostgreSQL, and SQL Server stores implement `IPurgableIdempotencyStore`; Redis relies on native key TTL
- Optimised expiry indexes on PostgreSQL (`expires_at`) and SQL Server (`ExpiresAtUtc`) for cleanup and reclaim queries
- OpenTelemetry metrics: request counts, replays, claims, releases, conflicts, fingerprint mismatches, store latency, purge counts, purge latency
- Health check integration via `IdempotencyStoreHealthCheck`
- Sample payment API demonstrating middleware usage
