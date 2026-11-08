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
    private IIdempotencyContext? _captured;
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
        store.HasKey(IdempotencyStoreKey.For(key, scope: "alice")).Should().BeTrue();
    }

    [Fact]
    public async Task Store_key_is_the_readable_prefix_followed_by_a_digest()
    {
        var client = await StartAsync(options => options.KeyPrefix = "prod:");
        var store = _host!.Services.GetRequiredService<InMemoryIdempotencyStore>();
        var key = NewKey();

        await client.SendAsync(Post("/default", key));

        store.HasKey($"prod:{key}").Should().BeFalse("the client's key must not reach the store");
        store.HasKey(IdempotencyStoreKey.For(key, prefix: "prod:")).Should().BeTrue();
    }

    [Fact]
    public async Task Policy_scoped_store_key_matches_the_public_helper()
    {
        var client = await StartAsync(options =>
            options.AddPolicy("user", p => p.KeyScope = context => context.Request.Headers["X-Caller"].ToString()));
        var store = _host!.Services.GetRequiredService<InMemoryIdempotencyStore>();
        var key = NewKey();

        await client.SendAsync(Post("/user", key, caller: "alice"));

        store.HasKey(IdempotencyStoreKey.For(key, scope: "alice", policyName: "user")).Should().BeTrue();
    }

    [Fact]
    public async Task Downstream_key_matches_the_value_1x_produced()
    {
        // A literal, computed outside this code. If this changes, a retry spanning the upgrade
        // sends the payment provider a new key, and the provider won't catch the duplicate.
        var client = await StartAsync(options => options.KeyPrefix = "prod:");

        await client.SendAsync(Post("/capture", "abc-123"));

        _captured!.Key.Should().Be("prod:abc-123");
        _captured.DownstreamKey("stripe", "charge").Should().Be("4ae858a26667dab19753b0bf7ddd5d34dcc57db229bf9dc2bcbf690c7acf1c19");
    }

    [Fact]
    public async Task Downstream_keys_differ_between_scoped_callers_using_the_same_key()
    {
        var client = await StartAsync(options => options.KeyScope = context => context.Request.Headers["X-Caller"].ToString());

        await client.SendAsync(Post("/capture", "abc-123", caller: "alice"));
        var alice = _captured!.DownstreamKey("stripe", "charge");

        await client.SendAsync(Post("/capture", "abc-123", caller: "bob"));
        var bob = _captured!.DownstreamKey("stripe", "charge");

        alice.Should().NotBe(bob);
    }

    [Fact]
    public async Task KeyPrefix_at_the_limit_is_accepted()
    {
        var client = await StartAsync(options => options.KeyPrefix = new string('p', IdempotencyStoreKey.MaxPrefixLength));

        var response = await client.SendAsync(Post("/default", NewKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task KeyPrefix_over_the_limit_fails_startup()
    {
        var act = () => StartAsync(options => options.KeyPrefix = new string('p', IdempotencyStoreKey.MaxPrefixLength + 1));

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("KeyPrefix");
    }

    [Fact]
    public async Task Per_request_prefix_over_the_limit_fails_the_request()
    {
        var client = await StartAsync(_ => { });
        var request = Post("/default", NewKey());
        request.Headers.Add("X-Prefix", new string('p', IdempotencyStoreKey.MaxPrefixLength + 1));

        var act = () => client.SendAsync(request);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("prefix");
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

    [Theory]
    [InlineData("/required-false-then-unset", HttpStatusCode.Created)]
    [InlineData("/required-false-then-true", HttpStatusCode.BadRequest)]
    [InlineData("/unset-then-required-false", HttpStatusCode.Created)]
    [InlineData("/unset-then-unset", HttpStatusCode.BadRequest)]
    public async Task Required_comes_from_the_most_specific_attribute_that_sets_it(string path, HttpStatusCode expected)
    {
        var client = await StartAsync(_ => { });
        var request = Post(path, NewKey());
        request.Headers.Remove("Idempotency-Key");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public void Required_defaults_to_true_when_no_attribute_sets_it()
    {
        new IdempotentAttribute().Required.Should().BeTrue();
        IdempotentAttribute.Merge([new IdempotentAttribute(), new IdempotentAttribute()])!.Required.Should().BeTrue();
    }

    [Fact]
    public void Required_set_with_attribute_syntax_is_inherited_by_an_action()
    {
        var controller = typeof(KeyOptionalController).GetCustomAttributes(typeof(IdempotentAttribute), false);
        var action = typeof(KeyOptionalController).GetMethod(nameof(KeyOptionalController.Create))!
            .GetCustomAttributes(typeof(IdempotentAttribute), false);

        var merged = IdempotentAttribute.Merge([.. controller.Concat(action).Cast<IdempotentAttribute>()]);

        merged!.Required.Should().BeFalse();
        merged.ResponseTtlSeconds.Should().Be(60);
    }

    [Fact]
    public async Task Unknown_policy_name_fails_startup_and_names_the_endpoint()
    {
        _unknownPolicyEndpoint = true;

        var act = () => StartAsync(options => options.ValidateEndpointsOnStartup = true);

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
                    app.Use((context, next) =>
                    {
                        // Stands in for middleware that resolves a per-request prefix.
                        if (context.Request.Headers.TryGetValue("X-Prefix", out var prefix))
                            context.Items[IdempotencyOptions.DefaultKeyPrefixItemKey] = prefix.ToString();

                        return next(context);
                    });
                    app.UseIdempotencyGuard();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/default", CreatedAsync);
                        endpoints.MapPost("/capture", context =>
                        {
                            _captured = (IIdempotencyContext)context.Items["IdempotencyContext"]!;
                            return CreatedAsync(context);
                        });
                        endpoints.MapPost("/user", CreatedAsync).RequireIdempotency(o => o.Policy = "user");
                        endpoints.MapPost("/service", CreatedAsync).RequireIdempotency(o => o.Policy = "service");
                        endpoints.MapPost("/fails-once", FailsOnceAsync);
                        endpoints.MapPost("/fails-once-cache-all", FailsOnceAsync).RequireIdempotency(o => o.Policy = "cache-all");
                        endpoints.MapPost("/required-false-then-unset", CreatedAsync)
                            .WithMetadata(new IdempotentAttribute { Required = false })
                            .WithMetadata(new IdempotentAttribute { ResponseTtlSeconds = 60 });
                        endpoints.MapPost("/required-false-then-true", CreatedAsync)
                            .WithMetadata(new IdempotentAttribute { Required = false })
                            .WithMetadata(new IdempotentAttribute { Required = true });
                        endpoints.MapPost("/unset-then-required-false", CreatedAsync)
                            .WithMetadata(new IdempotentAttribute { ResponseTtlSeconds = 60 })
                            .WithMetadata(new IdempotentAttribute { Required = false });
                        endpoints.MapPost("/unset-then-unset", CreatedAsync)
                            .WithMetadata(new IdempotentAttribute { ClaimTtlSeconds = 60 })
                            .WithMetadata(new IdempotentAttribute { ResponseTtlSeconds = 60 });
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

    [Idempotent(Required = false)]
    private sealed class KeyOptionalController
    {
        [Idempotent(ResponseTtlSeconds = 60)]
        public void Create() { }
    }

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
