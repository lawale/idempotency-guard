using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using IdempotencyGuard.AspNetCore;
using IdempotencyGuard.Stress.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace IdempotencyGuard.Stress.Tests;

/// <summary>
/// End-to-end load tests through the real ASP.NET Core pipeline. The store-level
/// tests prove the primitive is safe; these prove the middleware built on it
/// never runs a payment handler twice, whatever the retry pattern.
/// </summary>
public abstract class MiddlewareStressTests
{
    private const string PaymentBody = """{"amount":1250,"currency":"USD"}""";

    // Long enough that a herd arrives while the first request is still in flight.
    // With an instant handler nearly every duplicate lands after completion and
    // gets a plain replay, leaving the wait and conflict paths barely exercised.
    private static readonly TimeSpan HandlerLatency = TimeSpan.FromMilliseconds(50);

    protected MiddlewareStressTests(ITestOutputHelper output)
    {
        Output = output;
    }

    protected ITestOutputHelper Output { get; }

    protected abstract void ConfigureStore(IServiceCollection services);

    /// <summary>Label used in the printed throughput lines.</summary>
    protected abstract string StoreName { get; }

    [Fact]
    public async Task Retry_storm_on_one_key_executes_the_handler_once()
    {
        await using var app = await StartAsync(options => options.ConcurrentRequestPolicy = ConcurrentRequestPolicy.WaitThenReplay);

        var key = NewKey();
        var herdSize = StressProfile.HerdSize;

        var run = await LoadHarness.RunAsync(
            workers: herdSize,
            iterationsPerWorker: 1,
            operation: _ => app.PostPaymentAsync(key, PaymentBody));

        Output.WriteLine(run.Summary($"{StoreName} retry storm x{herdSize}"));

        app.HandlerCallCount.Should().Be(1, "the payment handler must run exactly once per idempotency key");

        run.Results.Count(r => r.Replayed).Should().Be(herdSize - 1,
            "every caller except the one that executed must be served the stored response");

        run.Results.Should().AllSatisfy(response =>
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            response.Body.Should().Be(app.ExpectedBodyFor(key));
        });
    }

    [Fact]
    public async Task Retry_storm_with_conflict_policy_returns_one_success_and_no_server_errors()
    {
        await using var app = await StartAsync(options => options.ConcurrentRequestPolicy = ConcurrentRequestPolicy.ReturnConflict);

        var key = NewKey();
        var herdSize = StressProfile.HerdSize;

        var run = await LoadHarness.RunAsync(
            workers: herdSize,
            iterationsPerWorker: 1,
            operation: _ => app.PostPaymentAsync(key, PaymentBody));

        Output.WriteLine(run.Summary($"{StoreName} conflict-policy storm x{herdSize}"));

        app.HandlerCallCount.Should().Be(1);

        run.Results.Should().AllSatisfy(response =>
            ((int)response.StatusCode).Should().BeLessThan(500, "callers must never see a server error under contention"));

        // ReturnConflict only governs the in-flight window. A duplicate that arrives
        // after the first request has finished is a replay, not a conflict — so the
        // invariant is that exactly one caller executed, and everyone else was either
        // turned away with 409 or served the stored response.
        run.Results.Count(r => r.StatusCode == HttpStatusCode.Created && !r.Replayed).Should().Be(1);

        run.Results.Should().AllSatisfy(r => ((int)r.StatusCode).Should().BeOneOf([201, 409],
            "a duplicate is either turned away as a conflict or served the stored response"));

        // AllSatisfy fails on an empty collection, and whether any duplicate lands
        // after completion depends on store latency — so iterate instead.
        foreach (var replay in run.Results.Where(r => r.Replayed))
        {
            replay.Body.Should().Be(app.ExpectedBodyFor(key), "a replay must return the original response body");
        }

        var conflicts = run.Results.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        var replays = run.Results.Count(r => r.Replayed);
        (conflicts + replays + 1).Should().Be(herdSize, "every caller must be accounted for");
        conflicts.Should().BeGreaterThan(0, "the herd must overlap the in-flight request for this test to mean anything");

        Output.WriteLine($"{StoreName} conflict-policy storm: 1 executed, {conflicts} conflicts, {replays} replays");
    }

    [Fact]
    public async Task Sustained_load_across_many_keys_executes_each_handler_once()
    {
        await using var app = await StartAsync();

        var keyCount = StressProfile.KeyCount;
        var workers = StressProfile.Workers;
        var runId = Guid.NewGuid().ToString("N")[..8];

        var total = keyCount * StressProfile.DuplicatesPerKey;
        var iterationsPerWorker = (total + workers - 1) / workers;

        var plan = new string[workers * iterationsPerWorker];
        for (var i = 0; i < plan.Length; i++)
        {
            plan[i] = $"{runId}-key-{i % keyCount}";
        }

        Shuffle(plan, seed: 20260910);

        var run = await LoadHarness.RunAsync(
            workers,
            iterationsPerWorker,
            index => app.PostPaymentAsync(plan[index], PaymentBody));

        Output.WriteLine(run.Summary($"{StoreName} mixed load ({keyCount} keys)"));

        app.HandlerCallCount.Should().Be(keyCount,
            "each distinct idempotency key must trigger exactly one handler execution");

        app.CallsByKey.Values.Should().AllSatisfy(count => count.Should().Be(1));

        run.Results.Should().AllSatisfy(response =>
            response.StatusCode.Should().Be(HttpStatusCode.Created));
    }

    [Fact]
    public async Task Replay_storm_after_completion_serves_every_caller_from_the_store()
    {
        await using var app = await StartAsync();

        var key = NewKey();

        var first = await app.PostPaymentAsync(key, PaymentBody);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var run = await LoadHarness.RunAsync(
            StressProfile.Workers,
            iterationsPerWorker: 25 * StressProfile.Scale,
            operation: _ => app.PostPaymentAsync(key, PaymentBody));

        Output.WriteLine(run.Summary($"{StoreName} replay storm"));

        app.HandlerCallCount.Should().Be(1);

        run.Results.Should().AllSatisfy(response =>
        {
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            response.Body.Should().Be(app.ExpectedBodyFor(key));
            response.Replayed.Should().BeTrue("every response after the first must be marked as a replay");
        });
    }

    [Fact]
    public async Task Failed_handlers_release_their_claim_so_the_retry_still_succeeds()
    {
        await using var app = await StartAsync(configureHost: null, failFirstAttempt: true);

        var keyCount = 50 * StressProfile.Scale;
        var runId = Guid.NewGuid().ToString("N")[..8];

        // Every key fails once, then succeeds. If a failed request leaked its claim,
        // the retry would hang on the claim or be rejected instead of being served.
        var firstPass = await LoadHarness.RunAsync(
            workers: Math.Min(keyCount, StressProfile.Workers),
            iterationsPerWorker: Math.Max(1, keyCount / Math.Min(keyCount, StressProfile.Workers)),
            operation: index => app.PostPaymentAsync($"{runId}-fail-{index}", PaymentBody));

        firstPass.Results.Should().AllSatisfy(r =>
            ((int)r.StatusCode).Should().Be(500, "the handler was configured to fail on first attempt"));

        var retry = await LoadHarness.RunAsync(
            workers: Math.Min(keyCount, StressProfile.Workers),
            iterationsPerWorker: Math.Max(1, keyCount / Math.Min(keyCount, StressProfile.Workers)),
            operation: index => app.PostPaymentAsync($"{runId}-fail-{index}", PaymentBody));

        Output.WriteLine(retry.Summary($"{StoreName} retry after failure"));

        retry.Results.Should().AllSatisfy(r =>
            r.StatusCode.Should().Be(HttpStatusCode.Created, "a released claim must be retryable"));
    }

    [Fact]
    public async Task Mismatched_payloads_under_load_are_rejected_without_running_the_handler_twice()
    {
        await using var app = await StartAsync();

        var key = NewKey();
        var herdSize = StressProfile.HerdSize;

        // Half the herd sends the original payload, half sends a tampered amount.
        var run = await LoadHarness.RunAsync(
            workers: herdSize,
            iterationsPerWorker: 1,
            operation: index => app.PostPaymentAsync(
                key,
                index % 2 == 0 ? PaymentBody : """{"amount":999999,"currency":"USD"}"""));

        Output.WriteLine(run.Summary($"{StoreName} payload-mismatch storm"));

        app.HandlerCallCount.Should().Be(1);

        var rejected = run.Results.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        rejected.Should().BeGreaterThan(0, "callers reusing a key with a different payload must be rejected");

        run.Results.Should().AllSatisfy(r =>
            ((int)r.StatusCode).Should().BeOneOf(201, 422));
    }

    private static string NewKey() => $"stress-{Guid.NewGuid():N}";

    private async Task<StressApp> StartAsync(
        Action<IdempotencyOptions>? configureHost = null,
        bool failFirstAttempt = false)
    {
        var app = new StressApp(failFirstAttempt);
        await app.StartAsync(ConfigureStore, configureHost);
        return app;
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

    protected sealed record StressResponse(HttpStatusCode StatusCode, string Body, bool Replayed);

    /// <summary>
    /// A minimal payments API wired through the real middleware, with a handler
    /// that records exactly how many times it ran for each key.
    /// </summary>
    protected sealed class StressApp : IAsyncDisposable
    {
        private readonly bool _failFirstAttempt;
        private readonly ConcurrentDictionary<string, int> _attempts = new();
        private IHost? _host;
        private HttpClient? _client;
        private int _handlerCallCount;

        public StressApp(bool failFirstAttempt)
        {
            _failFirstAttempt = failFirstAttempt;
        }

        public int HandlerCallCount => Volatile.Read(ref _handlerCallCount);

        public ConcurrentDictionary<string, int> CallsByKey { get; } = new();

        public string ExpectedBodyFor(string key) => $$"""{"id":"pay_{{key}}"}""";

        public async Task StartAsync(
            Action<IServiceCollection> configureStore,
            Action<IdempotencyOptions>? configureOptions)
        {
            _host = await new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
                        services.AddIdempotencyGuard(options =>
                        {
                            options.UnmarkedEndpoints = UnmarkedEndpointBehavior.RequireKey;
                            options.ClaimTtl = TimeSpan.FromMinutes(5);
                            options.ResponseTtl = TimeSpan.FromMinutes(30);
                            options.Cleanup.Enabled = false;
                            configureOptions?.Invoke(options);
                        });
                        configureStore(services);
                        services.AddRouting();
                    });
                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseIdempotencyGuard();
                        app.UseEndpoints(endpoints => endpoints.MapPost("/payments", HandlePaymentAsync));
                    });
                })
                .StartAsync();

            _client = _host.GetTestClient();
        }

        private async Task HandlePaymentAsync(HttpContext context)
        {
            var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? "";

            if (_failFirstAttempt && _attempts.AddOrUpdate(key, 1, (_, existing) => existing + 1) == 1)
            {
                throw new InvalidOperationException($"Simulated downstream failure for {key}");
            }

            Interlocked.Increment(ref _handlerCallCount);
            CallsByKey.AddOrUpdate(key, 1, (_, existing) => existing + 1);

            await Task.Delay(HandlerLatency);

            context.Response.StatusCode = 201;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync($$"""{"id":"pay_{{key}}"}""");
        }

        public async Task<StressResponse> PostPaymentAsync(string idempotencyKey, string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

            HttpResponseMessage response;
            try
            {
                response = await _client!.SendAsync(request);
            }
            catch (Exception)
            {
                // TestServer rethrows an unhandled handler exception at the call
                // site instead of turning it into a 500 response.
                return new StressResponse(HttpStatusCode.InternalServerError, "", false);
            }

            using (response)
            {
                var content = await response.Content.ReadAsStringAsync();
                var replayed = response.Headers.TryGetValues("X-Idempotent-Replayed", out var values)
                               && values.Contains("true", StringComparer.OrdinalIgnoreCase);

                return new StressResponse(response.StatusCode, content, replayed);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();

            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }
    }
}
