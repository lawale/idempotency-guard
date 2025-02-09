# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.0] - 2026-02-15

### Added
- Core idempotency models: `IdempotencyEntry`, `IdempotentResponse`, `ClaimResult` discriminated union
- `IIdempotencyStore` interface with claim-then-process pattern
- SHA256 request fingerprinting with JSON property normalization
- In-memory store with TTL-based expiration for development and testing
- ASP.NET Core middleware with full request lifecycle handling
- `IIdempotencyContext` for downstream key generation
- `[Idempotent]` attribute for per-endpoint configuration
- Configurable `MissingKeyPolicy` (Allow/Reject) and `ConcurrentRequestPolicy` (Reject/WaitThenReplay)
- Request payload mismatch detection (422 Unprocessable Entity)
- Concurrent request handling with wait-and-replay support
- `X-Idempotent-Replayed: true` header on cached responses
- Redis store with atomic Lua scripts (SET NX) for distributed environments
- PostgreSQL store with `INSERT ... ON CONFLICT DO NOTHING` for atomic claims
- SQL Server store with `MERGE` and `HOLDLOCK` for atomic claims
- OpenTelemetry metrics: request counts, replays, claims, conflicts, fingerprint mismatches, store latency
- Health check integration via `IdempotencyStoreHealthCheck`
- Sample payment API demonstrating middleware usage
- Comprehensive README with configuration reference and edge case documentation
