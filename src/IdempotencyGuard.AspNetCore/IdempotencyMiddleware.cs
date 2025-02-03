using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore;

public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IIdempotencyStore _store;
    private readonly IDownstreamKeyGenerator _keyGenerator;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(
        RequestDelegate next,
        IIdempotencyStore store,
        IDownstreamKeyGenerator keyGenerator,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _store = store;
        _keyGenerator = keyGenerator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext httpContext)
    {
        if (!_options.Enabled)
        {
            await _next(httpContext);
            return;
        }

        if (!_options.EnforcedMethods.Contains(httpContext.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            await _next(httpContext);
            return;
        }

        var idempotencyKey = httpContext.Request.Headers[_options.HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            if (_options.MissingKeyPolicy == MissingKeyPolicy.Reject)
            {
                httpContext.Response.StatusCode = 400;
                await httpContext.Response.WriteAsJsonAsync(new { error = $"Missing required header: {_options.HeaderName}" });
                return;
            }

            await _next(httpContext);
            return;
        }

        var requestBody = await ReadRequestBodyAsync(httpContext.Request);
        var fingerprint = RequestFingerprint.Compute(
            httpContext.Request.Method,
            httpContext.Request.Path.Value ?? "/",
            requestBody);

        var claimResult = await _store.TryClaimAsync(idempotencyKey, fingerprint, _options.ClaimTtl);

        switch (claimResult)
        {
            case ClaimResult.Claimed:
                await HandleNewRequestAsync(httpContext, idempotencyKey, fingerprint, requestBody);
                break;

            case ClaimResult.Completed completed:
                await ReplayResponseAsync(httpContext, idempotencyKey, completed.Entry);
                break;

            case ClaimResult.AlreadyClaimed:
                await HandleConcurrentRequestAsync(httpContext, idempotencyKey, fingerprint);
                break;

            case ClaimResult.FingerprintMismatch mismatch:
                _logger.LogWarning(
                    "Idempotency fingerprint mismatch for key {Key}. Expected: {Expected}, Received: {Received}",
                    idempotencyKey, mismatch.ExpectedFingerprint, mismatch.ActualFingerprint);
                httpContext.Response.StatusCode = 422;
                await httpContext.Response.WriteAsJsonAsync(new
                {
                    error = "Idempotency key has already been used with a different request payload"
                });
                break;
        }
    }

    private async Task HandleNewRequestAsync(HttpContext httpContext, string key, string fingerprint, byte[]? requestBody)
    {
        _logger.LogInformation("Idempotency key claimed: {Key}", key);

        var context = new IdempotencyContext(key, fingerprint, false, _keyGenerator);
        httpContext.Items["IdempotencyContext"] = context;

        var originalBodyStream = httpContext.Response.Body;

        try
        {
            using var responseBody = new MemoryStream();
            httpContext.Response.Body = responseBody;

            await _next(httpContext);

            responseBody.Seek(0, SeekOrigin.Begin);
            var responseBytes = responseBody.ToArray();

            if (responseBytes.Length <= _options.MaxResponseBodySize)
            {
                var headers = new Dictionary<string, string[]>();
                foreach (var header in httpContext.Response.Headers)
                {
                    headers[header.Key] = header.Value.ToArray()!;
                }

                var idempotentResponse = new IdempotentResponse
                {
                    StatusCode = httpContext.Response.StatusCode,
                    Headers = headers,
                    Body = responseBytes
                };

                await _store.SetResponseAsync(key, idempotentResponse, _options.ResponseTtl);

                _logger.LogInformation(
                    "Idempotent response stored for key {Key}, status {StatusCode}",
                    key, httpContext.Response.StatusCode);
            }

            responseBody.Seek(0, SeekOrigin.Begin);
            await responseBody.CopyToAsync(originalBodyStream);
        }
        catch (Exception ex)
        {
            await _store.ReleaseClaimAsync(key);
            _logger.LogWarning(ex, "Idempotency claim released after failure for key {Key}", key);
            throw;
        }
        finally
        {
            httpContext.Response.Body = originalBodyStream;
        }
    }

    private async Task ReplayResponseAsync(HttpContext httpContext, string key, IdempotencyEntry entry)
    {
        var response = await _store.GetResponseAsync(key);

        if (response is null)
        {
            httpContext.Response.StatusCode = 409;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Request is being processed" });
            return;
        }

        _logger.LogInformation("Replaying idempotent response for key {Key}, original status {StatusCode}", key, response.StatusCode);

        httpContext.Response.StatusCode = response.StatusCode;

        foreach (var header in response.Headers)
        {
            httpContext.Response.Headers[header.Key] = header.Value;
        }

        httpContext.Response.Headers["X-Idempotent-Replayed"] = "true";

        await httpContext.Response.Body.WriteAsync(response.Body);
    }

    private async Task HandleConcurrentRequestAsync(HttpContext httpContext, string key, string fingerprint)
    {
        if (_options.ConcurrentRequestPolicy == ConcurrentRequestPolicy.ReturnConflict)
        {
            httpContext.Response.StatusCode = 409;
            await httpContext.Response.WriteAsJsonAsync(new { error = "Request with this idempotency key is currently being processed" });
            return;
        }

        var deadline = DateTime.UtcNow.Add(_options.ConcurrentRequestTimeout);
        var delay = TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 2000));

            var response = await _store.GetResponseAsync(key);
            if (response is not null)
            {
                _logger.LogInformation("Replaying idempotent response after wait for key {Key}", key);

                httpContext.Response.StatusCode = response.StatusCode;
                foreach (var header in response.Headers)
                {
                    httpContext.Response.Headers[header.Key] = header.Value;
                }
                httpContext.Response.Headers["X-Idempotent-Replayed"] = "true";

                await httpContext.Response.Body.WriteAsync(response.Body);
                return;
            }
        }

        httpContext.Response.StatusCode = 409;
        await httpContext.Response.WriteAsJsonAsync(new { error = "Timed out waiting for concurrent request to complete" });
    }

    private static async Task<byte[]?> ReadRequestBodyAsync(HttpRequest request)
    {
        if (!request.Body.CanSeek)
        {
            request.EnableBuffering();
        }

        using var memoryStream = new MemoryStream();
        request.Body.Position = 0;
        await request.Body.CopyToAsync(memoryStream);
        request.Body.Position = 0;

        var bytes = memoryStream.ToArray();
        return bytes.Length > 0 ? bytes : null;
    }
}
