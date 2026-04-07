# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]
### Changed
- `IdempotencyGuard.Redis` now owns Redis connection lifecycle internally instead of registering or reusing `IConnectionMultiplexer`
- Redis connections are created lazily, reconnect on demand, force `AbortOnConnectFail=false`, and throttle reconnect attempts via `RedisIdempotencyOptions.MinimumReconnectInterval`

## [1.2.3] - 2026-04-03
### Added
- `ReplayedHeaderName` option on `IdempotencyOptions` to customise the response header name added to replayed responses (default: `X-Idempotent-Replayed`)
- `KeyPrefixItemKey` option on `IdempotencyOptions` for per-request key prefix overrides via `HttpContext.Items` (default: `IdempotencyKeyPrefix`)

### Fixed
- Completed responses could be deleted from Redis and in-memory stores when the client disconnects after the response is stored but before it is flushed
- Responses exceeding `MaxResponseBodySize` left idempotency keys stuck in claimed state until claim TTL expired
- Request body memory is now capped by `MaxFingerprintBodySize` during the read, rather than buffering the full body before truncation
- Extra fingerprint segments (query/route values) are now URI-encoded before joining, preventing ambiguous collisions when values contain delimiter characters

## [1.2.1] - 2026-03-18
### Fixed
- Bug fix in `IdempotencyGuard.Redis` where LuaScript.Prepare() in `StackExchange.Redis` works by replacing @paramName tokens in the script with the appropriate KEYS[n] or ARGV[n] references

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
