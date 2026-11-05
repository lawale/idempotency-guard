using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using IdempotencyGuard.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyGuard.AspNetCore.Tests;

public class PolicyAndScopeTests : IAsyncLifetime
{
    private const string ReplayedHeader = "X-Idempotent-Replayed";

    private IHost? _host;
    private int _handlerCallCount;
    private bool _unknownPolicyEndpoint;
    private readonly ConcurrentDictionary<string, int> _callsPerKey = new();

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
    public async Task Without_a_scope_callers_share_one_key_space()
    {
        var client = await StartAsync(_ => { });
        var key = NewKey();

        await client.SendAsync(Post("/default", key, caller: "alice"));
        var response = await client.SendAsync(Post("/default", key, caller: "bob"));

        response.Headers.Contains(ReplayedHeader).Should().BeTrue();
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Null_scope_is_rejected_without_running_the_handler()
    {
        var client = await StartAsync(options => options.KeyScope = _ => null);

        var response = await client.SendAsync(Post("/default", NewKey(), caller: "alice"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _handlerCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Request_without_a_key_does_not_need_a_scope()
    {
        var client = await StartAsync(options => options.KeyScope = _ => null);
        var request = new HttpRequestMessage(HttpMethod.Post, "/default")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Scope_and_key_cannot_combine_into_another_callers_store_key()
    {
        var client = await StartAsync(options => options.KeyScope = context => context.Request.Headers["X-Caller"].ToString());

        await client.SendAsync(Post("/default", "b", caller: "a:"));
        var response = await client.SendAsync(Post("/default", ":b", caller: "a"));

        response.Headers.Contains(ReplayedHeader).Should().BeFalse();
        _handlerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Caller_identifier_does_not_appear_in_the_store_key()
    {
        var client = await StartAsync(options => options.KeyScope = context => context.Request.Headers["X-Caller"].ToString());
        var store = _host!.Services.GetRequiredService<InMemoryIdempotencyStore>();
        var key = NewKey();

        await client.SendAsync(Post("/default", key, caller: "alice"));

        store.HasKey(key).Should().BeFalse("the key must be scoped");
        store.HasKey($"alice{key}").Should().BeFalse("the caller identifier must not reach the store");
        store.HasKey($"alice:{key}").Should().BeFalse("the caller identifier must not reach the store");
    }

    [Fact]
    public async Task Same_identifier_under_different_policies_stays_separate()
    {
        var client = await StartAsync(options =>
        {
            options.AddPolicy("user", p => p.KeyScope = context => context.Request.Headers["X-Caller"].ToString());
            options.AddPolicy("service", p => p.KeyScope = context => context.Request.Headers["X-Caller"].ToString());
        });
        var key = NewKey();

        await client.SendAsync(Post("/user", key, caller: "abc"));
        var response = await client.SendAsync(Post("/service", key, caller: "abc"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Contains(ReplayedHeader).Should().BeFalse();
    }

    [Fact]
    public async Task Policy_scope_takes_precedence_over_global_scope()
    {
        var client = await StartAsync(options =>
        {
            options.KeyScope = _ => null;
            options.AddPolicy("user", p => p.KeyScope = context => context.Request.Headers["X-Caller"].ToString());
        });

        var withPolicy = await client.SendAsync(Post("/user", NewKey(), caller: "alice"));
        var withoutPolicy = await client.SendAsync(Post("/default", NewKey(), caller: "alice"));

        withPolicy.StatusCode.Should().Be(HttpStatusCode.Created);
        withoutPolicy.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Server_errors_are_stored_and_replayed_by_default()
    {
        var client = await StartAsync(_ => { });
        var key = NewKey();

        await client.SendAsync(Post("/fails-once", key));
        var retry = await client.SendAsync(Post("/fails-once", key));

        retry.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        retry.Headers.Contains(ReplayedHeader).Should().BeTrue();
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ShouldStoreResponse_false_releases_the_claim_so_the_retry_runs_the_handler()
    {
        var client = await StartAsync(options => options.ShouldStoreResponse = status => status < 500);
        var key = NewKey();

        var first = await client.SendAsync(Post("/fails-once", key));
        var retry = await client.SendAsync(Post("/fails-once", key));

        first.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
        retry.Headers.Contains(ReplayedHeader).Should().BeFalse();
        _handlerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task ShouldStoreResponse_true_still_stores_the_response()
    {
        var client = await StartAsync(options => options.ShouldStoreResponse = status => status < 500);
        var key = NewKey();

        await client.SendAsync(Post("/default", key));
        var retry = await client.SendAsync(Post("/default", key));

        retry.Headers.Contains(ReplayedHeader).Should().BeTrue();
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Policy_ShouldStoreResponse_overrides_the_global_value_on_its_endpoints_only()
    {
        var client = await StartAsync(options =>
        {
            options.ShouldStoreResponse = status => status < 500;
            options.AddPolicy("cache-all", p => p.ShouldStoreResponse = _ => true);
        });

        var globalKey = NewKey();
        await client.SendAsync(Post("/fails-once", globalKey));
        var global = await client.SendAsync(Post("/fails-once", globalKey));

        var policyKey = NewKey();
        await client.SendAsync(Post("/fails-once-cache-all", policyKey));
        var policy = await client.SendAsync(Post("/fails-once-cache-all", policyKey));

        global.StatusCode.Should().Be(HttpStatusCode.Created);
        global.Headers.Contains(ReplayedHeader).Should().BeFalse();
        policy.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        policy.Headers.Contains(ReplayedHeader).Should().BeTrue();
    }

    [Fact]
    public async Task Action_attribute_keeps_the_policy_set_on_its_controller()
    {
        var client = await StartAsync(options =>
            options.AddPolicy("user", p => p.KeyScope = _ => null));

        // Metadata order mirrors MVC: controller first, then action. Rejection proves the
        // policy survived, because only the policy has a resolver that returns null.
        var response = await client.SendAsync(Post("/controller-and-action", NewKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void Merge_takes_each_setting_from_the_most_specific_attribute_that_sets_it()
    {
        var merged = IdempotentAttribute.Merge(
        [
            new IdempotentAttribute { ClaimTtlSeconds = 111, ResponseTtlSeconds = 1, Policy = "controller", FingerprintProperties = ["a"] },
            new IdempotentAttribute { ResponseTtlSeconds = 222, Required = false }
        ]);

        merged!.ClaimTtlSeconds.Should().Be(111);
        merged.ResponseTtlSeconds.Should().Be(222);
        merged.Policy.Should().Be("controller");
        merged.FingerprintProperties.Should().Equal("a");
        merged.Required.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_policy_name_fails_startup_and_names_the_endpoint()
    {
        _unknownPolicyEndpoint = true;

        var act = () => StartAsync(_ => { });

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("does-not-exist").And.Contain("/misspelled");
    }

    [Fact]
    public void Registering_a_policy_name_twice_throws()
    {
        var options = new IdempotencyOptions();
        options.AddPolicy("user", _ => { });

        var act = () => options.AddPolicy("user", _ => { });

        act.Should().Throw<InvalidOperationException>().WithMessage("*user*");
    }

    private async Task<HttpClient> StartAsync(Action<IdempotencyOptions> configure)
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
                        options.SuppressUnscopedKeysWarning = true;
                        configure(options);

                        // Every test host maps all the endpoints below, so a test only
                        // registers the policies whose behaviour it is exercising.
                        foreach (var name in new[] { "user", "service", "cache-all" })
                        {
                            if (!options.TryGetPolicy(name, out _))
                                options.AddPolicy(name, _ => { });
                        }
                    });
                    services.AddIdempotencyGuardInMemoryStore();
                    services.AddRouting();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseIdempotencyGuard();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/default", CreatedAsync);
                        endpoints.MapPost("/user", CreatedAsync).RequireIdempotency(o => o.Policy = "user");
                        endpoints.MapPost("/service", CreatedAsync).RequireIdempotency(o => o.Policy = "service");
                        endpoints.MapPost("/fails-once", FailsOnceAsync);
                        endpoints.MapPost("/fails-once-cache-all", FailsOnceAsync).RequireIdempotency(o => o.Policy = "cache-all");
                        endpoints.MapPost("/controller-and-action", CreatedAsync)
                            .WithMetadata(new IdempotentAttribute { Policy = "user" })
                            .WithMetadata(new IdempotentAttribute { ResponseTtlSeconds = 60 });

                        if (_unknownPolicyEndpoint)
                        {
                            endpoints.MapPost("/misspelled", CreatedAsync).RequireIdempotency(o => o.Policy = "does-not-exist");
                        }
                    });
                });
            })
            .StartAsync();

        return _host.GetTestClient();
    }

    private async Task CreatedAsync(HttpContext context)
    {
        Interlocked.Increment(ref _handlerCallCount);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsync("""{"status":"created"}""");
    }

    private async Task FailsOnceAsync(HttpContext context)
    {
        Interlocked.Increment(ref _handlerCallCount);

        // Fails the first call for each key, so tests that use several keys each see a failure.
        var key = context.Request.Headers["Idempotency-Key"].ToString();
        var call = _callsPerKey.AddOrUpdate(key, 1, (_, calls) => calls + 1);
        context.Response.StatusCode = call == 1
            ? StatusCodes.Status500InternalServerError
            : StatusCodes.Status201Created;
        await context.Response.WriteAsync("""{"status":"done"}""");
    }

    private static string NewKey() => $"policy-{Guid.NewGuid():N}";

    private static HttpRequestMessage Post(string path, string key, string? caller = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        if (caller is not null)
            request.Headers.Add("X-Caller", caller);
        return request;
    }
}
