using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore.Tests;

public sealed class OptionBehaviorTests : IAsyncLifetime
{
    private const string ReplayedHeader = "X-Idempotent-Replayed";

    private IHost? _host;
    private int _handlerRuns;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task Method_added_to_enforced_methods_is_guarded()
    {
        var client = await StartAsync(options => options.EnforcedMethods = ["DELETE"]);

        await client.SendAsync(Request(HttpMethod.Delete, "key-1"));
        var second = await client.SendAsync(Request(HttpMethod.Delete, "key-1"));

        second.Headers.Contains(ReplayedHeader).Should().BeTrue();
        _handlerRuns.Should().Be(1);
    }

    [Fact]
    public async Task Method_left_out_of_enforced_methods_skips_the_guard_even_when_marked()
    {
        var client = await StartAsync(options => options.EnforcedMethods = ["PUT"]);

        await client.SendAsync(Request(HttpMethod.Post, "key-1"));
        var second = await client.SendAsync(Request(HttpMethod.Post, "key-1"));

        second.Headers.Contains(ReplayedHeader).Should().BeFalse();
        _handlerRuns.Should().Be(2);
    }

    [Fact]
    public async Task Enforced_methods_ignore_case()
    {
        var client = await StartAsync(options => options.EnforcedMethods = ["post"]);

        await client.SendAsync(Request(HttpMethod.Post, "key-1"));
        var second = await client.SendAsync(Request(HttpMethod.Post, "key-1"));

        second.Headers.Contains(ReplayedHeader).Should().BeTrue();
    }

    [Theory]
    [InlineData("X-Keep")]
    [InlineData("x-keep")]
    public async Task Allow_list_replays_only_the_headers_it_names(string allowed)
    {
        var client = await StartAsync(options => options.HeaderAllowList = [allowed]);

        var replay = await SendTwiceAsync(client);

        replay.Headers.Contains("X-Keep").Should().BeTrue();
        replay.Headers.Contains("X-Drop").Should().BeFalse();
    }

    [Fact]
    public async Task Allow_list_overrides_the_deny_lists()
    {
        var client = await StartAsync(options =>
        {
            options.HeaderAllowList = ["X-Drop"];
            options.HeaderDenyList = ["X-Drop"];
        });

        var replay = await SendTwiceAsync(client);

        replay.Headers.Contains("X-Drop").Should().BeTrue();
        replay.Headers.Contains("X-Keep").Should().BeFalse();
    }

    [Theory]
    [InlineData("X-Drop")]
    [InlineData("x-drop")]
    public async Task Deny_list_keeps_a_header_out_of_the_replay(string denied)
    {
        var client = await StartAsync(options => options.HeaderDenyList = [denied]);

        var replay = await SendTwiceAsync(client);

        replay.Headers.Contains("X-Keep").Should().BeTrue();
        replay.Headers.Contains("X-Drop").Should().BeFalse();
    }

