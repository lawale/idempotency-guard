using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using FluentAssertions;
using IdempotencyGuard.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyGuard.AspNetCore.Tests;

// The meter is static, so tests running alongside these would add to their counts.
[CollectionDefinition(Name, DisableParallelization = true)]
public class TelemetryCollection
{
    public const string Name = "Telemetry";
}

[Collection(TelemetryCollection.Name)]
public sealed class TelemetryTests : IAsyncLifetime, IDisposable
{
    private const string Replays = "idempotency.replays.total";
    private const string Conflicts = "idempotency.conflicts.total";
    private const string Released = "idempotency.claims.released";
    private const string StoreLatency = "idempotency.store.latency";

    private readonly MeterListener _meters = new();
    private readonly ActivityListener _spans;
    private readonly ConcurrentQueue<Measurement> _measurements = new();
    private readonly ConcurrentQueue<string> _results = new();
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IHost? _host;

    public TelemetryTests()
    {
        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == IdempotencyMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _meters.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
        _meters.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
        _meters.Start();

        _spans = new ActivityListener
        {
            ShouldListenTo = source => source.Name == IdempotencyDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "idempotency.request")
                    _results.Enqueue(activity.GetTagItem("idempotency.result") as string ?? "");
            }
        };
        ActivitySource.AddActivityListener(_spans);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _release.TrySetResult();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    public void Dispose()
    {
        _meters.Dispose();
        _spans.Dispose();
    }

    [Fact]
    public async Task Replay_counts_as_a_replay_and_not_a_conflict()
    {
        var client = await StartAsync(CreatedAsync);
        var key = NewKey();

        await client.SendAsync(Post(key));
        var retry = await client.SendAsync(Post(key));

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        Count(Replays).Should().Be(1);
        Count(Conflicts).Should().Be(0);
        await ResultsShouldBeAsync("claimed", "replayed");
    }

    [Fact]
    public async Task Waiter_that_ends_in_a_replay_is_not_a_conflict()
    {
        var client = await StartAsync(BlockedAsync);
        var key = NewKey();

        var first = client.SendAsync(Post(key));
        await _entered.Task;
        var waiter = client.SendAsync(Post(key));
        await WaiterIsPollingAsync();
        _release.SetResult();
        await first;

        (await waiter).StatusCode.Should().Be(HttpStatusCode.Created);
        Count(Conflicts).Should().Be(0);
        Count(Replays).Should().Be(1);
        await ResultsShouldBeAsync("claimed", "replayed");
    }

    [Fact]
    public async Task Waiter_that_takes_over_is_not_a_conflict()
    {
        var calls = 0;
        var client = await StartAsync(async context =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                _entered.SetResult();
                await _release.Task;
                throw new InvalidOperationException("downstream failure");
            }

            await CreatedAsync(context);
        });
        var key = NewKey();

        var first = SwallowFailureAsync(client.SendAsync(Post(key)));
        await _entered.Task;
        var waiter = client.SendAsync(Post(key));
        await WaiterIsPollingAsync();
        _release.SetResult();
        await first;

        (await waiter).StatusCode.Should().Be(HttpStatusCode.Created);
        Count(Conflicts).Should().Be(0);

        // The first request's span has no result, because its handler threw.
        await ResultsShouldBeAsync("", "claimed");
    }

    [Fact]
    public async Task Waiter_that_times_out_counts_as_a_conflict()
    {
        var client = await StartAsync(BlockedAsync, options =>
            options.ConcurrentRequestTimeout = TimeSpan.FromMilliseconds(150));
        var key = NewKey();

        var first = client.SendAsync(Post(key));
        await _entered.Task;
        var waiter = await client.SendAsync(Post(key));

        waiter.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Count(Conflicts).Should().Be(1);
        _release.SetResult();
        await first;
    }

    [Fact]
    public async Task ReturnConflict_counts_as_a_conflict()
    {
        var client = await StartAsync(BlockedAsync, options =>
            options.ConcurrentRequestPolicy = ConcurrentRequestPolicy.ReturnConflict);
        var key = NewKey();

        var first = client.SendAsync(Post(key));
        await _entered.Task;
        var second = await client.SendAsync(Post(key));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Count(Conflicts).Should().Be(1);
        _release.SetResult();
        await first;
        await ResultsShouldBeAsync("conflict", "claimed");
    }

    [Fact]
    public async Task Completed_entry_with_no_response_counts_as_a_conflict_and_not_a_replay()
    {
        var store = new StubStore
        {
            Claim = new ClaimResult.Completed(new IdempotencyEntry { Key = "k", RequestFingerprint = "fp" })
        };
        var client = await StartAsync(CreatedAsync, store: store);

        var response = await client.SendAsync(Post(NewKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Count(Replays).Should().Be(0);
        Count(Conflicts).Should().Be(1);
        await ResultsShouldBeAsync("conflict");
    }

    [Fact]
    public async Task Failed_handler_releases_with_reason_failure()
    {
        var client = await StartAsync(_ => throw new InvalidOperationException("downstream failure"));

        await SwallowFailureAsync(client.SendAsync(Post(NewKey())));

        ReleaseReasons().Should().Equal("failure");
    }

    [Fact]
    public async Task Oversized_response_that_may_rerun_releases_with_reason_response_too_large()
    {
        var client = await StartAsync(CreatedAsync, options =>
        {
            options.MaxResponseBodySize = 1;
            options.OversizedResponse = OversizedResponseBehavior.RunHandlerAgain;
        });

        var response = await client.SendAsync(Post(NewKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        ReleaseReasons().Should().Equal("response_too_large");
        OversizedBehaviors().Should().Equal("run_handler_again");
    }

    [Fact]
    public async Task Rejected_retry_counts_separately_from_replays_and_conflicts()
    {
        var client = await StartAsync(CreatedAsync, options => options.MaxResponseBodySize = 1);
        var key = NewKey();

        await client.SendAsync(Post(key));
        var retry = await client.SendAsync(Post(key));

        retry.StatusCode.Should().Be(HttpStatusCode.Gone);
        OversizedBehaviors().Should().Equal("reject_retries");
        Count("idempotency.replays.rejected").Should().Be(1);
        Count(Replays).Should().Be(0);
        Count(Conflicts).Should().Be(0);
        Count(Released).Should().Be(0);
        await ResultsShouldBeAsync("claimed", "not_replayable");
    }

    [Fact]
    public async Task Response_refused_by_ShouldStoreResponse_releases_with_its_own_reason()
    {
        var client = await StartAsync(CreatedAsync, options => options.ShouldStoreResponse = _ => false);

        await client.SendAsync(Post(NewKey()));

        ReleaseReasons().Should().Equal("should_store_response");
    }

    [Fact]
    public async Task Release_of_a_claim_no_longer_held_is_not_counted()
    {
        var store = new StubStore { Claim = new ClaimResult.Claimed("token"), ReleaseSucceeds = false };
        var client = await StartAsync(CreatedAsync, options => options.ShouldStoreResponse = _ => false, store);

        await client.SendAsync(Post(NewKey()));

        Count(Released).Should().Be(0);
    }

    private async Task<HttpClient> StartAsync(
        RequestDelegate handler,
        Action<IdempotencyOptions>? configure = null,
        IIdempotencyStore? store = null)
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddIdempotencyGuard(options =>
                    {
                        options.Cleanup.Enabled = false;
                        configure?.Invoke(options);
                    });
                    services.AddIdempotencyGuardInMemoryStore();
                    if (store is not null)
                        services.AddSingleton(store);
                    services.AddRouting();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseIdempotencyGuard();
                    app.UseEndpoints(endpoints => endpoints.MapPost("/payments", handler));
                });
            })
            .StartAsync();

        return _host.GetTestClient();
    }

    private void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _measurements.Enqueue(new Measurement(instrument.Name, tags.ToArray()));

    private int Count(string name) => _measurements.Count(m => m.Name == name);

    private string[] ReleaseReasons() => _measurements
        .Where(m => m.Name == Released)
        .Select(m => m.Tags.Single(tag => tag.Key == "reason").Value as string ?? "")
        .ToArray();

    private string[] OversizedBehaviors() => _measurements
        .Where(m => m.Name == "idempotency.responses.oversized")
        .Select(m => m.Tags.Single(tag => tag.Key == "behavior").Value as string ?? "")
        .ToArray();

    // The first request's claim and the waiter's first attempt are both recorded by then.
    private Task WaiterIsPollingAsync() => WaitUntilAsync(() =>
        _measurements.Count(m => m.Name == StoreLatency
            && m.Tags.Any(tag => tag.Key == "operation" && "claim".Equals(tag.Value))) >= 2);

    private async Task ResultsShouldBeAsync(params string[] expected)
    {
        // The span stops after the response has reached the client.
        await WaitUntilAsync(() => _results.Count >= expected.Length);
        _results.Should().Equal(expected);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        condition().Should().BeTrue();
    }

    private async Task BlockedAsync(HttpContext context)
    {
        _entered.TrySetResult();
        await _release.Task;
        await CreatedAsync(context);
    }

    private static async Task CreatedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsync("created");
    }

    private static async Task SwallowFailureAsync(Task<HttpResponseMessage> request)
    {
        try
        {
            await request;
        }
        catch (InvalidOperationException)
        {
            // TestServer surfaces the handler's exception to the client.
        }
    }

    private static string NewKey() => $"telemetry-{Guid.NewGuid():N}";

    private static HttpRequestMessage Post(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = new StringContent("""{"amount":1}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private sealed record Measurement(string Name, KeyValuePair<string, object?>[] Tags);

    private sealed class StubStore : IIdempotencyStore
    {
        public required ClaimResult Claim { get; init; }
        public bool ReleaseSucceeds { get; init; } = true;

        public Task<ClaimResult> TryClaimAsync(string key, string requestFingerprint, TimeSpan claimTtl, CancellationToken ct = default) =>
            Task.FromResult(Claim);

        public Task<bool> SetResponseAsync(string key, string claimToken, IdempotentResponse response, TimeSpan responseTtl, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> ReleaseClaimAsync(string key, string claimToken, CancellationToken ct = default) =>
            Task.FromResult(ReleaseSucceeds);

        public Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<IdempotentResponse?>(null);
    }
}
