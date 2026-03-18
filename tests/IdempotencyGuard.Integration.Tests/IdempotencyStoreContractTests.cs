using System.Text;
using FluentAssertions;

namespace IdempotencyGuard.Integration.Tests;

public abstract class IdempotencyStoreContractTests
{
    protected abstract IIdempotencyStore Store { get; }

    private static readonly TimeSpan DefaultClaimTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultResponseTtl = TimeSpan.FromHours(1);

    private static string UniqueKey() => $"test-{Guid.NewGuid():N}";

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
        await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);
        await Store.SetResponseAsync(key, CreateResponse(200, "ok"), DefaultResponseTtl);

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
        await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);
        await Store.SetResponseAsync(key, CreateResponse(200, "ok"), DefaultResponseTtl);

        var result = await Store.TryClaimAsync(key, "fp-2", DefaultClaimTtl);

        var mismatch = result.Should().BeOfType<ClaimResult.FingerprintMismatch>().Subject;
        mismatch.ExpectedFingerprint.Should().Be("fp-1");
        mismatch.ActualFingerprint.Should().Be("fp-2");
    }

    [Fact]
    public async Task SetResponse_then_get_response_round_trips_correctly()
    {
        var key = UniqueKey();
        await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);

        var original = CreateResponse(201, """{"id":"pay_123"}""");
        await Store.SetResponseAsync(key, original, DefaultResponseTtl);

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
        await Store.TryClaimAsync(key, "fp-1", DefaultClaimTtl);
        await Store.ReleaseClaimAsync(key);

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
