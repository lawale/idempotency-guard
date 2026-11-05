using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using IdempotencyGuard.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyGuard.AspNetCore.Tests;

/// <summary>
/// A caller on a route with no scope chooses the whole key, and scope hashes are unkeyed, so
/// they can compute what a scoped caller's key looks like. None of that may reach the scoped
/// caller's entry, response or downstream key.
/// </summary>
public class ScopeIsolationTests : IAsyncLifetime
{
    private const string Replayed = "X-Idempotent-Replayed";
    private const string AliceKey = "abc";
    private const string PublicHost = "http://public.example";
    private const string InternalHost = "http://internal.example";

    private IHost? _host;
    private readonly List<string> _handled = [];
    private readonly ConcurrentDictionary<string, string> _provider = new();
    private IIdempotencyContext? _captured;

    // The identity with and without its tag, percent-encoded so NUL stays out of test names.
    public static TheoryData<string> Identities => new() { "user%00alice", "p%004:user%00alice" };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public async Task Forged_key_on_another_path_is_a_new_request(string identity)
    {
        var client = await StartAsync(UserPolicy, ScopedAndOpen);

        await client.SendAsync(Post("/scoped", AliceKey, "alice"));
        var mallory = await client.SendAsync(Post("/open", Forge(identity), "mallory"));

        await ShouldBeFreshResponseFor(mallory, "mallory");
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public async Task Forged_key_sent_first_does_not_block_the_scoped_caller(string identity)
    {
        var client = await StartAsync(UserPolicy, ScopedAndOpen);

        await client.SendAsync(Post("/open", Forge(identity), "mallory"));
        var alice = await client.SendAsync(Post("/scoped", AliceKey, "alice"));

        await ShouldBeFreshResponseFor(alice, "alice");
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public async Task Forged_key_does_not_replay_when_the_fingerprint_matches(string identity)
    {
        var client = await StartAsync(UserPolicy, HostSplit);

        await client.SendAsync(Post($"{PublicHost}/pay", AliceKey, "alice"));
        var mallory = await client.SendAsync(Post($"{InternalHost}/pay", Forge(identity), "mallory"));

        await ShouldBeFreshResponseFor(mallory, "mallory");
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public async Task Forged_key_sent_first_is_not_replayed_to_the_scoped_caller(string identity)
    {
        var client = await StartAsync(UserPolicy, HostSplit);

        await client.SendAsync(Post($"{InternalHost}/pay", Forge(identity), "mallory"));
        var alice = await client.SendAsync(Post($"{PublicHost}/pay", AliceKey, "alice"));

        await ShouldBeFreshResponseFor(alice, "alice");
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public async Task Forged_key_does_not_replay_when_the_body_is_not_fingerprinted(string identity)
    {
        var client = await StartAsync(options =>
        {
            UserPolicy(options);
            options.MaxFingerprintBodySize = 0;
        }, HostSplit);

        await client.SendAsync(Post($"{PublicHost}/pay", AliceKey, "alice", """{"amount":100}"""));
        var mallory = await client.SendAsync(Post($"{InternalHost}/pay", Forge(identity), "mallory"));

        await ShouldBeFreshResponseFor(mallory, "mallory");
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public async Task Forged_key_does_not_replay_across_a_policy_with_no_scope(string identity)
    {
        var client = await StartAsync(options =>
        {
            UserPolicy(options);
            options.AddPolicy("plain", _ => { });
        }, endpoints =>
        {
            endpoints.MapPost("/pay", Echo).RequireHost("public.example").RequireIdempotency(o => o.Policy = "user");
            endpoints.MapPost("/pay", Echo).RequireHost("internal.example").RequireIdempotency(o => o.Policy = "plain");
        });

        await client.SendAsync(Post($"{PublicHost}/pay", AliceKey, "alice"));
        var mallory = await client.SendAsync(Post($"{InternalHost}/pay", Forge(identity), "mallory"));

        await ShouldBeFreshResponseFor(mallory, "mallory");
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public async Task Forged_key_does_not_replay_across_app_branches(string identity)
    {
        var client = await StartWithPipelineAsync(UserPolicy, app =>
        {
            app.Map("/public", branch =>
            {
                branch.UseRouting();
                branch.UseIdempotencyGuard();
                branch.UseEndpoints(e => e.MapPost("/pay", Echo).RequireIdempotency(o => o.Policy = "user"));
            });
            app.Map("/internal", branch =>
            {
                branch.UseRouting();
                branch.UseIdempotencyGuard();
                branch.UseEndpoints(e => e.MapPost("/pay", Echo));
            });
        });

        await client.SendAsync(Post("/public/pay", AliceKey, "alice"));
        var mallory = await client.SendAsync(Post("/internal/pay", Forge(identity), "mallory"));

        await ShouldBeFreshResponseFor(mallory, "mallory");
    }

    [Fact]
    public async Task Forged_key_does_not_replay_by_moving_the_body_into_the_path()
    {
        var client = await StartAsync(UserPolicy, endpoints =>
        {
            endpoints.MapPost("/pay", Echo).RequireIdempotency(o => o.Policy = "user");
            endpoints.MapPost("/{**rest}", Echo);
        });
        const string body = """{"amount":100}""";

        await client.SendAsync(Post("/pay", AliceKey, "alice", body));
        var mallory = await client.SendAsync(
            Post($"/pay:{Uri.EscapeDataString(body)}", Forge("user%00alice"), "mallory", ""));

        await ShouldBeFreshResponseFor(mallory, "mallory");
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public async Task Forged_key_gets_a_different_downstream_key_after_a_released_claim(string identity)
    {
        var client = await StartAsync(UserPolicy, endpoints =>
        {
            endpoints.MapPost("/charge", Charge(throwsAfterCharging: true)).RequireIdempotency(o => o.Policy = "user");
            endpoints.MapPost("/open", Charge());
        });

        var alice = () => client.SendAsync(Post("/charge", AliceKey, "alice"));
        await alice.Should().ThrowAsync<InvalidOperationException>();
        var mallory = await client.SendAsync(Post("/open", Forge(identity), "mallory"));

        (await mallory.Content.ReadAsStringAsync()).Should().Contain("charge-of-mallory");
        _provider.Should().HaveCount(2);
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public async Task Forged_key_with_the_prefix_gets_a_different_downstream_key(string identity)
    {
        var client = await StartAsync(UserPolicy, endpoints =>
        {
            endpoints.MapPost("/charge", Charge()).RequireIdempotency(o => o.Policy = "user");
            endpoints.MapPost("/open", Charge());
        });
        var alice = Post("/charge", AliceKey, "alice");
        alice.Headers.Add("X-Prefix", "tenant-1:");

        await client.SendAsync(alice);
        var mallory = await client.SendAsync(Post("/open", $"tenant-1:{Forge(identity)}", "mallory"));

        (await mallory.Content.ReadAsStringAsync()).Should().Contain("charge-of-mallory");
        _provider.Should().HaveCount(2);
    }

    [Theory]
    [MemberData(nameof(Identities))]
    public async Task Forged_key_with_the_prefix_sent_first_does_not_give_the_scoped_caller_its_charge(string identity)
    {
        var client = await StartAsync(UserPolicy, endpoints =>
        {
            endpoints.MapPost("/charge", Charge()).RequireIdempotency(o => o.Policy = "user");
            endpoints.MapPost("/open", Charge());
        });
        var alice = Post("/charge", AliceKey, "alice");
        alice.Headers.Add("X-Prefix", "tenant-1:");

        await client.SendAsync(Post("/open", $"tenant-1:{Forge(identity)}", "mallory"));
        var response = await client.SendAsync(alice);

        (await response.Content.ReadAsStringAsync()).Should().Contain("charge-of-alice");
    }

    [Fact]
    public async Task Global_scope_cannot_imitate_a_policy_scope()
    {
        var client = await StartAsync(options =>
        {
            UserPolicy(options);
            // Stands in for a claim or stored identifier that can carry NUL.
            options.KeyScope = context => Uri.UnescapeDataString(context.Request.Headers["X-Caller"].ToString());
        }, HostSplit);

        await client.SendAsync(Post($"{PublicHost}/pay", AliceKey, "alice"));
        var mallory = await client.SendAsync(Post($"{InternalHost}/pay", AliceKey, "user%00alice"));

        await ShouldBeFreshResponseFor(mallory, "user%00alice");
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(0)]
    public async Task Client_key_cannot_contain_the_scope_separator(int maxKeyLength)
    {
        var client = await StartAsync(options =>
        {
            UserPolicy(options);
            options.MaxKeyLength = maxKeyLength;
        }, ScopedAndOpen);
        var request = Post("/open", "unused", "mallory");
        request.Headers.Remove("Idempotency-Key");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", $"{Hash("p\04:user\0alice")}\u001F{AliceKey}");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Literals, computed outside this code: these are the keys a payment provider sees, and
    // changing them after release means the provider misses a duplicate.
    [Fact]
    public async Task Scoped_downstream_key_format_is_pinned()
    {
        var client = await StartAsync(
            options => options.KeyScope = context => context.Request.Headers["X-Caller"].ToString(),
            endpoints => endpoints.MapPost("/capture", Capture));

        await client.SendAsync(Post("/capture", "abc-123", "alice"));

        _captured!.DownstreamKey("stripe", "charge").Should().Be("d15e6f1d347979d27e59bcafe34f238da5ff64ebe509de999fd7b5be86dd6df2");
    }

    [Fact]
    public async Task Policy_scoped_downstream_key_format_is_pinned()
    {
        var client = await StartAsync(UserPolicy, endpoints =>
            endpoints.MapPost("/capture", Capture).RequireIdempotency(o => o.Policy = "user"));

        await client.SendAsync(Post("/capture", "abc-123", "alice"));

        _captured!.DownstreamKey("stripe", "charge").Should().Be("db02fc102b1b0a9ab1048d7258787dc24004fcc35b5a73177fc17ff5ac6b22dc");
    }

    private static void UserPolicy(IdempotencyOptions options) =>
        options.AddPolicy("user", p => p.KeyScope = context => context.Request.Headers["X-Caller"].ToString());

    private void ScopedAndOpen(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/scoped", Echo).RequireIdempotency(o => o.Policy = "user");
        endpoints.MapPost("/open", Echo);
    }

    private void HostSplit(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/pay", Echo).RequireHost("public.example").RequireIdempotency(o => o.Policy = "user");
        endpoints.MapPost("/pay", Echo).RequireHost("internal.example");
    }

    private static string Forge(string identity) => $"{Hash(Uri.UnescapeDataString(identity))}:{AliceKey}";

    // What anyone can compute offline: an unkeyed SHA-256, truncated to 16 bytes.
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)), 0, 16).ToLowerInvariant();

    private static string OwnedBy(string caller) => $"owned by {caller}";

    private async Task ShouldBeFreshResponseFor(HttpResponseMessage response, string caller)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Contains(Replayed).Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().Be(OwnedBy(caller));
        _handled.Should().Contain(caller);
    }

    private async Task Echo(HttpContext context)
    {
        var caller = context.Request.Headers["X-Caller"].ToString();
        lock (_handled) _handled.Add(caller);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsync(OwnedBy(caller));
    }

    private Task Capture(HttpContext context)
    {
        _captured = (IIdempotencyContext)context.Items["IdempotencyContext"]!;
        return Task.CompletedTask;
    }

    // A fake provider: the first call under a key wins, and later calls get its result.
    private RequestDelegate Charge(bool throwsAfterCharging = false) => async context =>
    {
        var caller = context.Request.Headers["X-Caller"].ToString();
        var idempotency = (IIdempotencyContext)context.Items["IdempotencyContext"]!;
        var charge = _provider.GetOrAdd(idempotency.DownstreamKey("stripe", "charge"), _ => $"charge-of-{caller}");

        if (throwsAfterCharging)
        {
            throw new InvalidOperationException("Failed after the provider call");
        }

        context.Response.StatusCode = StatusCodes.Status201Created;
        await context.Response.WriteAsync(charge);
    };

    private Task<HttpClient> StartAsync(Action<IdempotencyOptions> configure, Action<IEndpointRouteBuilder> map) =>
        StartWithPipelineAsync(configure, app =>
        {
            app.UseRouting();
            app.Use((context, next) =>
            {
                if (context.Request.Headers.TryGetValue("X-Prefix", out var prefix))
                {
                    context.Items[IdempotencyOptions.DefaultKeyPrefixItemKey] = prefix.ToString();
                }

                return next(context);
            });
            app.UseIdempotencyGuard();
            app.UseEndpoints(map);
        });

    private async Task<HttpClient> StartWithPipelineAsync(Action<IdempotencyOptions> configure, Action<IApplicationBuilder> pipeline)
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddIdempotencyGuard(options =>
                    {
                        options.Cleanup.Enabled = false;
                        configure(options);
                    });
                    services.AddIdempotencyGuardInMemoryStore();
                    services.AddRouting();
                });
                web.Configure(pipeline);
            })
            .StartAsync();
        return _host.GetTestClient();
    }

    private static HttpRequestMessage Post(string uri, string key, string caller, string body = "{}")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        request.Headers.Add("X-Caller", caller);
        return request;
    }
}
