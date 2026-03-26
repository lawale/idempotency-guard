using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore.Tests;

public class ResponseWriterTests
{
    [Fact]
    public async Task ReplayAsync_uses_default_replayed_header_name()
    {
        var writer = CreateWriter(new IdempotencyOptions());
        var httpContext = new DefaultHttpContext();
        var response = CreateResponse();

        await writer.ReplayAsync(httpContext, response);

        httpContext.Response.Headers["X-Idempotent-Replayed"].ToString().Should().Be("true");
    }

    [Fact]
    public async Task ReplayAsync_uses_custom_replayed_header_name()
    {
        var writer = CreateWriter(new IdempotencyOptions { ReplayedHeaderName = "X-Custom-Replay" });
        var httpContext = new DefaultHttpContext();
        var response = CreateResponse();

        await writer.ReplayAsync(httpContext, response);

        httpContext.Response.Headers["X-Custom-Replay"].ToString().Should().Be("true");
        httpContext.Response.Headers.ContainsKey("X-Idempotent-Replayed").Should().BeFalse();
    }

    private static IdempotencyResponseWriter CreateWriter(IdempotencyOptions options) =>
        new(Options.Create(options));

    private static IdempotentResponse CreateResponse() => new()
    {
        StatusCode = 201,
        Headers = new Dictionary<string, string[]>
        {
            ["Content-Type"] = ["application/json"]
        },
        Body = ReadOnlyMemory<byte>.Empty
    };
}
