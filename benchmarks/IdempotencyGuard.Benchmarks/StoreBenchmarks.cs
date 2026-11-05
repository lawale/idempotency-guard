using System.Text;
using BenchmarkDotNet.Attributes;
using IdempotencyGuard;

namespace IdempotencyGuard.Benchmarks;

/// <summary>
/// Per-operation cost of the in-memory store. Redis, Postgres and SQL Server
/// add a network round trip on top of this; these numbers isolate the library's
/// own bookkeeping from the store's I/O.
/// </summary>
[MemoryDiagnoser]
public class StoreBenchmarks
{
    private static readonly TimeSpan ClaimTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResponseTtl = TimeSpan.FromHours(1);

    private InMemoryIdempotencyStore _store = null!;
    private IdempotentResponse _response = null!;
    private int _counter;

    private const string CompletedKey = "bench-completed";

    [GlobalSetup]
    public async Task Setup()
    {
        _store = new InMemoryIdempotencyStore();
        _response = new IdempotentResponse
        {
            StatusCode = 201,
            Headers = new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
            Body = Encoding.UTF8.GetBytes("""{"id":"pay_123","status":"created"}""")
        };

        var claimToken = ((ClaimResult.Claimed)await _store.TryClaimAsync(CompletedKey, "fp", ClaimTtl)).ClaimToken;
        await _store.SetResponseAsync(CompletedKey, claimToken, _response, ResponseTtl);
    }

    /// <summary>
    /// The write arms add an entry per invocation. Starting the store over every
    /// <see cref="ResetEvery"/> writes keeps the measurement on the operation
    /// rather than on an ever-growing hash table, without the per-invocation
    /// isolation that BenchmarkDotNet's IterationCleanup would force.
    /// </summary>
    private const int ResetEvery = 50_000;

    private int NextWrite()
    {
        var n = Interlocked.Increment(ref _counter);

        if (n % ResetEvery == 0)
        {
            _store.Dispose();
            Setup().GetAwaiter().GetResult();
        }

        return n;
    }

    [Benchmark(Description = "TryClaim (new key)")]
    public async Task<ClaimResult> ClaimNewKey() =>
        await _store.TryClaimAsync($"bench-{NextWrite()}", "fp", ClaimTtl);

    [Benchmark(Description = "TryClaim (already completed)")]
    public async Task<ClaimResult> ClaimCompletedKey() =>
        await _store.TryClaimAsync(CompletedKey, "fp", ClaimTtl);

    [Benchmark(Description = "GetResponse (replay)")]
    public async Task<IdempotentResponse?> GetResponse() =>
        await _store.GetResponseAsync(CompletedKey);

    [Benchmark(Description = "TryClaim + SetResponse (full cycle)")]
    public async Task ClaimAndComplete()
    {
        var key = $"bench-cycle-{NextWrite()}";
        var claimToken = ((ClaimResult.Claimed)await _store.TryClaimAsync(key, "fp", ClaimTtl)).ClaimToken;
        await _store.SetResponseAsync(key, claimToken, _response, ResponseTtl);
    }
}
