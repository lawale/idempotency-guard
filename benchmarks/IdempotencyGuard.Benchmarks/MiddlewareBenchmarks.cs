using System.Text;
using BenchmarkDotNet.Attributes;
using IdempotencyGuard.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IdempotencyGuard.Benchmarks;

/// <summary>
/// End-to-end cost of a request through the ASP.NET Core pipeline, with and
/// without the guard. The gap between <c>No guard</c> and the guarded arms is
/// the per-request overhead the library actually adds.
///
/// Each arm gets its own host and its own store so the new-key arm's writes
/// cannot disturb the replay arm's cached entry.
/// </summary>
[MemoryDiagnoser]
public class MiddlewareBenchmarks
{
    private const string RequestBody = """{"amount":1250,"currency":"USD","reference":"inv_00042"}""";
    private const string ReplayKey = "bench-replay-key";

    private readonly List<IHost> _hosts = [];

    private HttpClient _unguardedClient = null!;
    private HttpClient _replayClient = null!;
    private HttpClient _newKeyClient = null!;
    private int _counter;

    [GlobalSetup]
    public async Task Setup()
    {
        _unguardedClient = await StartClientAsync(store: null);
        _replayClient = await StartClientAsync(new InMemoryIdempotencyStore());
        _newKeyClient = await StartClientAsync(new SelfResettingStore(maxEntries: 50_000));

        // Prime the replay key so that arm always takes the cached path.
        await PostAsync(_replayClient, ReplayKey);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _unguardedClient.Dispose();
        _replayClient.Dispose();
        _newKeyClient.Dispose();

        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    [Benchmark(Baseline = true, Description = "No guard")]
    public Task<int> Unguarded() => PostAsync(_unguardedClient, idempotencyKey: null);

    [Benchmark(Description = "Guard, new key (claim + store)")]
    public Task<int> GuardedNewKey() => PostAsync(_newKeyClient, $"bench-{Interlocked.Increment(ref _counter)}");

    [Benchmark(Description = "Guard, replayed response")]
    public Task<int> GuardedReplay() => PostAsync(_replayClient, ReplayKey);

    private static async Task<int> PostAsync(HttpClient client, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = new StringContent(RequestBody, Encoding.UTF8, "application/json")
        };

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        using var response = await client.SendAsync(request);
        return (int)response.StatusCode;
    }

    private async Task<HttpClient> StartClientAsync(IIdempotencyStore? store)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
                    services.AddRouting();

                    if (store is not null)
                    {
                        services.AddIdempotencyGuard(options =>
                        {
                            options.UnmarkedEndpoints = UnmarkedEndpointBehavior.RequireKey;
                            options.Cleanup.Enabled = false;
                        });
                        services.AddSingleton(store);
                    }
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();

                    if (store is not null)
                    {
                        app.UseIdempotencyGuard();
                    }

                    app.UseEndpoints(endpoints => endpoints.MapPost("/payments", async context =>
                    {
                        context.Response.StatusCode = 201;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync("""{"id":"pay_123","status":"created"}""");
                    }));
                });
            })
            .StartAsync();

        _hosts.Add(host);
        return host.GetTestClient();
    }

    /// <summary>
    /// In-memory store that starts over once it has accumulated
    /// <paramref name="maxEntries"/> claims. The new-key arm writes a fresh
    /// entry on every invocation; without this the store would grow for the
    /// whole run and the benchmark would measure a swelling hash table.
    /// </summary>
    private sealed class SelfResettingStore(int maxEntries) : IIdempotencyStore
    {
        private InMemoryIdempotencyStore _inner = new();
        private int _writes;

        public Task<ClaimResult> TryClaimAsync(string key, string requestFingerprint, TimeSpan claimTtl, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _writes) >= maxEntries)
            {
                Interlocked.Exchange(ref _writes, 0);
                Interlocked.Exchange(ref _inner, new InMemoryIdempotencyStore()).Dispose();
            }

            return Volatile.Read(ref _inner).TryClaimAsync(key, requestFingerprint, claimTtl, ct);
        }

        public Task<bool> SetResponseAsync(string key, string claimToken, IdempotentResponse response, TimeSpan responseTtl, CancellationToken ct = default) =>
            Volatile.Read(ref _inner).SetResponseAsync(key, claimToken, response, responseTtl, ct);

        public Task<bool> ReleaseClaimAsync(string key, string claimToken, CancellationToken ct = default) =>
            Volatile.Read(ref _inner).ReleaseClaimAsync(key, claimToken, ct);

        public Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default) =>
            Volatile.Read(ref _inner).GetResponseAsync(key, ct);
    }
}
