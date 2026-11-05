using System.Text;
using FluentAssertions;

namespace IdempotencyGuard.Integration.Tests;

public abstract class IdempotencyStoreContractTests
{
    protected abstract IIdempotencyStore Store { get; }

    private static readonly TimeSpan DefaultClaimTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultResponseTtl = TimeSpan.FromHours(1);

    private static string UniqueKey() => $"test-{Guid.NewGuid():N}";

    /// <summary>
    /// Separate store instances share nothing but the database, like separate processes.
    /// Runs several rounds because the race doesn't lose every time.
    /// </summary>
    protected static async Task InstancesStartingTogetherCanClaimAsync(
        Func<string, Task> prepareTable,
        Func<string, IIdempotencyStore> newStore)
    {
        const int rounds = 5;
        const int instances = 8;

        for (var round = 0; round < rounds; round++)
        {
            var table = $"race_{Guid.NewGuid():N}";
            await prepareTable(table);

            var stores = Enumerable.Range(0, instances).Select(_ => newStore(table)).ToArray();
            var claims = await Task.WhenAll(stores.Select(store =>
                Task.Run(() => store.TryClaimAsync(UniqueKey(), "fp", DefaultClaimTtl))));

            claims.Should().AllBeOfType<ClaimResult.Claimed>();
        }
    }

    [Fact]
    public async Task TryClaim_new_key_returns_claimed()
    {
        var result = await Store.TryClaimAsync(UniqueKey(), "fp-1", DefaultClaimTtl);

        result.Should().BeOfType<ClaimResult.Claimed>();
    }

    [Fact]
    public async Task TryClaim_existing_claimed_key_returns_already_claimed()
    {
        var key = UniqueKey();
        await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);

