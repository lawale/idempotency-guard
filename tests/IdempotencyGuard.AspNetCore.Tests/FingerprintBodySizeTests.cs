using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IdempotencyGuard.AspNetCore.Tests;

public sealed class FingerprintBodySizeTests : IAsyncLifetime
{
    private const string ReplayedHeader = "X-Idempotent-Replayed";

    private IHost? _host;
    private CountingStream? _body;
    private long _bytesReadBeforeHandler;

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
    public async Task Zero_leaves_the_body_unread_for_the_handler()
    {
        var client = await StartAsync(maxBodySize: 0);

        var response = await client.SendAsync(Post("key", "payload"));

        _bytesReadBeforeHandler.Should().Be(0);
        (await response.Content.ReadAsStringAsync()).Should().Be("payload");
    }

    [Fact]
    public async Task Zero_replays_a_retry_with_a_different_body()
    {
        var client = await StartAsync(maxBodySize: 0);

        await client.SendAsync(Post("key", "first"));
        var retry = await client.SendAsync(Post("key", "second"));

        retry.Headers.Contains(ReplayedHeader).Should().BeTrue();
        (await retry.Content.ReadAsStringAsync()).Should().Be("first");
    }

    [Fact]
    public async Task Cap_reads_only_that_many_bytes_before_the_handler()
    {
        var client = await StartAsync(maxBodySize: 4);

        var response = await client.SendAsync(Post("key", new string('x', 100_000)));

        _bytesReadBeforeHandler.Should().Be(4);
        (await response.Content.ReadAsStringAsync()).Should().HaveLength(100_000);
    }

    [Theory]
    [InlineData("aaaa-tail", "aaaa-other", HttpStatusCode.OK)]
    [InlineData("aaaa-tail", "bbbb-tail", HttpStatusCode.UnprocessableEntity)]
    public async Task Cap_compares_only_the_first_bytes(string first, string retry, HttpStatusCode expected)
    {
        var client = await StartAsync(maxBodySize: 4);

        await client.SendAsync(Post("key", first));
        var response = await client.SendAsync(Post("key", retry));

        response.StatusCode.Should().Be(expected);
    }

    private async Task<HttpClient> StartAsync(int maxBodySize)
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
                        options.MaxFingerprintBodySize = maxBodySize;
                    });
                    services.AddIdempotencyGuardInMemoryStore();
                    services.AddRouting();
                });
                webBuilder.Configure(app =>
                {
                    app.Use((context, next) =>
                    {
                        context.Request.Body = _body = new CountingStream(context.Request.Body);
                        return next(context);
                    });
                    app.UseRouting();
                    app.UseIdempotencyGuard();
                    app.UseEndpoints(endpoints => endpoints.MapPost("/payments", async context =>
                    {
                        _bytesReadBeforeHandler = _body!.BytesRead;
                        using var reader = new StreamReader(context.Request.Body);
                        await context.Response.WriteAsync(await reader.ReadToEndAsync());
                    }));
                });
            })
            .StartAsync();

        return _host.GetTestClient();
    }

    private static HttpRequestMessage Post(string key, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
