# Upgrading to 2.0

2.0 closes several ways a retry could run a handler twice or receive someone else's response. Most of the changes need no code from you, but four things can affect a live system during the rollout, so read those first.

## Check before you deploy

### 1. Move the guard after authorization

`UseIdempotencyGuard()` has to come after `UseAuthorization()`:

```csharp
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseIdempotencyGuard();
```

In 1.x, a guard placed before authorization served stored responses to callers who would have been rejected, including anonymous ones. It also stored their 401s, so the rightful caller's retry got a 401 back.

2.0 throws `InvalidOperationException` on any endpoint that requires authorization if authorization hasn't run yet. **An app that was misordered will start failing on those endpoints after the upgrade**, which is deliberate. Check for this before you deploy. `SuppressAuthorizationOrderCheck` turns the check off if you enforce authorization some other way.

The check can't see the problem if you call `UseIdempotencyGuard()` before an explicit `UseRouting()`, because the endpoint isn't known yet.

### 2. Add the new column if you manage your own schema

The SQL stores need a new nullable column. With `AutoCreateTable = true` it's added on first use. With `AutoCreateTable = false`, **add it before deploying**, or every claim will fail:

```sql
-- PostgreSQL
ALTER TABLE idempotency_entries ADD COLUMN IF NOT EXISTS claim_token TEXT;

-- SQL Server
ALTER TABLE [IdempotencyEntries] ADD ClaimToken NVARCHAR(64) NULL;
```

1.x instances keep working with the column in place, so you can add it ahead of the deploy.

### 3. Retries that span the upgrade run the handler again

Store keys are now a prefix followed by a fixed-length digest, rather than the client's key. **Entries stored by 1.x can't be found under the new keys**, so a retry of a request that completed on 1.x runs the handler again.

- **Downstream keys don't change** when you upgrade, so a payment provider still catches a duplicate from a call that used `DownstreamKey`. Other side effects aren't protected.
- **Don't run 1.x and 2.0 side by side.** They compute different keys for the same request, so neither sees the other's claims, and concurrent duplicates can both run. Replace all instances at once rather than in a rolling deploy.
- **Deploy at a quiet time and let in-flight retries drain first.** Most retries happen within minutes, so the exposure is small if you do.

Old entries do no harm. They expire through the cleanup service or Redis key TTL as usual.

### 4. Turning on key scoping changes every key

Enabling `KeyScope` changes both the store key and the value `DownstreamKey` is derived from. A retry that spans the moment you enable it counts as a new request: the handler runs again **and sends the payment provider a new downstream key, so the provider won't catch the duplicate**.

Enable scoping at a quiet time, and wait for in-flight retries to drain first. Downstream keys have to stay scoped: otherwise two callers using the same key would send the provider the same key, and the second charge would be treated as a duplicate of the first.

## Breaking API changes

### `IIdempotencyStore`

Claims now carry a token, so a request that outlived its claim can't overwrite or release a newer holder's claim. Custom stores need to:

- Generate a unique token on every successful claim, including takeovers, persist it, and return it through `new ClaimResult.Claimed(token)`.
- Accept the token in `SetResponseAsync` and `ReleaseClaimAsync`. Apply the change only if the entry is still claimed under that token, atomically, and return whether it was applied.
- Treat any expired entry as absent in `TryClaimAsync`, whether it was claimed or completed.

```csharp
Task<bool> SetResponseAsync(string key, string claimToken, IdempotentResponse response, TimeSpan responseTtl, CancellationToken ct = default);
Task<bool> ReleaseClaimAsync(string key, string claimToken, CancellationToken ct = default);
```

### Package changes

- `IdempotencyOptions`, `IdempotencyPolicy`, `HeaderFilter`, `CleanupOptions` and the related enums now ship in `IdempotencyGuard.AspNetCore`, because they refer to `HttpContext`. The namespace is still `IdempotencyGuard`. Only code that references the core package without the AspNetCore package is affected.

### Telemetry

- Logs carry `KeyHash` instead of `Key`, and spans carry `idempotency.key_hash` instead of `idempotency.key`. The value is the digest from the store key, so it correlates requests and leads to the row, but can't be used to replay a response. Update any dashboards or log queries that use the old names.
- New counter: `idempotency.claims.lost`. It counts responses that weren't stored because the handler outlived `ClaimTtl` and the claim was taken over. If it's above zero, raise `ClaimTtl`.

### Behaviour

- `MaxFingerprintBodySize = 0` no longer reads the request body. It skipped hashing the body as documented, but read all of it into memory first, with no limit.
- Keys longer than `MaxKeyLength` (1024 characters by default), or containing control characters, are rejected with a 400. Previously, long keys failed with a 500 on SQL Server (over 256 characters) and PostgreSQL (over about 2.7 KB). If your clients send longer keys, raise `MaxKeyLength`, which is now safe on every store.
- `KeyPrefix` is limited to 128 characters. A longer configured prefix fails at startup, and a longer per-request prefix fails the request.
- Keys differing only by case are now separate on SQL Server, matching the other stores.
- Numbers now hash as numbers in fingerprints, so `{"amount":1}` and `{"amount":"1"}` no longer match.
- Fingerprints now include `PathBase` and the hosts an endpoint declares with `RequireHost` or `[Host]`. Reusing a key on an endpoint split that way now gets a 422 rather than the other endpoint's response. The `Host` header isn't included, so a retry whose `Host` a proxy rewrote still replays.
- Fingerprint fields can no longer run into each other, so `POST /pay:{}` with no body no longer matches `POST /pay` with the body `{}`.
- End `KeyPrefix` with a delimiter if it doesn't have one already. Without one, `tenant-1` with the key `2abc` and `tenant-12` with the key `abc` get the same downstream key. Adding a delimiter changes your downstream keys, so treat it like turning on key scoping.
- `IdempotencyProblem.IdempotencyKey` now holds the key the client sent, without any prefix or scope.
- A request that waited under `WaitThenReplay` now takes over if the original request fails, rather than timing out with a 409.
- A client disconnecting no longer cancels the response write. On the SQL stores, this used to discard the response and release the claim, so the retry ran the handler a second time.
- An in-memory entry past its `ResponseTtl` can be claimed again straight away, matching the other stores.
- A response is no longer stored if its claim is gone. Previously the in-memory store created an entry with an empty fingerprint, and every retry got a 422.