        var result = await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);

        result.Should().BeOfType<ClaimResult.AlreadyClaimed>();
    }

    [Fact]
    public async Task TryClaim_with_different_fingerprint_returns_mismatch()
    {
        var key = UniqueKey();
        await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);

        var result = await Store.TryClaimAsync(key, "fp-2", DefaultClaimTtl);

        var mismatch = result.Should().BeOfType<ClaimResult.FingerprintMismatch>().Subject;
        mismatch.ExpectedFingerprint.Should().Be("fp-1");
        mismatch.ActualFingerprint.Should().Be("fp-2");
    }

    [Fact]
    public async Task TryClaim_completed_key_returns_completed()
    {
        var key = UniqueKey();
        var claimToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl)).ClaimToken;
        await Store.SetResponseAsync(key, claimToken, CreateResponse(200, "ok"), DefaultResponseTtl);

        var result = await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);

        result.Should().BeOfType<ClaimResult.Completed>();
    }

    [Fact]
    public async Task TryClaim_expired_claim_allows_new_claim()
    {
        var key = UniqueKey();
        await Store.TryClaimAsync(key, "fp-1", TimeSpan.FromMilliseconds(100));

        await Task.Delay(300);

        var result = await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);

        result.Should().BeOfType<ClaimResult.Claimed>();
    }

    [Fact]
    public async Task TryClaim_completed_key_different_fingerprint_returns_mismatch()
    {
        var key = UniqueKey();
        var claimToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl)).ClaimToken;
        await Store.SetResponseAsync(key, claimToken, CreateResponse(200, "ok"), DefaultResponseTtl);

        var result = await Store.TryClaimAsync(key, "fp-2", DefaultClaimTtl);

        var mismatch = result.Should().BeOfType<ClaimResult.FingerprintMismatch>().Subject;
        mismatch.ExpectedFingerprint.Should().Be("fp-1");
        mismatch.ActualFingerprint.Should().Be("fp-2");
    }

    [Fact]
    public async Task SetResponse_then_get_response_round_trips_correctly()
    {
        var key = UniqueKey();
        var claimToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl)).ClaimToken;

        var original = CreateResponse(201, """{"id":"pay_123"}""");
        await Store.SetResponseAsync(key, claimToken, original, DefaultResponseTtl);

        var retrieved = await Store.GetResponseAsync(key);

        retrieved.Should().NotBeNull();
        retrieved!.StatusCode.Should().Be(201);
        retrieved.Headers.Should().ContainKey("Content-Type");
        retrieved.Headers["Content-Type"].Should().BeEquivalentTo(["application/json"]);
        Encoding.UTF8.GetString(retrieved.Body.Span).Should().Be("""{"id":"pay_123"}""");
    }

    [Fact]
    public async Task GetResponse_returns_null_for_unknown_key()
    {
        var result = await Store.GetResponseAsync(UniqueKey());

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetResponse_returns_null_for_claimed_but_not_completed_key()
    {
        var key = UniqueKey();
        await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);

        var result = await Store.GetResponseAsync(key);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseClaim_allows_new_claim()
    {
        var key = UniqueKey();
        var claimToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl)).ClaimToken;
        await Store.ReleaseClaimAsync(key, claimToken);

        var result = await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);

        result.Should().BeOfType<ClaimResult.Claimed>();
    }

    [Fact]
    public async Task Concurrent_claims_only_one_succeeds()
    {
        var key = UniqueKey();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl));

        var results = await Task.WhenAll(tasks);

        results.OfType<ClaimResult.Claimed>().Should().HaveCount(1);
        results.OfType<ClaimResult.AlreadyClaimed>().Should().HaveCount(9);
    }

    [Fact]
    public async Task TryClaim_expired_completed_entry_allows_new_claim()
    {
        var key = UniqueKey();
        var claimToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl)).ClaimToken;
        await Store.SetResponseAsync(key, claimToken, CreateResponse(200, "ok"), TimeSpan.FromMilliseconds(100));

        await Task.Delay(300);

        var result = await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);

        result.Should().BeOfType<ClaimResult.Claimed>();
    }

    [Fact]
    public async Task TryClaim_expired_completed_entry_with_different_fingerprint_allows_new_claim()
    {
        var key = UniqueKey();
        var claimToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl)).ClaimToken;
        await Store.SetResponseAsync(key, claimToken, CreateResponse(200, "ok"), TimeSpan.FromMilliseconds(100));

        await Task.Delay(300);

        var result = await Store.TryClaimAsync(key, "fp-2", DefaultClaimTtl);

        result.Should().BeOfType<ClaimResult.Claimed>();
    }

    [Fact]
    public async Task SetResponse_with_stale_token_returns_false_and_leaves_the_new_claim_in_place()
    {
        var key = UniqueKey();
        var staleToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-1", TimeSpan.FromMilliseconds(100))).ClaimToken;
        await Task.Delay(300);
        var currentToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-2", DefaultClaimTtl)).ClaimToken;

        var stored = await Store.SetResponseAsync(key, staleToken, CreateResponse(200, "stale"), DefaultResponseTtl);

        stored.Should().BeFalse();
        (await Store.GetResponseAsync(key)).Should().BeNull();

        (await Store.SetResponseAsync(key, currentToken, CreateResponse(200, "current"), DefaultResponseTtl)).Should().BeTrue();
        var response = await Store.GetResponseAsync(key);
        Encoding.UTF8.GetString(response!.Body.Span).Should().Be("current");
    }

    [Fact]
    public async Task ReleaseClaim_with_stale_token_returns_false_and_leaves_the_new_claim_in_place()
    {
        var key = UniqueKey();
        var staleToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-1", TimeSpan.FromMilliseconds(100))).ClaimToken;
        await Task.Delay(300);
        await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);

        var released = await Store.ReleaseClaimAsync(key, staleToken);

        released.Should().BeFalse();
        (await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl)).Should().BeOfType<ClaimResult.AlreadyClaimed>();
    }

    [Fact]
    public async Task ReleaseClaim_does_not_remove_a_completed_entry()
    {
        var key = UniqueKey();
        var claimToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl)).ClaimToken;
        await Store.SetResponseAsync(key, claimToken, CreateResponse(200, "ok"), DefaultResponseTtl);

        var released = await Store.ReleaseClaimAsync(key, claimToken);

        released.Should().BeFalse();
        (await Store.GetResponseAsync(key)).Should().NotBeNull();
    }

    [Fact]
    public async Task SetResponse_after_the_claim_was_released_returns_false()
    {
        var key = UniqueKey();
        var claimToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl)).ClaimToken;
        await Store.ReleaseClaimAsync(key, claimToken);

        var stored = await Store.SetResponseAsync(key, claimToken, CreateResponse(200, "ok"), DefaultResponseTtl);

        stored.Should().BeFalse();
        (await Store.GetResponseAsync(key)).Should().BeNull();
    }

    protected static IdempotentResponse CreateResponse(int statusCode, string body) =>
        new()
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"]
            },
            Body = Encoding.UTF8.GetBytes(body)
        };
}
