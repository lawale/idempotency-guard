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
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore.Tests;

/// <summary>
/// A key reused on a different endpoint is a different request, even when the method, path and
/// body match. A retry of the same endpoint has to replay however it reached the server.
/// </summary>
public class FingerprintRoutingTests : IAsyncLifetime
{
    private const string Replayed = "X-Idempotent-Replayed";

    private IHost? _host;
    private int _handled;

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
    public async Task Key_reused_on_an_endpoint_for_another_host_is_a_422()
    {
        var client = await StartWithEndpointsAsync(endpoints =>
        {
            endpoints.MapPost("/pay", Handle).RequireHost("public.example");
            endpoints.MapPost("/pay", Handle).RequireHost("internal.example");
        });

        await client.SendAsync(Post("http://public.example/pay"));
        var response = await client.SendAsync(Post("http://internal.example/pay"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _handled.Should().Be(1);
    }

    [Fact]
    public async Task Key_reused_on_another_host_is_a_422_when_the_endpoints_are_attributed()
    {
        var client = await StartWithEndpointsAsync(endpoints =>
        {
            endpoints.MapPost("/pay", Handle).RequireHost("public.example")
                .RequireIdempotency(o => o.FingerprintQueryParameters = ["a"]);
            endpoints.MapPost("/pay", Handle).RequireHost("internal.example")
                .RequireIdempotency(o => o.FingerprintQueryParameters = ["a"]);
        });

        await client.SendAsync(Post("http://public.example/pay?a=1"));
        var response = await client.SendAsync(Post("http://internal.example/pay?a=1"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Key_reused_between_a_host_constrained_endpoint_and_an_open_one_is_a_422()
    {
        var client = await StartWithEndpointsAsync(endpoints =>
        {
            endpoints.MapPost("/pay", Handle).RequireHost("public.example");
            endpoints.MapPost("/pay", Handle);
        });

        await client.SendAsync(Post("http://public.example/pay"));
        var response = await client.SendAsync(Post("http://other.example/pay"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Key_reused_on_another_app_branch_is_a_422()
    {
        var client = await StartWithBranchesAsync();

        await client.SendAsync(Post("/public/pay"));
        var response = await client.SendAsync(Post("/internal/pay"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _handled.Should().Be(1);
    }

    [Fact]
    public async Task Retry_on_the_same_app_branch_replays()
    {
        var client = await StartWithBranchesAsync();

        await client.SendAsync(Post("/public/pay"));
        var response = await client.SendAsync(Post("/public/pay"));

        response.Headers.Contains(Replayed).Should().BeTrue();
        _handled.Should().Be(1);
    }

    // Proxies often rewrite Host, some of them to the upstream instance's address, so a retry
    // can arrive with a different value. That has to replay rather than fail with a 422.
    [Fact]
    public async Task Retry_with_a_different_host_header_replays_on_an_unconstrained_endpoint()
    {
        var client = await StartWithEndpointsAsync(endpoints => endpoints.MapPost("/pay", Handle));

        await client.SendAsync(Post("http://10.0.0.1:8080/pay"));
        var response = await client.SendAsync(Post("http://10.0.0.2:8080/pay"));

        response.Headers.Contains(Replayed).Should().BeTrue();
        _handled.Should().Be(1);
    }

    [Fact]
    public async Task Retry_through_another_host_allowed_by_the_same_endpoint_replays()
    {
        var client = await StartWithEndpointsAsync(endpoints =>
            endpoints.MapPost("/pay", Handle).RequireHost("*.example.com"));

        await client.SendAsync(Post("http://a.example.com/pay"));
        var response = await client.SendAsync(Post("http://b.example.com/pay"));

        response.Headers.Contains(Replayed).Should().BeTrue();
        _handled.Should().Be(1);
    }

    // Reordering or recasing the hosts in code mustn't turn a retry spanning a deploy into a 422.
    [Fact]
    public async Task Order_and_case_of_declared_hosts_do_not_change_the_fingerprint()
    {
        var first = await FingerprintWithHostsAsync("a.example", "b.example");
        var second = await FingerprintWithHostsAsync("B.example", "A.example");

        second.Should().Be(first);
    }

    [Fact]
    public async Task Declared_hosts_do_not_run_into_each_other()
    {
        var first = await FingerprintWithHostsAsync("a.example", "b.example");
        var second = await FingerprintWithHostsAsync("a.example,b.example");

        second.Should().NotBe(first);
    }

    private static async Task<string> FingerprintWithHostsAsync(params string[] hosts)
    {
        var builder = new DefaultFingerprintBuilder(Options.Create(new IdempotencyOptions()));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/pay";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new HostAttribute(hosts)),
            "test"));

        var result = await builder.ComputeAsync(context, null);
        return result.Fingerprint;
    }

    private Task Handle(HttpContext context)
    {
        Interlocked.Increment(ref _handled);
        context.Response.StatusCode = StatusCodes.Status201Created;
        return Task.CompletedTask;
    }

    private Task<HttpClient> StartWithEndpointsAsync(Action<IEndpointRouteBuilder> map) =>
        StartAsync(app =>
        {
            app.UseRouting();
            app.UseIdempotencyGuard();
            app.UseEndpoints(map);
        });

    private Task<HttpClient> StartWithBranchesAsync() =>
        StartAsync(app =>
        {
            foreach (var branchPath in new[] { "/public", "/internal" })
            {
                app.Map(branchPath, branch =>
                {
                    branch.UseRouting();
                    branch.UseIdempotencyGuard();
                    branch.UseEndpoints(endpoints => endpoints.MapPost("/pay", Handle));
                });
            }
        });

    private async Task<HttpClient> StartAsync(Action<IApplicationBuilder> pipeline)
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddIdempotencyGuard(options => options.Cleanup.Enabled = false);
                    services.AddIdempotencyGuardInMemoryStore();
                    services.AddRouting();
                });
                web.Configure(pipeline);
            })
            .StartAsync();
        return _host.GetTestClient();
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
}
