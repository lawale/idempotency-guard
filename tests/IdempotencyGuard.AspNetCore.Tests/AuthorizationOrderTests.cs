using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using FluentAssertions;
using IdempotencyGuard.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore.Tests;

public class AuthorizationOrderTests : IAsyncLifetime
{
    private const string ReplayedHeader = "X-Idempotent-Replayed";

    private IHost? _host;

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
    [InlineData("/attribute")]
    [InlineData("/policy")]
    public async Task Guard_before_authorization_throws_instead_of_serving_the_request(string path)
    {
        var client = await StartAsync(guardFirst: true);

        var act = () => client.SendAsync(Post(path, NewKey(), jwt: "alice"));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("UseAuthorization");
    }

    [Fact]
    public async Task Guard_before_authorization_is_allowed_on_anonymous_endpoints()
    {
        var client = await StartAsync(guardFirst: true);

        (await client.SendAsync(Post("/public", NewKey()))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.SendAsync(Post("/allow-anonymous", NewKey()))).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Order_check_can_be_suppressed()
    {
        var client = await StartAsync(guardFirst: true, configure: options => options.SuppressAuthorizationOrderCheck = true);

        var response = await client.SendAsync(Post("/attribute", NewKey(), jwt: "alice"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Guard_after_authorization_does_not_replay_to_an_anonymous_caller()
    {
        var client = await StartAsync(guardFirst: false);
        var key = NewKey();

        await client.SendAsync(Post("/attribute", key, jwt: "alice"));
        var response = await client.SendAsync(Post("/attribute", key));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains(ReplayedHeader).Should().BeFalse();
    }

    [Fact]
    public async Task Guard_after_authorization_does_not_store_an_anonymous_callers_401()
    {
        var client = await StartAsync(guardFirst: false);
        var key = NewKey();

        await client.SendAsync(Post("/attribute", key));
        var response = await client.SendAsync(Post("/attribute", key, jwt: "alice"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Scope_can_read_identity_from_a_non_default_authentication_scheme()
    {
        var client = await StartAsync(guardFirst: false, configure: options =>
            options.KeyScope = context => context.User.FindFirstValue("client_id"));

        // Only UseAuthorization authenticates a scheme that isn't the default.
        var response = await client.SendAsync(Post("/service", NewKey(), apiKey: "billing"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Users_and_services_on_one_app_get_separate_key_spaces()
    {
        var client = await StartAsync(guardFirst: false, configure: options =>
        {
            options.AddPolicy("user", p => p.KeyScope = context => context.User.FindFirstValue("sub"));
            options.AddPolicy("service", p => p.KeyScope = context => context.User.FindFirstValue("client_id"));
        });
        var key = NewKey();

        await client.SendAsync(Post("/user-scoped", key, jwt: "abc"));

        var sameUser = await client.SendAsync(Post("/user-scoped", key, jwt: "abc"));
        var otherUser = await client.SendAsync(Post("/user-scoped", key, jwt: "def"));
        var service = await client.SendAsync(Post("/service-scoped", key, apiKey: "abc"));

        sameUser.Headers.Contains(ReplayedHeader).Should().BeTrue();
        otherUser.Headers.Contains(ReplayedHeader).Should().BeFalse();
        (await otherUser.Content.ReadAsStringAsync()).Should().Contain("def");
        service.StatusCode.Should().Be(HttpStatusCode.Created);
        service.Headers.Contains(ReplayedHeader).Should().BeFalse();
    }

    private async Task<HttpClient> StartAsync(bool guardFirst, Action<IdempotencyOptions>? configure = null)
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddAuthentication("Jwt")
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("Jwt", _ => { })
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("ApiKey", _ => { });
                    services.AddAuthorization();
                    services.AddIdempotencyGuard(options =>
                    {
                        options.Cleanup.Enabled = false;
                        options.SuppressUnscopedKeysWarning = true;
                        configure?.Invoke(options);

                        // Every test host maps all the endpoints below, so a test only
                        // registers the policies whose behaviour it is exercising.
                        foreach (var name in new[] { "user", "service" })
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
                    app.UseAuthentication();
                    if (guardFirst)
                        app.UseIdempotencyGuard();
                    app.UseAuthorization();
                    if (!guardFirst)
                        app.UseIdempotencyGuard();
                    app.UseEndpoints(endpoints =>
                    {
                        var jwt = new AuthorizeAttribute { AuthenticationSchemes = "Jwt" };
                        var apiKey = new AuthorizeAttribute { AuthenticationSchemes = "ApiKey" };

                        endpoints.MapPost("/attribute", EchoAsync).RequireAuthorization(jwt);
                        endpoints.MapPost("/policy", EchoAsync).RequireAuthorization(
                            new AuthorizationPolicyBuilder("Jwt").RequireAuthenticatedUser().Build());
                        endpoints.MapPost("/service", EchoAsync).RequireAuthorization(apiKey);
                        endpoints.MapPost("/public", EchoAsync);
                        endpoints.MapPost("/allow-anonymous", EchoAsync).RequireAuthorization(jwt).AllowAnonymous();
                        endpoints.MapPost("/user-scoped", EchoAsync).RequireAuthorization(jwt)
                            .RequireIdempotency(o => o.Policy = "user");
                        endpoints.MapPost("/service-scoped", EchoAsync).RequireAuthorization(apiKey)
                            .RequireIdempotency(o => o.Policy = "service");
                    });
                });
            })
            .StartAsync();

        return _host.GetTestClient();
    }

    private static async Task EchoAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status201Created;
        var caller = context.User.Claims.FirstOrDefault()?.Value ?? "anonymous";
        await context.Response.WriteAsync($$"""{"caller":"{{caller}}"}""");
    }

    private static string NewKey() => $"auth-{Guid.NewGuid():N}";

    private static HttpRequestMessage Post(string path, string key, string? jwt = null, string? apiKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", key);
        if (jwt is not null)
            request.Headers.Add("X-Jwt", jwt);
        if (apiKey is not null)
            request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var (header, claim) = Scheme.Name == "Jwt" ? ("X-Jwt", "sub") : ("X-Api-Key", "client_id");
            var value = Request.Headers[header].ToString();

            if (value.Length == 0)
                return Task.FromResult(AuthenticateResult.NoResult());

            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claim, value)], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
