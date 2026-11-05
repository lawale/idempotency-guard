# Stability and performance

Two questions come up often enough to deserve a written answer: is this safe to put in front of real money, and what does it cost per request?

## Where the code came from

IdempotencyGuard.NET was extracted from a payments service that runs in production. The claim-then-process pattern, the fingerprint check, the expired-claim takeover and the downstream key propagation all exist because each one was needed to stop a specific class of duplicate charge. The open-source version is that code with the service-specific bits removed and a store abstraction put in their place.

That's an origin story, not evidence. So the repo carries a stress suite whose whole job is to try to break the guarantee, plus benchmarks that put a number on the overhead.

## What the stress suite checks

`tests/IdempotencyGuard.Stress.Tests` runs the same six store-level scenarios against every store implementation (in-memory, Redis, PostgreSQL, SQL Server), and six end-to-end scenarios through the real ASP.NET Core pipeline against in-memory and Redis. Every store hits a real server in a Testcontainers container; nothing is mocked.

Store level:

- **Thundering herd.** 128 callers claim one key simultaneously, ten rounds, fresh key each round. Exactly one `Claimed`, the rest `AlreadyClaimed`.
- **Sustained mixed load.** 250 keys, four requests each, shuffled so duplicates of different keys interleave. Every key claimed exactly once.
- **Mutual exclusion.** Workers claim and release the same key in a loop while a counter tracks how many hold it at once. The observed maximum must be 1.
- **Racing fingerprints.** Four different payloads race for one key. One wins; every loser is told about the *same* winning fingerprint, which catches half-written entries.
- **Read storm on a completed entry.** 2,000 concurrent reads of one stored response, each compared byte for byte. Catches torn or partial reads.
- **Expired-claim takeover.** A claim lapses, then 128 callers race to take it over. Exactly one wins.

Through the middleware, with a handler that takes 50 ms so concurrent requests really overlap the in-flight one:

- A 128-request retry storm on one key executes the handler once, and every caller gets the same 201 body.
- Under `ReturnConflict`, one caller executes and everyone else gets a 409 or a replay. Nobody sees a 5xx.
- 250 keys x 4 requests: handler call count equals key count, exactly.
- A replay storm after completion serves all 1,000 from the store, with `X-Idempotent-Replayed: true` on each.
- A handler that throws releases its claim, and the retry succeeds rather than hanging.
- Half a herd sends a tampered amount under the same key: the tampered half gets 422, the handler still runs once.

All 36 pass, in about 20 seconds once the containers are up. CI runs them on every push at the default size. A weekly workflow (`.github/workflows/stress.yml`) reruns everything at ten times the load and publishes the benchmark output.

To push harder locally:

```bash
IDEMPOTENCY_STRESS_SCALE=10 dotnet test tests/IdempotencyGuard.Stress.Tests/
```

## Measured overhead

Benchmarks live in `benchmarks/IdempotencyGuard.Benchmarks`, built on BenchmarkDotNet. Run them with:

```bash
dotnet run --project benchmarks/IdempotencyGuard.Benchmarks -c Release -f net9.0 -- --filter '*'
```

Numbers below were measured on an Apple M2 Pro (10 cores), .NET 9.0.17, Arm64. Treat them as ratios rather than absolutes; your hardware will differ.

### Per request, end to end

An ASP.NET Core `TestServer` POST to a trivial handler, with and without the guard, using the in-memory store:

| Path | Mean | vs baseline | Allocated |
|---|---:|---:|---:|
| No guard | 8.47 µs | 1.00x | 9.4 KB |
| Guard, new key (claim + store response) | 22.58 µs | 2.67x | 27.1 KB |
| Guard, replayed response | 16.09 µs | 1.90x | 25.4 KB |

So roughly 14 µs of added work on a first request and 7.6 µs on a replay, with a 57-byte JSON body.

The 2.67x ratio looks alarming and isn't. `TestServer` skips the kernel, the socket, TLS and the network, so the baseline here is about as cheap as an HTTP request can possibly get. Against a real handler that talks to a database or a payment provider, the same 14 µs disappears into the noise. It does not disappear against a 200 µs in-memory endpoint, which is why the absolute number matters more than the multiple.

### Store operations

In-memory store, isolated from the pipeline:

| Operation | Mean | Allocated |
|---|---:|---:|
| `TryClaim` (already completed) | 45 ns | 288 B |
| `GetResponse` (replay) | 86 ns | 440 B |
| `TryClaim` (new key) | 588 ns | 448 B |
| `TryClaim` + `SetResponse` | 1.08 µs | 760 B |

Redis, Postgres and SQL Server add a round trip on top. From the stress runs against local Docker containers, single-claim latency at 128-way concurrency looked like this:

| Store | p50 | p99 |
|---|---:|---:|
| In-memory | < 0.01 ms | 0.27 ms |
| Redis 7 | 0.57 ms | 3.44 ms |
| PostgreSQL 16 | 2.14 ms | 4.83 ms |
| SQL Server 2022 | 4.33 ms | 111 ms |

The SQL Server figures came from an x86 image under emulation on Apple Silicon and are not representative; ignore them until someone reruns them on native hardware. Container-local Redis and Postgres also skip real network latency, so add your own RTT.

### The part worth knowing about

Fingerprint hashing is the one place where cost scales with your payload, and it scales worse than you'd expect:

| Body size | `RequestFingerprint.Compute` | Allocated |
|---|---:|---:|
| 256 B | 5.03 µs | 7.7 KB |
| 4 KB | 74.6 µs | 106 KB |
| 64 KB | 1.25 ms | 1.63 MB |

SHA-256 isn't the problem. The JSON normalisation is: the body gets parsed into a `JsonDocument`, rebuilt as a sorted object graph and re-serialised so that `{"a":1,"b":2}` and `{"b":2,"a":1}` hash the same. That's the right semantics and it's expensive.

If your endpoints take large bodies, use one of the knobs:

- `[Idempotent(FingerprintProperties = ["Amount", "Currency"])]` narrows the hash to the fields that decide whether two requests are the same. Extraction from a 64 KB body takes 225 µs, and the hash then runs over the handful of extracted bytes instead of all 64 KB: about 230 µs total against 1.25 ms, and roughly 1.6 KB of managed allocation against 1.63 MB.
- `options.MaxFingerprintBodySize` caps how many bytes are read. Setting it to 0 skips body hashing entirely, at the cost of no longer detecting a changed payload.

For the payment-sized payloads this library was written for, a few hundred bytes to a couple of KB, the default is fine.

## What this doesn't prove

Stress tests show that the concurrency guarantees hold on one machine against a single store node. They say nothing about Redis failover mid-claim, Postgres primary promotion, or a partition between your app and the store. `RedisConnectionResilienceTests` in the integration suite covers reconnection, and that's the extent of it.

Throughput ceilings are the store's, not the library's. Redis will do tens of thousands of claims per second and Postgres rather fewer; the guard adds microseconds to whatever your store gives you.

## Targets

net8.0, net9.0 and net10.0. The public API follows semver; see [CHANGELOG.md](../CHANGELOG.md) for what has changed and when.
