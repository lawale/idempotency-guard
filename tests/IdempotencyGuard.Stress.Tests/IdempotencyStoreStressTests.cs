using System.Text;
using FluentAssertions;
using IdempotencyGuard.Stress.Tests.Support;
using Xunit.Abstractions;

namespace IdempotencyGuard.Stress.Tests;

/// <summary>
/// Load-level guarantees every <see cref="IIdempotencyStore"/> implementation
/// must hold. The contract tests prove the state machine is right; these prove
/// it stays right when hundreds of callers race for the same key, which is the
/// shape of a real retry storm.
/// </summary>
public abstract class IdempotencyStoreStressTests
{
    private static readonly TimeSpan LongClaimTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResponseTtl = TimeSpan.FromHours(1);

    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    protected IdempotencyStoreStressTests(ITestOutputHelper output)
    {
        Output = output;
    }

    protected abstract IIdempotencyStore Store { get; }

    protected ITestOutputHelper Output { get; }

    private string Key(string scenario, int index = 0) => $"stress-{_runId}-{scenario}-{index}";

    [Fact]
    public async Task Thundering_herd_on_one_key_yields_exactly_one_claim()
    {
        var herdSize = StressProfile.HerdSize;
        var rounds = StressProfile.HerdRounds;
        var claimedTotal = 0;

        for (var round = 0; round < rounds; round++)
        {
            var key = Key("herd", round);

            var run = await LoadHarness.RunAsync(
                workers: herdSize,
                iterationsPerWorker: 1,
                operation: _ => Store.TryClaimAsync(key, "fp-herd", LongClaimTtl));

            var claimed = run.Results.OfType<ClaimResult.Claimed>().Count();
            claimed.Should().Be(1, "exactly one caller may win the claim for key {0}", key);

            run.Results.OfType<ClaimResult.AlreadyClaimed>().Should().HaveCount(herdSize - 1);
            run.Results.OfType<ClaimResult.FingerprintMismatch>().Should().BeEmpty();

            claimedTotal += claimed;

            if (round == 0)
            {
                Output.WriteLine(run.Summary($"herd claim x{herdSize}"));
            }
        }

        claimedTotal.Should().Be(rounds);
    }

    [Fact]
    public async Task Sustained_load_across_many_keys_claims_each_key_exactly_once()
    {
        var keyCount = StressProfile.KeyCount;
        var workers = StressProfile.Workers;
        var total = keyCount * StressProfile.DuplicatesPerKey;
        var iterationsPerWorker = (total + workers - 1) / workers;

        // Interleave duplicates of different keys so the store sees a realistic
        // mix rather than a burst per key.
        var plan = new string[workers * iterationsPerWorker];
        for (var i = 0; i < plan.Length; i++)
        {
            plan[i] = Key("mixed", i % keyCount);
        }

        Shuffle(plan, seed: 20260910);

        var run = await LoadHarness.RunAsync(
            workers,
            iterationsPerWorker,
            async index =>
            {
                var key = plan[index];
                var result = await Store.TryClaimAsync(key, "fp-mixed", LongClaimTtl);
                return (Key: key, Result: result);
            });

        Output.WriteLine(run.Summary($"mixed claim ({keyCount} keys)"));

        var claimsPerKey = run.Results
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Count(r => r.Result is ClaimResult.Claimed));

        claimsPerKey.Should().HaveCount(keyCount);
        claimsPerKey.Values.Should().AllSatisfy(count =>
            count.Should().Be(1, "every key must be claimed by exactly one caller"));

