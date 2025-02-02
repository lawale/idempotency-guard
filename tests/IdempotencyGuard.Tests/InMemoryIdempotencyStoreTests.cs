using System.Text;
using FluentAssertions;

namespace IdempotencyGuard.Tests;

public class InMemoryIdempotencyStoreTests : IDisposable
{
    private readonly InMemoryIdempotencyStore _store = new();

    [Fact]
    public async Task TryClaim_new_key_returns_claimed()
    {
        var result = await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        result.Should().BeOfType<ClaimResult.Claimed>();
    }

    [Fact]
    public async Task TryClaim_existing_claimed_key_returns_already_claimed()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        var result = await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        result.Should().BeOfType<ClaimResult.AlreadyClaimed>();
    }

    [Fact]
    public async Task TryClaim_completed_key_returns_completed()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));
        await _store.SetResponseAsync("key-1", CreateResponse(200, "ok"), TimeSpan.FromHours(24));

        var result = await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        result.Should().BeOfType<ClaimResult.Completed>();
    }

    [Fact]
    public async Task TryClaim_with_different_fingerprint_returns_mismatch()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        var result = await _store.TryClaimAsync("key-1", "fingerprint-2", TimeSpan.FromMinutes(5));

        var mismatch = result.Should().BeOfType<ClaimResult.FingerprintMismatch>().Subject;
        mismatch.ExpectedFingerprint.Should().Be("fingerprint-1");
        mismatch.ActualFingerprint.Should().Be("fingerprint-2");
    }

    [Fact]
    public async Task TryClaim_expired_claim_allows_new_claim()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMilliseconds(1));

        await Task.Delay(10);

        var result = await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        result.Should().BeOfType<ClaimResult.Claimed>();
    }

    [Fact]
    public async Task SetResponse_stores_response_for_replay()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        var response = CreateResponse(200, """{"id": "pay_123"}""");
        await _store.SetResponseAsync("key-1", response, TimeSpan.FromHours(24));

        var retrieved = await _store.GetResponseAsync("key-1");

        retrieved.Should().NotBeNull();
        retrieved!.StatusCode.Should().Be(200);
        Encoding.UTF8.GetString(retrieved.Body).Should().Be("""{"id": "pay_123"}""");
    }

    [Fact]
    public async Task GetResponse_returns_null_for_unknown_key()
    {
        var result = await _store.GetResponseAsync("unknown-key");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetResponse_returns_null_for_claimed_but_not_completed_key()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        var result = await _store.GetResponseAsync("key-1");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseClaim_allows_new_claim()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));
        await _store.ReleaseClaimAsync("key-1");

        var result = await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        result.Should().BeOfType<ClaimResult.Claimed>();
    }

    [Fact]
    public async Task HasKey_returns_true_for_claimed_key()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        _store.HasKey("key-1").Should().BeTrue();
    }

    [Fact]
    public async Task HasKey_returns_false_for_released_key()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));
        await _store.ReleaseClaimAsync("key-1");

        _store.HasKey("key-1").Should().BeFalse();
    }

    [Fact]
    public async Task GetState_returns_claimed_state()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));

        _store.GetState("key-1").Should().Be(IdempotencyState.Claimed);
    }

    [Fact]
    public async Task GetState_returns_completed_after_response_set()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));
        await _store.SetResponseAsync("key-1", CreateResponse(200, "ok"), TimeSpan.FromHours(24));

        _store.GetState("key-1").Should().Be(IdempotencyState.Completed);
    }

    [Fact]
    public async Task GetResponse_returns_null_for_expired_response()
    {
        await _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5));
        await _store.SetResponseAsync("key-1", CreateResponse(200, "ok"), TimeSpan.FromMilliseconds(1));

        await Task.Delay(10);

        var result = await _store.GetResponseAsync("key-1");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_claims_only_one_succeeds()
    {
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _store.TryClaimAsync("key-1", "fingerprint-1", TimeSpan.FromMinutes(5)));

        var results = await Task.WhenAll(tasks);

        results.OfType<ClaimResult.Claimed>().Should().HaveCount(1);
        results.OfType<ClaimResult.AlreadyClaimed>().Should().HaveCount(9);
    }

    private static IdempotentResponse CreateResponse(int statusCode, string body) =>
        new()
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"]
            },
            Body = Encoding.UTF8.GetBytes(body)
        };

    public void Dispose()
    {
        _store.Dispose();
        GC.SuppressFinalize(this);
    }
}
