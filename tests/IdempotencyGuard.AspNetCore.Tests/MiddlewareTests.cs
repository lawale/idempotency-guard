using System.Net;
using System.Net.Http.Json;
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

namespace IdempotencyGuard.AspNetCore.Tests;

public class MiddlewareTests : IAsyncLifetime
{
    private IHost? _host;
    private HttpClient? _client;
    private int _handlerCallCount;

    public async Task InitializeAsync()
    {
        _handlerCallCount = 0;

        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddIdempotencyGuard(options =>
                    {
                        options.MissingKeyPolicy = MissingKeyPolicy.Allow;
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
                        endpoints.MapPost("/payments", async context =>
                        {
                            Interlocked.Increment(ref _handlerCallCount);
                            context.Response.StatusCode = 201;
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync("""{"id": "pay_123", "status": "created"}""");
                        });

                        endpoints.MapPost("/payments-selective", async context =>
                        {
                            Interlocked.Increment(ref _handlerCallCount);
                            context.Response.StatusCode = 201;
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync("""{"id": "pay_456", "status": "created"}""");
                        }).WithMetadata(new IdempotentAttribute
                        {
                            FingerprintProperties = ["Amount", "Currency"]
                        });

                        endpoints.MapPost("/payments-query", async context =>
                        {
                            Interlocked.Increment(ref _handlerCallCount);
                            context.Response.StatusCode = 201;
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync("""{"id": "pay_789", "status": "created"}""");
                        }).WithMetadata(new IdempotentAttribute
                        {
                            FingerprintQueryParameters = ["version"]
                        });

                        endpoints.MapPost("/merchants/{merchantId}/payments", async context =>
                        {
                            Interlocked.Increment(ref _handlerCallCount);
                            context.Response.StatusCode = 201;
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync("""{"id": "pay_route", "status": "created"}""");
                        }).WithMetadata(new IdempotentAttribute
                        {
                            FingerprintRouteValues = ["merchantId"]
                        });

                        endpoints.MapGet("/health", async context =>
                        {
                            context.Response.StatusCode = 200;
                            await context.Response.WriteAsync("ok");
                        });
                    });
                });
            })
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task First_request_with_key_passes_through_and_returns_response()
    {
        var request = CreatePaymentRequest("key-1", """{"amount": 100}""");

        var response = await _client!.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("pay_123");
        response.Headers.Contains("X-Idempotent-Replayed").Should().BeFalse();
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Duplicate_request_returns_cached_response_with_replay_header()
    {
        var request1 = CreatePaymentRequest("key-2", """{"amount": 100}""");
        await _client!.SendAsync(request1);

        var request2 = CreatePaymentRequest("key-2", """{"amount": 100}""");
        var response = await _client.SendAsync(request2);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("pay_123");
        response.Headers.GetValues("X-Idempotent-Replayed").Should().Contain("true");
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Same_key_with_different_payload_returns_422()
    {
        var request1 = CreatePaymentRequest("key-3", """{"amount": 100}""");
        await _client!.SendAsync(request1);

        var request2 = CreatePaymentRequest("key-3", """{"amount": 200}""");
        var response = await _client.SendAsync(request2);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Request_without_key_passes_through()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = new StringContent("""{"amount": 100}""", Encoding.UTF8, "application/json")
        };

        var response = await _client!.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_request_bypasses_idempotency()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");

        var response = await _client!.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Multiple_unique_keys_are_processed_independently()
    {
        var request1 = CreatePaymentRequest("key-a", """{"amount": 100}""");
        var request2 = CreatePaymentRequest("key-b", """{"amount": 200}""");

        await _client!.SendAsync(request1);
        await _client.SendAsync(request2);

        _handlerCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Selective_fingerprint_same_key_different_non_fingerprint_fields_replays()
    {
        var request1 = CreateRequest("/payments-selective", "key-fp-1",
            """{"amount": 100, "currency": "USD", "description": "first"}""");
        await _client!.SendAsync(request1);

        var request2 = CreateRequest("/payments-selective", "key-fp-1",
            """{"amount": 100, "currency": "USD", "description": "second"}""");
        var response = await _client.SendAsync(request2);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("X-Idempotent-Replayed").Should().Contain("true");
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Selective_fingerprint_same_key_different_fingerprint_fields_returns_422()
    {
        var request1 = CreateRequest("/payments-selective", "key-fp-2",
            """{"amount": 100, "currency": "USD", "description": "first"}""");
        await _client!.SendAsync(request1);

        var request2 = CreateRequest("/payments-selective", "key-fp-2",
            """{"amount": 200, "currency": "USD", "description": "first"}""");
        var response = await _client.SendAsync(request2);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Selective_fingerprint_case_insensitive_property_matching()
    {
        var request1 = CreateRequest("/payments-selective", "key-fp-3",
            """{"amount": 500, "currency": "GBP", "description": "A"}""");
        await _client!.SendAsync(request1);

        var request2 = CreateRequest("/payments-selective", "key-fp-3",
            """{"amount": 500, "currency": "GBP", "description": "B"}""");
        var response = await _client.SendAsync(request2);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("X-Idempotent-Replayed").Should().Contain("true");
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Query_parameter_fingerprint_same_key_different_query_value_returns_422()
    {
        var request1 = CreateRequest("/payments-query?version=1", "key-qp-1",
            """{"amount": 100}""");
        await _client!.SendAsync(request1);

        var request2 = CreateRequest("/payments-query?version=2", "key-qp-1",
            """{"amount": 100}""");
        var response = await _client.SendAsync(request2);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Query_parameter_fingerprint_same_key_same_query_value_replays()
    {
        var request1 = CreateRequest("/payments-query?version=1", "key-qp-2",
            """{"amount": 100}""");
        await _client!.SendAsync(request1);

        var request2 = CreateRequest("/payments-query?version=1", "key-qp-2",
            """{"amount": 100}""");
        var response = await _client.SendAsync(request2);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("X-Idempotent-Replayed").Should().Contain("true");
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Route_value_fingerprint_same_key_different_route_value_returns_422()
    {
        var request1 = CreateRequest("/merchants/42/payments", "key-rv-1",
            """{"amount": 100}""");
        await _client!.SendAsync(request1);

        var request2 = CreateRequest("/merchants/99/payments", "key-rv-1",
            """{"amount": 100}""");
        var response = await _client.SendAsync(request2);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _handlerCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Route_value_fingerprint_same_key_same_route_value_replays()
    {
        var request1 = CreateRequest("/merchants/42/payments", "key-rv-2",
            """{"amount": 100}""");
        await _client!.SendAsync(request1);

        var request2 = CreateRequest("/merchants/42/payments", "key-rv-2",
            """{"amount": 100}""");
        var response = await _client.SendAsync(request2);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.GetValues("X-Idempotent-Replayed").Should().Contain("true");
        _handlerCallCount.Should().Be(1);
    }

    private static HttpRequestMessage CreatePaymentRequest(string idempotencyKey, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static HttpRequestMessage CreateRequest(string path, string idempotencyKey, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }
}
