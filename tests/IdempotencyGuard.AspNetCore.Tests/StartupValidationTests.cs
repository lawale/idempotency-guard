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
        string environment, Action<IdempotencyOptions> configure, Action<IEndpointRouteBuilder> map)
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
