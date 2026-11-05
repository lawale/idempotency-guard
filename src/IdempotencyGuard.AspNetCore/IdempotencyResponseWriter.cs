using System.Globalization;
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
    private const string NotReplayableHeader = "X-Idempotent-Not-Replayable";
    private const string OriginalStatusHeader = "X-Idempotent-Original-Status";

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
    /// Builds what's stored in place of a response too large to store. It's an ordinary stored
    /// response, so it needs nothing from the store, and a store or an older instance that
    /// doesn't understand it still serves a 410. The body stays neutral because stores only
    /// ever see digests of client keys, so <see cref="IdempotencyOptions.ErrorResponseFactory"/>
    /// runs at replay rather than here.
    /// </summary>
    internal static IdempotentResponse CreateNotReplayableMarker(int originalStatusCode) => new()
    {
        StatusCode = StatusCodes.Status410Gone,
        Headers = new Dictionary<string, string[]>
        {
            ["Content-Type"] = ["application/json"],
            [NotReplayableHeader] = ["true"],
            [OriginalStatusHeader] = [originalStatusCode.ToString(CultureInfo.InvariantCulture)]
        },
        Body = JsonSerializer.SerializeToUtf8Bytes(new { error = NotReplayableMessage(originalStatusCode) })
    };

    /// <summary>
    /// Whether a header belongs to the marker. A handler's copy isn't stored, so a
    /// response can't pass itself off as one.
    /// </summary>
    internal static bool IsMarkerHeader(string headerName) =>
        string.Equals(headerName, NotReplayableHeader, StringComparison.OrdinalIgnoreCase)
        || string.Equals(headerName, OriginalStatusHeader, StringComparison.OrdinalIgnoreCase);

    internal static bool TryGetNotReplayableStatus(IdempotentResponse response, out int originalStatusCode)
    {
        originalStatusCode = 0;
        var isMarker = false;

        foreach (var header in response.Headers)
        {
            if (string.Equals(header.Key, NotReplayableHeader, StringComparison.OrdinalIgnoreCase))
            {
                isMarker = true;
            }
            else if (string.Equals(header.Key, OriginalStatusHeader, StringComparison.OrdinalIgnoreCase)
                     && header.Value is [var value])
            {
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out originalStatusCode);
            }
        }

        return isMarker;
    }

    /// <summary>
    /// Tells a retry that the original request completed, but its response couldn't be stored.
    /// </summary>
    internal Task WriteNotReplayableAsync(HttpContext httpContext, int originalStatusCode, string idempotencyKey)
    {
        httpContext.Response.Headers[_options.ReplayedHeaderName] = "true";
        httpContext.Response.Headers[OriginalStatusHeader] = originalStatusCode.ToString(CultureInfo.InvariantCulture);

        return WriteErrorAsync(
            httpContext,
            StatusCodes.Status410Gone,
            IdempotencyErrorKind.ResponseNotReplayable,
            NotReplayableMessage(originalStatusCode),
            idempotencyKey,
            originalStatusCode);
    }

    private static string NotReplayableMessage(int originalStatusCode) =>
        $"The original request completed with status {originalStatusCode}, but its response was too large to store, so it can't be replayed";

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
        string? idempotencyKey = null,
        int? originalStatusCode = null)
    {
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";

        var problem = new IdempotencyProblem
        {
            StatusCode = statusCode,
            Kind = kind,
            Message = message,
            IdempotencyKey = idempotencyKey,
            OriginalStatusCode = originalStatusCode
        };

        var body = _options.ErrorResponseFactory is not null
            ? _options.ErrorResponseFactory(problem)
            : new { error = message };

        var json = JsonSerializer.SerializeToUtf8Bytes(body);
        await httpContext.Response.Body.WriteAsync(json, httpContext.RequestAborted);
    }
}
