using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using IdempotencyGuard.AspNetCore;
using IdempotencyGuard.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IdempotencyGuard.AspNetCore.Tests;

public class MiddlewareEdgeCaseTests : IAsyncLifetime
{
    private const string ReplayedHeader = "X-Idempotent-Replayed";

    private IHost? _host;
    private int _handlerCallCount;

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
    public async Task Duplicate_json_properties_do_not_fail_the_request()
    {
        var client = await StartAsync(CreatedHandler);

        var response = await client.SendAsync(Post(NewKey(), """{"amount":1,"amount":2}"""));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Retry_after_response_ttl_runs_the_handler_again_instead_of_returning_conflict()
    {
        var client = await StartAsync(CreatedHandler, options => options.ResponseTtl = TimeSpan.FromMilliseconds(100));
        var key = NewKey();

        await client.SendAsync(Post(key));
        await Task.Delay(300);
        var retry = await client.SendAsync(Post(key));

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.Headers.Contains(ReplayedHeader).Should().BeFalse();
        _handlerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Waiter_takes_over_when_the_original_request_fails()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = await StartAsync(
            async context =>
            {
                if (Interlocked.Increment(ref _handlerCallCount) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                    throw new InvalidOperationException("downstream failure");
                }

                context.Response.StatusCode = StatusCodes.Status201Created;
                await context.Response.WriteAsync("""{"status":"created"}""");
            },
            options =>
            {
                options.ConcurrentRequestPolicy = ConcurrentRequestPolicy.WaitThenReplay;
                options.ConcurrentRequestTimeout = TimeSpan.FromSeconds(5);
            });
        var key = NewKey();

        var original = SwallowFailureAsync(client.SendAsync(Post(key)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var waiter = client.SendAsync(Post(key));
        await Task.Delay(250); // Let the waiter see AlreadyClaimed and begin polling.
        release.TrySetResult();
        await original;

        var response = await waiter;

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _handlerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Different_user_with_same_key_and_body_does_not_receive_the_first_users_response()
    {
        var client = await StartAsync(EchoCallerHandler, options => options.KeyScope = CallerScope);
        var key = NewKey();

        await client.SendAsync(Post(key, user: "alice"));
        var response = await client.SendAsync(Post(key, user: "bob"));

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("alice");
        response.Headers.Contains(ReplayedHeader).Should().BeFalse();
    }

    [Fact]
    public async Task Different_user_with_same_key_and_different_body_is_not_told_the_key_exists()
    {
        var client = await StartAsync(EchoCallerHandler, options => options.KeyScope = CallerScope);
        var key = NewKey();

        await client.SendAsync(Post(key, """{"amount":1}""", user: "alice"));
        var response = await client.SendAsync(Post(key, """{"amount":2}""", user: "bob"));

        response.StatusCode.Should().NotBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Same_user_retry_still_replays_when_keys_are_scoped_per_user()
    {
        var client = await StartAsync(EchoCallerHandler, options => options.KeyScope = CallerScope);
        var key = NewKey();

        await client.SendAsync(Post(key, user: "alice"));
        var retry = await client.SendAsync(Post(key, user: "alice"));

        retry.Headers.Contains(ReplayedHeader).Should().BeTrue();
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Raw_idempotency_key_is_not_written_to_logs_at_information_or_above()
    {
        var logs = new CapturingLoggerProvider();
        var client = await StartAsync(CreatedHandler, services: services =>
            services.AddSingleton<ILoggerProvider>(logs));
        var key = NewKey();

        await client.SendAsync(Post(key, """{"amount":1}""")); // claim + store
        await client.SendAsync(Post(key, """{"amount":1}""")); // replay
        await client.SendAsync(Post(key, """{"amount":2}""")); // mismatch

        logs.Entries
            .Where(entry => entry.Level >= LogLevel.Information)
            .Select(entry => entry.Message)
            .Should().NotContain(message => message.Contains(key));
    }

    [Fact]
    public async Task Raw_idempotency_key_is_not_recorded_on_spans()
    {
        var key = NewKey();
        var leakedTags = new ConcurrentBag<string>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == IdempotencyDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                foreach (var tag in activity.Tags)
                {
                    if (tag.Value?.Contains(key) == true)
                        leakedTags.Add($"{activity.OperationName}: {tag.Key}");
                }
            }
        };
        ActivitySource.AddActivityListener(listener);

        var client = await StartAsync(CreatedHandler);

        await client.SendAsync(Post(key));
        await client.SendAsync(Post(key));

        leakedTags.Should().BeEmpty();
    }

    [Fact]
    public async Task Key_at_the_maximum_length_is_accepted()
    {
        var client = await StartAsync(CreatedHandler);

        var response = await client.SendAsync(Post(new string('k', 200)));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Key_over_the_maximum_length_is_rejected_without_running_the_handler_or_echoing_the_key()
    {
        object? problemKey = "not called";
        var client = await StartAsync(CreatedHandler, options => options.ErrorResponseFactory = problem =>
        {
            problemKey = problem.IdempotencyKey;
            return new { kind = problem.Kind.ToString() };
        });

        var response = await client.SendAsync(Post(new string('k', 201)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(nameof(IdempotencyErrorKind.InvalidKey));
        problemKey.Should().BeNull();
        _handlerCallCount.Should().Be(0);
    }

    [Fact]
    public async Task MaxKeyLength_can_be_configured()
    {
        var client = await StartAsync(CreatedHandler, options => options.MaxKeyLength = 8);

        (await client.SendAsync(Post(new string('k', 8)))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.SendAsync(Post(new string('k', 9)))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MaxKeyLength_of_zero_disables_the_check()
    {
        var client = await StartAsync(CreatedHandler, options => options.MaxKeyLength = 0);

        var response = await client.SendAsync(Post(new string('k', 500)));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Key_with_a_control_character_is_rejected()
    {
        var client = await StartAsync(CreatedHandler);
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "abc\tdef").Should().BeTrue();

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _handlerCallCount.Should().Be(0);
    }

    private static string? CallerScope(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier);

    private async Task CreatedHandler(HttpContext context)
    {
        Interlocked.Increment(ref _handlerCallCount);
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"status":"created"}""");
    }

    private async Task EchoCallerHandler(HttpContext context)
    {
        Interlocked.Increment(ref _handlerCallCount);
        var caller = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($$"""{"account":"private data for {{caller}}"}""");
    }

    private async Task<HttpClient> StartAsync(
        RequestDelegate handler,
        Action<IdempotencyOptions>? configure = null,
        Action<IServiceCollection>? services = null)
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(collection =>
                {
                    collection.AddIdempotencyGuard(options =>
                    {
                        options.Cleanup.Enabled = false;
                        configure?.Invoke(options);
                    });
                    collection.AddIdempotencyGuardInMemoryStore();
                    collection.AddRouting();
                    services?.Invoke(collection);
                });
                webBuilder.Configure(app =>
                {
                    // Stands in for authentication middleware, which runs before the guard.
                    app.Use((context, next) =>
                    {
                        var user = context.Request.Headers["X-Test-User"].ToString();
                        if (user.Length > 0)
                        {
                            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                                [new Claim(ClaimTypes.NameIdentifier, user)], "Test"));
                        }

                        return next(context);
                    });
                    app.UseRouting();
                    app.UseIdempotencyGuard();
                    app.UseEndpoints(endpoints => endpoints.MapPost("/payments", handler));
                });
            })
            .StartAsync();

        return _host.GetTestClient();
    }

    private static string NewKey() => $"edge-{Guid.NewGuid():N}";

    private static HttpRequestMessage Post(string key, string body = """{"amount":1}""", string? user = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        if (user is not null)
            request.Headers.Add("X-Test-User", user);
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

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
