using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyGuard.AspNetCore.Tests;

public sealed class OversizedResponseTests : IAsyncLifetime
{
    private const string Body = "0123456789";
    private const string OriginalStatusHeader = "X-Idempotent-Original-Status";
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
    public async Task Retry_gets_a_410_without_running_the_handler_again()
    {
        var client = await StartAsync(options => options.MaxResponseBodySize = 4);

        var first = await client.SendAsync(Post("key"));
        var retry = await client.SendAsync(Post("key"));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        (await first.Content.ReadAsStringAsync()).Should().Be(Body);
        retry.StatusCode.Should().Be(HttpStatusCode.Gone);
        retry.Headers.GetValues(OriginalStatusHeader).Should().Equal("201");
        retry.Headers.Contains(ReplayedHeader).Should().BeTrue();
        (await retry.Content.ReadAsStringAsync()).Should().Contain("201");
        _handlerRuns.Should().Be(1);
    }

    [Fact]
    public async Task RunHandlerAgain_lets_a_retry_run_the_handler()
    {
        var client = await StartAsync(options =>
        {
            options.MaxResponseBodySize = 4;
            options.OversizedResponse = OversizedResponseBehavior.RunHandlerAgain;
        });

        await client.SendAsync(Post("key"));
        var retry = await client.SendAsync(Post("key"));

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        _handlerRuns.Should().Be(2);
    }

    [Fact]
    public async Task Policy_can_rerun_when_the_global_behavior_rejects()
    {
        var client = await StartAsync(options =>
        {
            options.MaxResponseBodySize = 4;
            options.AddPolicy("rerun", policy => policy.OversizedResponse = OversizedResponseBehavior.RunHandlerAgain);
        }, endpoint => endpoint.RequireIdempotency(o => o.Policy = "rerun"));

        await client.SendAsync(Post("key"));
        (await client.SendAsync(Post("key"))).StatusCode.Should().Be(HttpStatusCode.Created);
        _handlerRuns.Should().Be(2);
    }

    [Fact]
    public async Task Policy_can_reject_retries_when_the_global_behavior_reruns()
    {
        var client = await StartAsync(options =>
        {
            options.MaxResponseBodySize = 4;
            options.OversizedResponse = OversizedResponseBehavior.RunHandlerAgain;
            options.AddPolicy("reject", policy => policy.OversizedResponse = OversizedResponseBehavior.RejectRetries);
        }, endpoint => endpoint.RequireIdempotency(o => o.Policy = "reject"));

        await client.SendAsync(Post("key"));
        (await client.SendAsync(Post("key"))).StatusCode.Should().Be(HttpStatusCode.Gone);
        _handlerRuns.Should().Be(1);
    }

    [Fact]
    public async Task Status_that_ShouldStoreResponse_refuses_still_reruns()
    {
        var client = await StartAsync(options =>
        {
            options.MaxResponseBodySize = 4;
            options.ShouldStoreResponse = status => status < 201;
        });

        await client.SendAsync(Post("key"));
        var retry = await client.SendAsync(Post("key"));

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        _handlerRuns.Should().Be(2);
    }

    [Fact]
    public async Task Response_exactly_at_the_limit_is_stored_and_replayed()
    {
        var client = await StartAsync(options => options.MaxResponseBodySize = Body.Length);

        await client.SendAsync(Post("key"));
        var retry = await client.SendAsync(Post("key"));

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        (await retry.Content.ReadAsStringAsync()).Should().Be(Body);
        retry.Headers.Contains(OriginalStatusHeader).Should().BeFalse();
    }

