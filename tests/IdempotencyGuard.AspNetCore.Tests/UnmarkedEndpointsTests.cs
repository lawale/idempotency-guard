using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyGuard.AspNetCore.Tests;

public sealed class UnmarkedEndpointsTests : IAsyncLifetime
{
    private const string ReplayedHeader = "X-Idempotent-Replayed";

    private readonly CountingStore _store = new();
    private IHost? _host;
    private WebApplication? _webApplication;
    private int _handlerRuns;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        if (_webApplication is not null)
        {
            await _webApplication.DisposeAsync();
        }
    }

    [Fact]
    public async Task Unmarked_endpoint_ignores_the_key_and_stores_nothing()
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.Ignore);

        var first = await client.SendAsync(Post("/unmarked", "key-1"));
        var second = await client.SendAsync(Post("/unmarked", "key-1"));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Headers.Contains(ReplayedHeader).Should().BeFalse();
        _handlerRuns.Should().Be(2);
        _store.Claims.Should().Be(0);
    }

    [Theory]
    [InlineData("/marked")]
    [InlineData("/marked-with-attribute")]
    public async Task Marked_endpoint_is_still_guarded(string path)
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.Ignore);

        await client.SendAsync(Post(path, "key-1"));
        var second = await client.SendAsync(Post(path, "key-1"));

        second.Headers.Contains(ReplayedHeader).Should().BeTrue();
        _handlerRuns.Should().Be(1);
    }

    [Fact]
    public async Task Unmatched_route_is_not_stored()
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.Ignore);

        await client.SendAsync(Post("/does-not-exist", "key-1"));
        var second = await client.SendAsync(Post("/does-not-exist", "key-1"));

        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
        second.Headers.Contains(ReplayedHeader).Should().BeFalse();
        _store.Claims.Should().Be(0);
    }

    [Fact]
    public async Task Key_reused_across_unmarked_endpoints_is_not_a_mismatch()
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.Ignore);

        await client.SendAsync(Post("/unmarked", "key-1"));
        var other = await client.SendAsync(Post("/unmarked-2", "key-1"));

        other.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Invalid_key_on_an_unmarked_endpoint_is_not_rejected()
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.Ignore, configure: options => options.MaxKeyLength = 3);

        var response = await client.SendAsync(Post("/unmarked", "too-long"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_key_on_an_unmarked_endpoint_passes_through_when_ignored()
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.Ignore);

        var response = await client.SendAsync(Post("/unmarked", key: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(UnmarkedEndpointBehavior.GuardWhenKeyPresent, HttpStatusCode.OK)]
    [InlineData(UnmarkedEndpointBehavior.RequireKey, HttpStatusCode.BadRequest)]
    public async Task Behavior_decides_for_an_unmarked_endpoint_without_a_key(UnmarkedEndpointBehavior behavior, HttpStatusCode expected)
    {
        var client = await StartAsync(behavior);

        var response = await client.SendAsync(Post("/unmarked", key: null));

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task Marked_endpoint_can_make_the_key_optional_under_require_key()
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.RequireKey);

        var response = await client.SendAsync(Post("/marked-key-optional", key: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(UnmarkedEndpointBehavior.GuardWhenKeyPresent)]
    [InlineData(UnmarkedEndpointBehavior.RequireKey)]
    public async Task Unmarked_endpoint_with_a_key_is_guarded_unless_ignored(UnmarkedEndpointBehavior behavior)
    {
        var client = await StartAsync(behavior);

        await client.SendAsync(Post("/unmarked", "key-1"));
        var second = await client.SendAsync(Post("/unmarked", "key-1"));

        second.Headers.Contains(ReplayedHeader).Should().BeTrue();
        _handlerRuns.Should().Be(1);
    }

    [Fact]
    public async Task Require_key_rejects_an_unmatched_route_without_a_key()
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.RequireKey);

        var response = await client.SendAsync(Post("/does-not-exist", key: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/marked", HttpStatusCode.BadRequest)]
    [InlineData("/marked-key-optional", HttpStatusCode.OK)]
    public async Task Required_decides_for_a_marked_endpoint_without_a_key(string path, HttpStatusCode expected)
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.Ignore);

        var response = await client.SendAsync(Post(path, key: null));

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task Endpoint_filter_can_still_exclude_a_marked_endpoint()
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.Ignore, configure: options => options.EndpointFilter = (_, _) => false);

        await client.SendAsync(Post("/marked", "key-1"));
        await client.SendAsync(Post("/marked", "key-1"));

        _handlerRuns.Should().Be(2);
        _store.Claims.Should().Be(0);
    }

    [Theory]
    [InlineData("/unmarked", false)]
    [InlineData("/marked", true)]
    public async Task Authorization_order_check_only_covers_guarded_endpoints(string path, bool throws)
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.Ignore, authorizeAllAfterGuard: true);

        var send = () => client.SendAsync(Post(path, "key-1"));

        if (throws)
        {
            (await send.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*UseAuthorization*");
        }
        else
        {
            (await send()).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void Guarding_when_a_key_is_present_is_the_default()
    {
        new IdempotencyOptions().UnmarkedEndpoints.Should().Be(UnmarkedEndpointBehavior.GuardWhenKeyPresent);
    }

    [Fact]
    public async Task Routing_after_the_guard_fails_startup()
    {
        var start = () => StartAsync(UnmarkedEndpointBehavior.Ignore, routingAfterGuard: true);

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*UseRouting*");
    }

    [Fact]
    public async Task Routing_after_the_guard_is_allowed_unless_ignoring()
    {
        var start = () => StartAsync(UnmarkedEndpointBehavior.GuardWhenKeyPresent, routingAfterGuard: true);

        await start.Should().NotThrowAsync();
    }

#pragma warning disable IDEMP001 // These cover the deprecated settings until 3.0 removes them
    [Theory]
    [InlineData(MissingKeyPolicy.Allow, UnmarkedEndpointBehavior.GuardWhenKeyPresent)]
    [InlineData(MissingKeyPolicy.Reject, UnmarkedEndpointBehavior.RequireKey)]
    public void Missing_key_policy_maps_to_the_new_setting(MissingKeyPolicy policy, UnmarkedEndpointBehavior expected)
    {
        var options = new IdempotencyOptions { MissingKeyPolicy = policy };

        options.UnmarkedEndpoints.Should().Be(expected);
        options.MissingKeyPolicy.Should().Be(policy);
    }

    [Theory]
    [InlineData(UnmarkedEndpointBehavior.GuardWhenKeyPresent, MissingKeyPolicy.Allow)]
    [InlineData(UnmarkedEndpointBehavior.RequireKey, MissingKeyPolicy.Reject)]
    [InlineData(UnmarkedEndpointBehavior.Ignore, MissingKeyPolicy.Allow)]
    public void Missing_key_policy_reads_back_from_the_new_setting(UnmarkedEndpointBehavior behavior, MissingKeyPolicy expected)
    {
        new IdempotencyOptions { UnmarkedEndpoints = behavior }.MissingKeyPolicy.Should().Be(expected);
    }

    [Fact]
    public async Task Missing_key_policy_reject_still_rejects()
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.GuardWhenKeyPresent, setBehavior: false,
            configure: options => options.MissingKeyPolicy = MissingKeyPolicy.Reject);

        var response = await client.SendAsync(Post("/unmarked", key: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(MissingKeyPolicy.Reject, UnmarkedEndpointBehavior.GuardWhenKeyPresent)]
    [InlineData(MissingKeyPolicy.Reject, UnmarkedEndpointBehavior.Ignore)]
    [InlineData(MissingKeyPolicy.Allow, UnmarkedEndpointBehavior.RequireKey)]
    [InlineData(MissingKeyPolicy.Allow, UnmarkedEndpointBehavior.Ignore)]
    public async Task Settings_that_disagree_fail_startup(MissingKeyPolicy policy, UnmarkedEndpointBehavior behavior)
    {
        var start = () => StartAsync(behavior, configure: options => options.MissingKeyPolicy = policy);

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*MissingKeyPolicy*UnmarkedEndpoints*");
    }

    [Theory]
    [InlineData(MissingKeyPolicy.Allow, UnmarkedEndpointBehavior.GuardWhenKeyPresent)]
    [InlineData(MissingKeyPolicy.Reject, UnmarkedEndpointBehavior.RequireKey)]
    public async Task Settings_that_agree_start_normally(MissingKeyPolicy policy, UnmarkedEndpointBehavior behavior)
    {
        var start = () => StartAsync(behavior, configure: options => options.MissingKeyPolicy = policy);

        await start.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Enabled_false_still_turns_the_guard_off()
    {
        var client = await StartAsync(UnmarkedEndpointBehavior.GuardWhenKeyPresent, configure: options => options.Enabled = false);

        await client.SendAsync(Post("/marked", "key-1"));
        await client.SendAsync(Post("/marked", "key-1"));

        _handlerRuns.Should().Be(2);
        _store.Claims.Should().Be(0);
    }
#pragma warning restore IDEMP001

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebApplication_guards_marked_endpoints_with_implicit_or_explicit_routing(bool explicitRouting)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIdempotencyGuard(ConfigureIgnore);
        builder.Services.AddSingleton<IIdempotencyStore>(_store);

        _webApplication = builder.Build();
        if (explicitRouting)
        {
            _webApplication.UseRouting();
        }

        _webApplication.UseIdempotencyGuard();
        MapEndpoints(_webApplication);
        await _webApplication.StartAsync();
        var client = _webApplication.GetTestClient();

        await client.SendAsync(Post("/marked", "key-1"));
        var replay = await client.SendAsync(Post("/marked", "key-1"));
        await client.SendAsync(Post("/unmarked", "key-1"));
        var unmarked = await client.SendAsync(Post("/unmarked", "key-1"));

        replay.Headers.Contains(ReplayedHeader).Should().BeTrue();
        unmarked.Headers.Contains(ReplayedHeader).Should().BeFalse();
    }

    [Fact]
    public async Task WebApplication_with_routing_after_the_guard_fails_startup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddIdempotencyGuard(ConfigureIgnore);
        builder.Services.AddSingleton<IIdempotencyStore>(_store);

        _webApplication = builder.Build();
        _webApplication.UseIdempotencyGuard();
        _webApplication.UseRouting();
        MapEndpoints(_webApplication);

        var start = () => _webApplication.StartAsync();

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*UseRouting*");
    }

    private static void ConfigureIgnore(IdempotencyOptions options)
    {
        options.UnmarkedEndpoints = UnmarkedEndpointBehavior.Ignore;
        options.SuppressUnscopedKeysWarning = true;
    }

    private async Task<HttpClient> StartAsync(
        UnmarkedEndpointBehavior behavior,
        Action<IdempotencyOptions>? configure = null,
        bool routingAfterGuard = false,
        bool setBehavior = true,
        bool authorizeAllAfterGuard = false)
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddIdempotencyGuard(options =>
                    {
                        if (setBehavior)
                        {
                            options.UnmarkedEndpoints = behavior;
                        }

                        options.SuppressUnscopedKeysWarning = true;
                        configure?.Invoke(options);
                    });
                    services.AddSingleton<IIdempotencyStore>(_store);
                });
                webBuilder.Configure(app =>
                {
                    if (routingAfterGuard)
                    {
                        app.UseIdempotencyGuard();
                        app.UseRouting();
                    }
                    else
                    {
                        app.UseRouting();
                        app.UseIdempotencyGuard();
                    }

                    if (authorizeAllAfterGuard)
                    {
                        app.UseAuthorization();
                    }

                    app.UseEndpoints(endpoints =>
                    {
                        MapEndpoints(endpoints, authorizeAllAfterGuard);
                    });
                });
            })
            .StartAsync();

        return _host.GetTestClient();
    }

    private void MapEndpoints(IEndpointRouteBuilder endpoints, bool requireAuthorization = false)
    {
        var routes = new[]
        {
            endpoints.MapPost("/marked", HandleAsync).RequireIdempotency(),
            endpoints.MapPost("/marked-with-attribute", HandleAsync).WithMetadata(new IdempotentAttribute()),
            endpoints.MapPost("/marked-key-optional", HandleAsync).RequireIdempotency(o => o.Required = false),
            endpoints.MapPost("/unmarked", HandleAsync),
            endpoints.MapPost("/unmarked-2", HandleAsync)
        };

        if (requireAuthorization)
        {
            var allowAll = new AuthorizationPolicyBuilder().RequireAssertion(_ => true).Build();
            foreach (var route in routes)
            {
                route.RequireAuthorization(allowAll);
            }
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        Interlocked.Increment(ref _handlerRuns);
        await context.Response.WriteAsync("ok");
    }

    private static HttpRequestMessage Post(string path, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return request;
    }

    private sealed class CountingStore : IIdempotencyStore
    {
        private readonly InMemoryIdempotencyStore _inner = new();
        private int _claims;

        public int Claims => _claims;

        public Task<ClaimResult> TryClaimAsync(string key, string requestFingerprint, TimeSpan claimTtl, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _claims);
            return _inner.TryClaimAsync(key, requestFingerprint, claimTtl, ct);
        }

        public Task<bool> SetResponseAsync(string key, string claimToken, IdempotentResponse response, TimeSpan responseTtl, CancellationToken ct = default) =>
            _inner.SetResponseAsync(key, claimToken, response, responseTtl, ct);

        public Task<bool> ReleaseClaimAsync(string key, string claimToken, CancellationToken ct = default) =>
            _inner.ReleaseClaimAsync(key, claimToken, ct);

        public Task<IdempotentResponse?> GetResponseAsync(string key, CancellationToken ct = default) =>
            _inner.GetResponseAsync(key, ct);
    }
}