        run.Results.Should().NotContain(x => x.Result is ClaimResult.FingerprintMismatch,
            "all callers sent the same fingerprint");
    }

    [Fact]
    public async Task Claim_and_release_cycles_never_overlap()
    {
        var key = Key("mutex");
        var workers = StressProfile.Workers;
        var iterations = 25 * StressProfile.Scale;

        var holders = 0;
        var maxObservedHolders = 0;
        var completedCycles = 0;

        var run = await LoadHarness.RunAsync(
            workers,
            iterations,
            async _ =>
            {
                var result = await Store.TryClaimAsync(key, "fp-mutex", LongClaimTtl);

                if (result is not ClaimResult.Claimed claimed)
                {
                    return false;
                }

                var current = Interlocked.Increment(ref holders);
                InterlockedMax(ref maxObservedHolders, current);

                // Hold the claim briefly so overlapping holders would be observable.
                await Task.Yield();

                Interlocked.Decrement(ref holders);
                Interlocked.Increment(ref completedCycles);

                await Store.ReleaseClaimAsync(key, claimed.ClaimToken);
                return true;
            });

        Output.WriteLine(run.Summary("claim/release cycle"));
        Output.WriteLine($"completed cycles: {completedCycles}, max concurrent holders: {maxObservedHolders}");

        maxObservedHolders.Should().Be(1,
            "the store must never hand the same key to two holders at once");
        completedCycles.Should().BeGreaterThan(0, "at least some claims must succeed");
    }

    [Fact]
    public async Task Racing_fingerprints_agree_on_a_single_winner()
    {
        var key = Key("fingerprint-race");
        var herdSize = StressProfile.HerdSize;

        var run = await LoadHarness.RunAsync(
            workers: herdSize,
            iterationsPerWorker: 1,
            operation: index => Store.TryClaimAsync(key, $"fp-{index % 4}", LongClaimTtl));

        Output.WriteLine(run.Summary("racing fingerprints"));

        run.Results.OfType<ClaimResult.Claimed>().Should().HaveCount(1);

        var winner = await Store.TryClaimAsync(key, "fp-probe", LongClaimTtl);
        var expected = winner.Should().BeOfType<ClaimResult.FingerprintMismatch>().Subject.ExpectedFingerprint;

        // Every loser must have been told about the same winning fingerprint —
        // no caller may see a half-written entry.
        run.Results
            .OfType<ClaimResult.FingerprintMismatch>()
            .Should().AllSatisfy(m => m.ExpectedFingerprint.Should().Be(expected));
    }

    [Fact]
    public async Task Completed_response_replays_byte_identical_under_read_storm()
    {
        var key = Key("replay");
        var body = $$"""{"id":"pay_{{_runId}}","amount":1250,"currency":"USD"}""";

        var claimToken = ((ClaimResult.Claimed)await Store.TryClaimAsync(key, "fp-replay", LongClaimTtl)).ClaimToken;
        await Store.SetResponseAsync(key, claimToken, CreateResponse(201, body), ResponseTtl);

        var run = await LoadHarness.RunAsync(
            StressProfile.Workers,
            iterationsPerWorker: 50 * StressProfile.Scale,
            operation: async _ =>
            {
                var response = await Store.GetResponseAsync(key);
                return response is null
                    ? null
                    : $"{response.StatusCode}:{Encoding.UTF8.GetString(response.Body.Span)}";
            });

        Output.WriteLine(run.Summary("replay read"));

        run.Results.Should().AllBe($"201:{body}",
            "a stored response must never be observed torn, empty or partially written");
    }

    [Fact]
    public async Task Expired_claim_is_reclaimed_by_exactly_one_caller()
    {
        var rounds = Math.Min(5, StressProfile.HerdRounds);
        var herdSize = StressProfile.HerdSize;

        for (var round = 0; round < rounds; round++)
        {
            var key = Key("expiry", round);

            var first = await Store.TryClaimAsync(key, "fp-expiry", TimeSpan.FromMilliseconds(250));
            first.Should().BeOfType<ClaimResult.Claimed>();

            // Let the claim lapse the way an orphaned in-flight request would.
            await Task.Delay(600);

            var run = await LoadHarness.RunAsync(
                workers: herdSize,
                iterationsPerWorker: 1,
                operation: _ => Store.TryClaimAsync(key, "fp-expiry", LongClaimTtl));

            run.Results.OfType<ClaimResult.Claimed>().Should().HaveCount(1,
                "exactly one caller may take over an expired claim");

            if (round == 0)
            {
                Output.WriteLine(run.Summary($"expired-claim takeover x{herdSize}"));
            }
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private static void Shuffle<T>(T[] items, int seed)
    {
        var random = new Random(seed);
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
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
}