    [Fact]
    public async Task Built_in_deny_list_applies_alongside_a_custom_one()
    {
        var client = await StartAsync(options => options.HeaderDenyList = ["X-Drop"]);

        var replay = await SendTwiceAsync(client);

        replay.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Without_lists_every_header_but_the_built_in_ones_is_replayed()
    {
        var client = await StartAsync(_ => { });

        var replay = await SendTwiceAsync(client);

        replay.Headers.Contains("X-Keep").Should().BeTrue();
        replay.Headers.Contains("X-Drop").Should().BeTrue();
        replay.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Empty_allow_list_is_treated_as_not_set()
    {
        var client = await StartAsync(options => options.HeaderAllowList = []);

        var replay = await SendTwiceAsync(client);

        replay.Headers.Contains("X-Keep").Should().BeTrue();
    }

    [Fact]
    public async Task Sweep_passes_the_batch_size_and_stops_at_the_first_short_batch()
    {
        var store = new ScriptedPurgeStore(remainingBatchesThatFill: 2);

        await RunOneSweepAsync(store, expectedPurges: 3, cleanup =>
        {
            cleanup.BatchSize = 7;
            cleanup.MaxIterationsPerSweep = 10;
        });

        store.Purges.Should().Be(3);
        store.BatchSizes.Should().OnlyContain(size => size == 7);
    }

    [Fact]
    public async Task Sweep_stops_at_max_iterations_while_batches_are_still_full()
    {
        var store = new ScriptedPurgeStore(remainingBatchesThatFill: int.MaxValue);

        await RunOneSweepAsync(store, expectedPurges: 3, cleanup =>
        {
            cleanup.BatchSize = 5;
            cleanup.MaxIterationsPerSweep = 3;
        });

        store.Purges.Should().Be(3);
    }

    private static async Task RunOneSweepAsync(ScriptedPurgeStore store, int expectedPurges, Action<CleanupOptions> configure)
    {
        var provider = new ServiceCollection()
            .AddLogging()
            .Configure<IdempotencyOptions>(options =>
            {
                options.Cleanup.Interval = TimeSpan.FromHours(1);
                configure(options.Cleanup);
            })
            .BuildServiceProvider();

        using var service = new IdempotencyCleanupService(
            store,
            provider.GetRequiredService<IOptionsMonitor<IdempotencyOptions>>(),
            provider.GetRequiredService<ILogger<IdempotencyCleanupService>>());

        await service.StartAsync(CancellationToken.None);
        var finished = await Task.WhenAny(store.Reached(expectedPurges), Task.Delay(TimeSpan.FromSeconds(10)));
        finished.Should().Be(store.Reached(expectedPurges));

        // A sweep has no pause of its own, so this is long enough to catch one that overruns.
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);
    }

    private async Task<HttpResponseMessage> SendTwiceAsync(HttpClient client)
    {
        await client.SendAsync(Request(HttpMethod.Post, "key-1"));
        var replay = await client.SendAsync(Request(HttpMethod.Post, "key-1"));
        replay.Headers.Contains(ReplayedHeader).Should().BeTrue();
        return replay;
    }

    private async Task<HttpClient> StartAsync(Action<IdempotencyOptions> configure)
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddIdempotencyGuard(options =>
                    {
                        options.SuppressUnscopedKeysWarning = true;
                        options.Cleanup.Enabled = false;
                        configure(options);
                    });
                    services.AddIdempotencyGuardInMemoryStore();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseIdempotencyGuard();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapMethods("/orders", ["POST", "DELETE"], HandleAsync).RequireIdempotency());
                });
            })
            .StartAsync();

        return _host.GetTestClient();
    }

    private async Task HandleAsync(HttpContext context)
    {
        Interlocked.Increment(ref _handlerRuns);
        context.Response.Headers["X-Keep"] = "keep";
        context.Response.Headers["X-Drop"] = "drop";
        context.Response.Headers.SetCookie = "session=abc";
        await context.Response.WriteAsync("ok");
    }

    private static HttpRequestMessage Request(HttpMethod method, string key)
    {
        var request = new HttpRequestMessage(method, "/orders")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    /// <summary>
    /// Returns a full batch the given number of times, and a short one after that.
    /// </summary>
    private sealed class ScriptedPurgeStore(int remainingBatchesThatFill) : IIdempotencyStore, IPurgableIdempotencyStore
    {
        private readonly InMemoryIdempotencyStore _inner = new();
        private readonly List<int> _batchSizes = [];
        private readonly Dictionary<int, TaskCompletionSource> _targets = [];
        private int _remaining = remainingBatchesThatFill;

        public int Purges { get { lock (_batchSizes) { return _batchSizes.Count; } } }
        public IReadOnlyList<int> BatchSizes { get { lock (_batchSizes) { return [.. _batchSizes]; } } }

        public Task Reached(int count)
        {
            lock (_batchSizes)
            {
                if (!_targets.TryGetValue(count, out var target))
                {
                    target = new TaskCompletionSource();
                    _targets[count] = target;
                    if (_batchSizes.Count >= count)
                    {
                        target.SetResult();
                    }
                }

                return target.Task;
            }
        }

        public Task<int> PurgeExpiredAsync(int batchSize, CancellationToken ct = default)
        {
            lock (_batchSizes)
            {
                _batchSizes.Add(batchSize);
                if (_targets.TryGetValue(_batchSizes.Count, out var target))
                {
                    target.TrySetResult();
                }

                if (_remaining > 0)
                {
                    _remaining--;
                    return Task.FromResult(batchSize);
                }

                return Task.FromResult(batchSize - 1);
            }
        }

        public Task<ClaimResult> TryClaimAsync(string key, string requestFingerprint, TimeSpan claimTtl, CancellationToken ct = default) =>
            _inner.TryClaimAsync(key, requestFingerprint, claimTtl, ct);

        public Task<bool> SetResponseAsync(string key, string claimToken, IdempotentResponse response, TimeSpan responseTtl, CancellationToken ct = default) =>
            _inner.SetResponseAsync(key, claimToken, response, responseTtl, ct);

        public Task<bool> ReleaseClaimAsync(string key, string claimToken, CancellationToken ct = default) =>
            _inner.ReleaseClaimAsync(key, claimToken, ct);

        public Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default) =>
            _inner.GetResponseAsync(key, ct);
    }
}
