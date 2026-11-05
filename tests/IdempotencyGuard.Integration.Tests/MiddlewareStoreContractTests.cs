using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using IdempotencyGuard.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyGuard.Integration.Tests;

/// <summary>
/// Guarantees that depend on how the middleware and a store interact, so they run
/// through the real pipeline against every store rather than against the store alone.
/// </summary>
public abstract class MiddlewareStoreContractTests : IAsyncLifetime
{
    // Long enough that the replacement claim is still live while a test makes its
    // assertions, including against containers running under emulation.
    private static readonly TimeSpan ClaimTtl = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PastClaimTtl = ClaimTtl + TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, int> _runs = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _entered = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _gates = new();

    private readonly TaskCompletionSource _firstRequestFinished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _abortHandlerRuns;
    private IHost? _host;
    private HttpClient? _client;

    protected abstract void ConfigureStore(IServiceCollection services);

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddIdempotencyGuard(options =>
                    {
                        options.ClaimTtl = ClaimTtl;
                        options.ConcurrentRequestPolicy = ConcurrentRequestPolicy.ReturnConflict;
                        options.Cleanup.Enabled = false;
                        options.AddPolicy("scoped", policy => policy.KeyScope = _ => "tenant");
                    });
                    ConfigureStore(services);
                    services.AddRouting();
                });
                webBuilder.Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        try
                        {
                            await next(context);
                        }
                        finally
                        {
                            if (context.Request.Path == "/abort")
                                _firstRequestFinished.TrySetResult();
                        }
                    });
                    app.UseRouting();
                    app.UseIdempotencyGuard();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/gated", GatedHandlerAsync);
                        endpoints.MapPost("/abort", AbortingHandlerAsync);
                        endpoints.MapPost("/scoped", CreatedHandlerAsync).RequireIdempotency(o => o.Policy = "scoped");
                    });
                });
            })
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        foreach (var gate in _gates.Values)
            gate.TrySetResult(true);

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task Client_abort_after_the_handler_ran_does_not_cause_a_second_execution()
    {
        var key = UniqueKey();

        try
        {
            await _client!.SendAsync(Post("/abort", key, "A"));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException)
        {
            // The client side of an aborted request fails; the server side is what matters here.
        }

        await _firstRequestFinished.Task.WaitAsync(WaitLimit);

        var retry = await _client!.SendAsync(Post("/abort", key, "A"));

        _abortHandlerRuns.Should().Be(1, "the side effect already happened on the aborted request");
        retry.Headers.Contains("X-Idempotent-Replayed").Should().BeTrue();
    }

    [Fact]
    public async Task Longest_allowed_key_works_with_a_scope_on_every_store()
    {
        var key = $"{UniqueKey()}{new string('k', 200)}"[..200];

        var first = await _client!.SendAsync(Post("/scoped", key, "A"));
        var retry = await _client!.SendAsync(Post("/scoped", key, "A"));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.Headers.Contains("X-Idempotent-Replayed").Should().BeTrue();
    }

    [Fact]
    public async Task Oversized_key_is_rejected_with_a_400_on_every_store()
    {
        var key = $"{UniqueKey()}{new string('k', 300)}";

        var response = await _client!.SendAsync(Post("/scoped", key, "A"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stale_holder_finishing_does_not_complete_the_replacement_claim()
    {
        var key = UniqueKey();
        var (holder, replacement) = await StartTakeoverAsync(key);

        Gate("A").TrySetResult(true);
        await holder;

        // B is still running, so a retry of B must see an in-flight request.
        var retry = await _client!.SendAsync(Post("/gated", key, "B"));

        retry.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "B has not finished, and A's response must not be served for B's payload");

        Gate("B").TrySetResult(true);
        await replacement;
    }

    [Fact]
    public async Task Stale_holder_finishing_last_does_not_replace_the_replacement_response()
    {
        var key = UniqueKey();
        var (holder, replacement) = await StartTakeoverAsync(key);

        Gate("B").TrySetResult(true);
        await replacement;

        Gate("A").TrySetResult(true);
        await holder;

        var retry = await _client!.SendAsync(Post("/gated", key, "B"));
        var body = await retry.Content.ReadAsStringAsync();

        body.Should().Contain("handled:B").And.NotContain("handled:A");
    }

    [Fact]
    public async Task Stale_holder_failing_does_not_release_the_replacement_claim()
    {
        var key = UniqueKey();
        var (holder, replacement) = await StartTakeoverAsync(key);

        Gate("A").TrySetResult(false); // A's handler throws, which releases "its" claim.
        await SwallowFailureAsync(holder);

        var retry = await _client!.SendAsync(Post("/gated", key, "B"));

        retry.StatusCode.Should().Be(HttpStatusCode.Conflict, "B still holds the claim");
        _runs["B"].Should().Be(1, "B's handler must not run concurrently with itself");

        Gate("B").TrySetResult(true);
        await replacement;
    }

    /// <summary>
    /// A claims the key and stalls past ClaimTtl, then B takes the key over with a
    /// different payload and also stalls. Both are left running behind their gates.
    /// </summary>
    private async Task<(Task<HttpResponseMessage> Holder, Task<HttpResponseMessage> Replacement)> StartTakeoverAsync(string key)
    {
        var holder = _client!.SendAsync(Post("/gated", key, "A"));
        await Entered("A").Task.WaitAsync(WaitLimit);

        await Task.Delay(PastClaimTtl);

        var replacement = _client!.SendAsync(Post("/gated", key, "B"));
        var first = await Task.WhenAny(Entered("B").Task, replacement).WaitAsync(WaitLimit);

        first.Should().Be(Entered("B").Task, "B should have taken over A's expired claim");

        return (holder, replacement);
    }

    private async Task GatedHandlerAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body);
        var who = document.RootElement.GetProperty("who").GetString()!;

        // Only the first run for a payload blocks, so a wrongly admitted duplicate
        // returns and shows up in the assertions rather than hanging the test.
        if (_runs.AddOrUpdate(who, 1, (_, runs) => runs + 1) == 1)
        {
            Entered(who).TrySetResult();

            if (!await Gate(who).Task)
                throw new InvalidOperationException($"handler for {who} failed");
        }

        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsync($$"""{"result":"handled:{{who}}"}""");
    }

    private static async Task CreatedHandlerAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsync("""{"created":true}""");
    }

    private async Task AbortingHandlerAsync(HttpContext context)
    {
        // The side effect commits, then the client hangs up before the response is stored.
        if (Interlocked.Increment(ref _abortHandlerRuns) == 1)
            context.Abort();

        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsync("""{"charged":true}""");
    }

    private TaskCompletionSource Entered(string who) =>
        _entered.GetOrAdd(who, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private TaskCompletionSource<bool> Gate(string who) =>
        _gates.GetOrAdd(who, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

    private static string UniqueKey() => $"mw-{Guid.NewGuid():N}";

    private static HttpRequestMessage Post(string path, string key, string who)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent($$"""{"who":"{{who}}"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
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
}