    [Fact]
    public async Task Waiter_gets_the_410_when_the_original_finishes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = await StartAsync(options => options.MaxResponseBodySize = 4, before: async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });

        var first = client.SendAsync(Post("key"));
        await entered.Task;
        var waiter = client.SendAsync(Post("key"));
        await Task.Delay(50);
        release.SetResult();
        await first;

        (await waiter).StatusCode.Should().Be(HttpStatusCode.Gone);
        _handlerRuns.Should().Be(1);
    }

    [Fact]
    public async Task Different_payload_still_gets_a_422()
    {
        var client = await StartAsync(options => options.MaxResponseBodySize = 4);

        await client.SendAsync(Post("key", """{"amount":1}"""));
        var other = await client.SendAsync(Post("key", """{"amount":2}"""));

        other.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Error_factory_sees_the_kind_and_status_but_its_output_is_never_stored()
    {
        IdempotencyProblem? seen = null;
        var store = new RecordingStore();
        var client = await StartAsync(options =>
        {
            options.MaxResponseBodySize = 4;
            options.ErrorResponseFactory = problem =>
            {
                seen = problem;
                return new { key = problem.IdempotencyKey };
            };
        }, store: store);

        await client.SendAsync(Post("secret-client-key"));
        var retry = await client.SendAsync(Post("secret-client-key"));

        seen!.Kind.Should().Be(IdempotencyErrorKind.ResponseNotReplayable);
        seen.StatusCode.Should().Be(410);
        seen.OriginalStatusCode.Should().Be(201);
        (await retry.Content.ReadAsStringAsync()).Should().Contain("secret-client-key");
        Encoding.UTF8.GetString(store.Stored!.Body.Span).Should().NotContain("secret-client-key");
    }

    [Fact]
    public async Task Handler_cannot_forge_a_marker()
    {
        var client = await StartAsync(_ => { }, handlerHeaders: new()
        {
            ["X-Idempotent-Not-Replayable"] = "true",
            [OriginalStatusHeader] = "500"
        });

        await client.SendAsync(Post("key"));
        var retry = await client.SendAsync(Post("key"));

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        (await retry.Content.ReadAsStringAsync()).Should().Be(Body);
        retry.Headers.Contains(OriginalStatusHeader).Should().BeFalse();
    }

    [Fact]
    public async Task Store_that_drops_headers_still_serves_the_stored_410()
    {
        var client = await StartAsync(
            options => options.MaxResponseBodySize = 4,
            store: new RecordingStore { DropHeaders = true });

        await client.SendAsync(Post("key"));
        var retry = await client.SendAsync(Post("key"));

        retry.StatusCode.Should().Be(HttpStatusCode.Gone);
        _handlerRuns.Should().Be(1);
    }

    private async Task<HttpClient> StartAsync(
        Action<IdempotencyOptions> configure,
        Action<IEndpointConventionBuilder>? endpoint = null,
        Func<Task>? before = null,
        IIdempotencyStore? store = null,
        Dictionary<string, string>? handlerHeaders = null)
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
                        configure(options);
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
                    app.UseEndpoints(endpoints =>
                    {
                        var route = endpoints.MapPost("/payments", async context =>
                        {
                            Interlocked.Increment(ref _handlerRuns);
                            if (before is not null)
                                await before();

                            foreach (var header in handlerHeaders ?? [])
                                context.Response.Headers[header.Key] = header.Value;

                            context.Response.StatusCode = StatusCodes.Status201Created;
                            await context.Response.WriteAsync(Body);
                        });
                        endpoint?.Invoke(route);
                    });
                });
            })
            .StartAsync();

        return _host.GetTestClient();
    }

    private static HttpRequestMessage Post(string key, string body = """{"amount":1}""")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private sealed class RecordingStore : IIdempotencyStore
    {
        private readonly InMemoryIdempotencyStore _inner = new();

        public bool DropHeaders { get; init; }
        public IdempotentResponse? Stored { get; private set; }

        public Task<ClaimResult> TryClaimAsync(string key, string requestFingerprint, TimeSpan claimTtl, CancellationToken ct = default) =>
            _inner.TryClaimAsync(key, requestFingerprint, claimTtl, ct);

        public Task<bool> SetResponseAsync(string key, string claimToken, IdempotentResponse response, TimeSpan responseTtl, CancellationToken ct = default)
        {
            Stored = response;
            return _inner.SetResponseAsync(key, claimToken, response, responseTtl, ct);
        }

        public Task<bool> ReleaseClaimAsync(string key, string claimToken, CancellationToken ct = default) =>
            _inner.ReleaseClaimAsync(key, claimToken, ct);

        public async Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default)
        {
            var response = await _inner.GetResponseAsync(key, ct);
            return response is null || !DropHeaders
                ? response
                : new IdempotentResponse { StatusCode = response.StatusCode, Headers = [], Body = response.Body };
        }
    }
}
