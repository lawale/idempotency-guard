using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore;

/// <summary>
/// Writes idempotency-related HTTP responses: cached response replay
/// and structured error responses. Centralises the response-writing
/// logic that was previously duplicated in the middleware.
/// </summary>
public class IdempotencyResponseWriter
{
    private readonly IdempotencyOptions _options;

    public IdempotencyResponseWriter(IOptions<IdempotencyOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    /// Writes a cached <see cref="IdempotentResponse"/> back to the client,
    /// including the original status code, stored headers, the configured
    /// <see cref="IdempotencyOptions.ReplayedHeaderName"/> marker, and the response body bytes.
    /// </summary>
    public async Task ReplayAsync(HttpContext httpContext, IdempotentResponse response)
    {
        httpContext.Response.StatusCode = response.StatusCode;

        foreach (var header in response.Headers)
        {
            httpContext.Response.Headers[header.Key] = header.Value;
        }

        httpContext.Response.Headers[_options.ReplayedHeaderName] = "true";

        await httpContext.Response.Body.WriteAsync(response.Body);
    }

    /// <summary>
    /// Writes a structured error response. Uses the configured
    /// <see cref="IdempotencyOptions.ErrorResponseFactory"/> if set,
    /// otherwise writes the default <c>{"error":"..."}</c> JSON format.
    /// </summary>
    public async Task WriteErrorAsync(
        HttpContext httpContext,
        int statusCode,
        IdempotencyErrorKind kind,
        string message,
        string? idempotencyKey = null)
    {
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";

        var problem = new IdempotencyProblem
        {
            StatusCode = statusCode,
            Kind = kind,
            Message = message,
            IdempotencyKey = idempotencyKey
        };

        var body = _options.ErrorResponseFactory is not null
            ? _options.ErrorResponseFactory(problem)
            : new { error = message };

        var json = JsonSerializer.SerializeToUtf8Bytes(body);
        await httpContext.Response.Body.WriteAsync(json, httpContext.RequestAborted);
    }
}
