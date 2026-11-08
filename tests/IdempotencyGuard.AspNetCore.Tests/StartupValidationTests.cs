using System.Net;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace IdempotencyGuard.AspNetCore.Tests;

public class StartupValidationTests : IAsyncLifetime
{
    private IHost? _host;
    private bool _handled;
    private readonly CountingEndpointSource _source = new();
    private readonly CapturingLoggerProvider _logs = new();

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
    [InlineData("Development", null, true)]
    [InlineData("Production", null, false)]
    [InlineData("Staging", null, false)]
    [InlineData("Production", true, true)]
    [InlineData("Development", false, false)]
    public async Task Environment_decides_unless_the_option_is_set(string environment, bool? option, bool validates)
    {
        var start = () => StartAsync(environment, options => options.ValidateEndpointsOnStartup = option, UnknownPolicy);

        if (validates)
        {
            (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*missing*");
        }
        else
        {
            await start.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task Endpoints_are_not_read_when_validation_is_off()
    {
        await StartAsync("Production", _ => { }, _ => { });

        _source.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Endpoints_are_not_read_when_validation_is_off_and_every_warning_applies()
    {
        await StartAsync("Production", options => options.AddPolicy("user", p => p.KeyScope = _ => "x"), ScopedAndOpen);

        _source.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Endpoints_are_read_when_validation_is_on()
    {
        await StartAsync("Development", _ => { }, _ => { });

        _source.Reads.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Unknown_policy_fails_the_request_before_the_handler_when_validation_is_off()
    {
        var client = await StartAsync("Production", _ => { }, UnknownPolicy);

        var send = () => client.SendAsync(Post("/unknown"));

        await send.Should().ThrowAsync<InvalidOperationException>();
        _handled.Should().BeFalse();
    }

    [Fact]
    public async Task KeyPrefix_is_checked_whatever_the_setting()
    {
        var start = () => StartAsync("Production", options =>
        {
            options.ValidateEndpointsOnStartup = false;
            options.KeyPrefix = new string('p', IdempotencyStoreKey.MaxPrefixLength + 1);
        }, _ => { });

        await start.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Warning_about_no_scope_at_all_is_logged_whatever_the_setting()
    {
        await StartAsync("Production", _ => { }, _ => { });

        _logs.Warnings.Should().ContainSingle().Which.Should().Contain("not scoped");
    }

    [Fact]
    public async Task Warning_names_routes_left_unscoped_while_others_have_a_scope()
    {
        await StartAsync("Development", options =>
            options.AddPolicy("user", p => p.KeyScope = _ => "x"), ScopedAndOpen);

        var warning = _logs.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("/open").And.Contain("/any");
        warning.Should().NotContain("/scoped").And.NotContain("/read");
    }

    [Fact]
    public async Task Warning_counts_routes_beyond_the_ones_it_names()
    {
        await StartAsync("Development", options =>
            options.AddPolicy("user", p => p.KeyScope = _ => "x"), endpoints =>
        {
            endpoints.MapPost("/scoped", Handle).RequireIdempotency(o => o.Policy = "user");
            for (var i = 0; i < 25; i++)
            {
                endpoints.MapPost($"/open{i}", Handle);
            }
        });

        var warning = _logs.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("25").And.Contain("/open0").And.NotContain("/open24");
    }

    [Fact]
    public async Task No_route_warning_when_a_global_scope_covers_every_route()
    {
        await StartAsync("Development", options =>
        {
            options.KeyScope = _ => "x";
            options.AddPolicy("user", p => p.KeyScope = _ => "x");
        }, ScopedAndOpen);

        _logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task No_route_warning_when_every_enforced_route_has_a_scope()
    {
        await StartAsync("Development", options =>
            options.AddPolicy("user", p => p.KeyScope = _ => "x"), endpoints =>
        {
            endpoints.MapPost("/scoped", Handle).RequireIdempotency(o => o.Policy = "user");
            endpoints.MapGet("/read", Handle);
        });

        _logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task No_route_warning_when_warnings_are_suppressed()
    {
        await StartAsync("Development", options =>
        {
            options.SuppressUnscopedKeysWarning = true;
            options.AddPolicy("user", p => p.KeyScope = _ => "x");
        }, ScopedAndOpen);

        _logs.Warnings.Should().BeEmpty();
    }

    // A filter takes request paths, and route patterns aren't paths, so it can't be applied here.
    [Fact]
    public async Task No_route_warning_when_an_endpoint_filter_decides_what_is_guarded()
    {
        await StartAsync("Development", options =>
        {
            options.EndpointFilter = (_, path) => path.StartsWith("/scoped");
            options.AddPolicy("user", p => p.KeyScope = _ => "x");
        }, ScopedAndOpen);

        _logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Route_warning_skips_unmarked_routes_when_they_are_ignored()
    {
        await StartAsync("Development", options =>
        {
            options.AddPolicy("user", p => p.KeyScope = _ => "x");
            options.UnmarkedEndpoints = UnmarkedEndpointBehavior.Ignore;
        }, endpoints =>
        {
            ScopedAndOpen(endpoints);
            endpoints.MapPost("/marked-unscoped", Handle).RequireIdempotency();
        });

        var warning = _logs.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("/marked-unscoped");
        warning.Should().NotContain("/open").And.NotContain("/any");
    }

    [Fact]
    public async Task No_route_warning_when_validation_is_off()
    {
        await StartAsync("Production", options =>
            options.AddPolicy("user", p => p.KeyScope = _ => "x"), ScopedAndOpen);

        _logs.Warnings.Should().BeEmpty();
    }

    private static readonly Dictionary<string, Action<IdempotencyOptions>> InvalidOptions = new()
    {
        ["ClaimTtl zero"] = o => o.ClaimTtl = TimeSpan.Zero,
        ["ClaimTtl negative"] = o => o.ClaimTtl = TimeSpan.FromSeconds(-1),
        ["ResponseTtl zero"] = o => o.ResponseTtl = TimeSpan.Zero,
        ["ResponseTtl negative"] = o => o.ResponseTtl = TimeSpan.FromSeconds(-1),
        ["MaxResponseBodySize zero"] = o => o.MaxResponseBodySize = 0,
        ["MaxResponseBodySize negative"] = o => o.MaxResponseBodySize = -1,
        ["HeaderName empty"] = o => o.HeaderName = "",
        ["HeaderName blank"] = o => o.HeaderName = " ",
        ["HeaderName null"] = o => o.HeaderName = null!,
        ["ReplayedHeaderName empty"] = o => o.ReplayedHeaderName = "",
        ["ReplayedHeaderName null"] = o => o.ReplayedHeaderName = null!,
        ["EnforcedMethods null"] = o => o.EnforcedMethods = null!,
        ["Cleanup null"] = o => o.Cleanup = null!,
        ["Cleanup.Interval zero"] = o => o.Cleanup.Interval = TimeSpan.Zero,
        ["Cleanup.Interval negative"] = o => o.Cleanup.Interval = TimeSpan.FromSeconds(-1),
        ["Cleanup.BatchSize zero"] = o => o.Cleanup.BatchSize = 0,
        ["Cleanup.BatchSize negative"] = o => o.Cleanup.BatchSize = -1,
        ["Cleanup.MaxIterationsPerSweep zero"] = o => o.Cleanup.MaxIterationsPerSweep = 0,
        ["Cleanup.MaxIterationsPerSweep negative"] = o => o.Cleanup.MaxIterationsPerSweep = -1,
    };

    public static IEnumerable<object[]> InvalidOptionNames => InvalidOptions.Keys.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(InvalidOptionNames))]
    public async Task Invalid_option_fails_startup_and_names_the_setting(string name)
    {
        // Production with validation off: these checks must not depend on reading endpoints.
        var start = () => StartAsync("Production", InvalidOptions[name], _ => { });

        var setting = name[..name.LastIndexOf(' ')];
        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{setting}*");
        _source.Reads.Should().Be(0);
    }

    [Fact]
    public async Task Every_invalid_option_is_reported_at_once()
    {
        var start = () => StartAsync("Production", options =>
        {
            options.ClaimTtl = TimeSpan.Zero;
            options.ResponseTtl = TimeSpan.Zero;
            options.HeaderName = "";
        }, _ => { });

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*HeaderName*ClaimTtl*ResponseTtl*");
    }

    [Theory]
    [InlineData("Cleanup.Interval negative")]
    [InlineData("Cleanup.Interval zero")]
    public async Task Cleanup_service_never_runs_with_an_invalid_option(string name)
    {
        // Run after the service started, these checks would lose the race: it would sweep, then
        // spin on a zero interval, or stop the host on a negative one and hide the real error.
        var store = new CountingPurgeStore();
        var start = () => StartAsync("Production", options =>
        {
            options.Cleanup.Enabled = true;
            InvalidOptions[name](options);
        }, _ => { }, services => services.AddSingleton<IIdempotencyStore>(store));

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Cleanup.Interval*");
        store.Purges.Should().Be(0);
    }

    [Fact]
    public async Task Option_checks_accept_small_positive_values()
    {
        var start = () => StartAsync("Production", options =>
        {
            options.ClaimTtl = TimeSpan.FromMilliseconds(1);
            options.ResponseTtl = TimeSpan.FromMilliseconds(1);
            options.MaxResponseBodySize = 1;
            options.Cleanup.Interval = TimeSpan.FromMilliseconds(1);
            options.Cleanup.BatchSize = 1;
            options.Cleanup.MaxIterationsPerSweep = 1;
        }, _ => { });

        await start.Should().NotThrowAsync();
    }

    private void MarkedWithFingerprintProperties(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/selective", Handle).RequireIdempotency(o => o.FingerprintProperties = ["amount"]);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Fingerprint_properties_without_a_body_to_read_fail_startup(int size)
    {
        var start = () => StartAsync("Development", options => options.MaxFingerprintBodySize = size, MarkedWithFingerprintProperties);

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*/selective*MaxFingerprintBodySize*");
    }

    [Fact]
    public async Task Fingerprint_properties_are_inherited_when_checking_for_a_body_to_read()
    {
        var start = () => StartAsync("Development", options => options.MaxFingerprintBodySize = 0, endpoints =>
            endpoints.MapPost("/merged", Handle)
                .WithMetadata(new IdempotentAttribute { FingerprintProperties = ["amount"] })
                .WithMetadata(new IdempotentAttribute { ResponseTtlSeconds = 60 }));

        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*/merged*");
    }

    [Fact]
    public async Task Skipping_the_body_alone_is_allowed()
    {
        var start = () => StartAsync("Development", options => options.MaxFingerprintBodySize = 0,
            endpoints => endpoints.MapPost("/whole-body", Handle).RequireIdempotency());

        await start.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Custom_fingerprint_builder_may_combine_properties_with_no_body_limit()
    {
        var start = () => StartAsync("Development", options => options.MaxFingerprintBodySize = 0, MarkedWithFingerprintProperties,
            services => services.AddSingleton<IFingerprintBuilder, CustomFingerprintBuilder>());

        await start.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Fingerprint_properties_without_a_body_to_read_fail_each_request_when_not_validated()
    {
        var client = await StartAsync("Production", options => options.MaxFingerprintBodySize = 0, MarkedWithFingerprintProperties);

        var send = () => client.SendAsync(Post("/selective"));

        (await send.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*MaxFingerprintBodySize*");
        _handled.Should().BeFalse();
    }

    [Fact]
    public async Task Fingerprint_properties_without_a_body_to_read_work_with_a_custom_builder_when_not_validated()
    {
        var client = await StartAsync("Production", options => options.MaxFingerprintBodySize = 0, MarkedWithFingerprintProperties,
            services => services.AddSingleton<IFingerprintBuilder, CustomFingerprintBuilder>());

        var response = await client.SendAsync(Post("/selective"));

        response.IsSuccessStatusCode.Should().BeTrue();
        _handled.Should().BeTrue();
    }

    [Fact]
    public async Task Warning_names_marked_routes_the_guard_skips_because_of_their_method()
    {
        await StartAsync("Development", options => options.SuppressUnscopedKeysWarning = true, endpoints =>
        {
            endpoints.MapDelete("/marked-delete", Handle).RequireIdempotency();
            endpoints.MapDelete("/unmarked-delete", Handle);
            endpoints.MapGet("/marked-get", Handle).RequireIdempotency();
            endpoints.MapPost("/marked-post", Handle).RequireIdempotency();
            endpoints.MapMethods("/marked-any-enforced", ["DELETE", "POST"], Handle).RequireIdempotency();
            endpoints.Map("/marked-all-methods", Handle).RequireIdempotency();
        });

        var warning = _logs.Warnings.Should().ContainSingle().Subject;
        warning.Should().Contain("/marked-delete");
        warning.Should().NotContain("/unmarked-delete").And.NotContain("/marked-get")
            .And.NotContain("/marked-post").And.NotContain("/marked-any-enforced").And.NotContain("/marked-all-methods");
    }

    [Fact]
    public async Task No_method_warning_once_the_method_is_enforced()
    {
        await StartAsync("Development", options =>
        {
            options.SuppressUnscopedKeysWarning = true;
            options.EnforcedMethods = ["delete"];
        }, endpoints => endpoints.MapDelete("/marked-delete", Handle).RequireIdempotency());

        _logs.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task No_method_warning_when_validation_is_off()
    {
        await StartAsync("Production", options => options.SuppressUnscopedKeysWarning = true,
            endpoints => endpoints.MapDelete("/marked-delete", Handle).RequireIdempotency());

        _logs.Warnings.Should().BeEmpty();
    }

    private sealed class CountingPurgeStore : IIdempotencyStore, IPurgableIdempotencyStore
    {
        private readonly InMemoryIdempotencyStore _inner = new();
        private int _purges;

        public int Purges => _purges;

        public Task<int> PurgeExpiredAsync(int batchSize, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _purges);
            return _inner.PurgeExpiredAsync(batchSize, ct);
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

    private sealed class CustomFingerprintBuilder : IFingerprintBuilder
    {
        public Task<FingerprintResult> ComputeAsync(HttpContext context, IdempotentAttribute? attribute) =>
            Task.FromResult(new FingerprintResult("custom"));
    }

    private void UnknownPolicy(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/unknown", Handle).RequireIdempotency(o => o.Policy = "missing");

    private void ScopedAndOpen(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/scoped", Handle).RequireIdempotency(o => o.Policy = "user");
        endpoints.MapPost("/open", Handle);
        endpoints.Map("/any", Handle);
        endpoints.MapGet("/read", Handle);
    }

    private Task Handle(HttpContext context)
    {
        _handled = true;
        return Task.CompletedTask;
    }

    private async Task<HttpClient> StartAsync(
        string environment,
        Action<IdempotencyOptions> configure,
        Action<IEndpointRouteBuilder> map,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(_logs))
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
                    services.AddSingleton<EndpointDataSource>(_source);
                    configureServices?.Invoke(services);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseIdempotencyGuard();
                    app.UseEndpoints(map);
                });
            })
            .Build();

        _host = host;
        await host.StartAsync();
        return host.GetTestClient();
    }

    private static HttpRequestMessage Post(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", "abc");
        return request;
    }

    private sealed class CountingEndpointSource : EndpointDataSource
    {
        public int Reads { get; private set; }

        public override IReadOnlyList<Endpoint> Endpoints
        {
            get
            {
                Reads++;
                return [];
            }
        }

        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ILogger
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return _warnings.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) =>
            categoryName.Contains("Idempotency") ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (_warnings)
                {
                    _warnings.Add(formatter(state, exception));
                }
            }
        }

        public void Dispose()
        {
        }
    }
}
