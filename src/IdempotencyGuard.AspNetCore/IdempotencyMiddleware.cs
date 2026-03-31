using System.Diagnostics;
using IdempotencyGuard.Diagnostics;
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
    private readonly IFingerprintBuilder _fingerprintBuilder;
    private readonly IdempotencyResponseWriter _responseWriter;

    public IdempotencyMiddleware(
        RequestDelegate next,
        IIdempotencyStore store,
        IDownstreamKeyGenerator keyGenerator,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyMiddleware> logger,
        IFingerprintBuilder fingerprintBuilder,
        IdempotencyResponseWriter responseWriter)
    {
        _next = next;
        _store = store;
        _keyGenerator = keyGenerator;
        _options = options.Value;
        _logger = logger;
        _fingerprintBuilder = fingerprintBuilder;
        _responseWriter = responseWriter;
    }

    public async Task InvokeAsync(HttpContext httpContext)
    {
        if (!IsEnabled(httpContext))
        {
            await _next(httpContext);
            return;
        }

        if (_options.EndpointFilter is not null)
        {
            var httpMethod = new HttpMethod(httpContext.Request.Method);
            var path = httpContext.Request.Path.Value ?? "/";
            if (!_options.EndpointFilter(httpMethod, path))
            {
                await _next(httpContext);
                return;
            }
        }

        // Read [Idempotent] attribute from endpoint metadata
        var endpoint = httpContext.GetEndpoint();
        var idempotentAttr = endpoint?.Metadata.GetMetadata<IdempotentAttribute>();

        var rawKey = httpContext.Request.Headers[_options.HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(rawKey))
        {
            // Per-endpoint Required override takes precedence, then fall back to global policy
            var requireKey = idempotentAttr?.Required ?? (_options.MissingKeyPolicy == MissingKeyPolicy.Reject);

            if (requireKey)
            {
                await _responseWriter.WriteErrorAsync(httpContext, 400, IdempotencyErrorKind.MissingKey,
                    $"Missing required header: {_options.HeaderName}");
                return;
            }

            await _next(httpContext);
            return;
        }

        var keyPrefix = httpContext.Items.TryGetValue("IdempotencyKeyPrefix", out var prefixObj)
                && prefixObj is string perRequestPrefix
            ? perRequestPrefix
            : _options.KeyPrefix;

        var idempotencyKey = string.IsNullOrEmpty(keyPrefix)
            ? rawKey
            : $"{keyPrefix}{rawKey}";

        // Per-endpoint TTL overrides
        var claimTtl = idempotentAttr?.ClaimTtlSeconds > 0
            ? TimeSpan.FromSeconds(idempotentAttr.ClaimTtlSeconds)
            : _options.ClaimTtl;

        var responseTtl = idempotentAttr?.ResponseTtlSeconds > 0
            ? TimeSpan.FromSeconds(idempotentAttr.ResponseTtlSeconds)
            : _options.ResponseTtl;

        IdempotencyMetrics.RequestsTotal.Add(1);

        var result = await _fingerprintBuilder.ComputeAsync(httpContext, idempotentAttr);

        var claimTs = Stopwatch.GetTimestamp();
        var claimResult = await _store.TryClaimAsync(idempotencyKey, result.Fingerprint, claimTtl);
        RecordStoreLatency(claimTs, "claim");

        switch (claimResult)
        {
            case ClaimResult.Claimed:
                IdempotencyMetrics.ClaimsTotal.Add(1);
                await HandleNewRequestAsync(httpContext, idempotencyKey, result.Fingerprint, result.RequestBody, responseTtl);
                break;

            case ClaimResult.Completed completed:
                IdempotencyMetrics.ReplaysTotal.Add(1);
                await ReplayResponseAsync(httpContext, idempotencyKey, completed.Entry);
                break;

            case ClaimResult.AlreadyClaimed:
                IdempotencyMetrics.ConflictsTotal.Add(1);
                await HandleConcurrentRequestAsync(httpContext, idempotencyKey);
                break;

            case ClaimResult.FingerprintMismatch mismatch:
                IdempotencyMetrics.FingerprintMismatchesTotal.Add(1);
                _logger.LogWarning(
                    "Idempotency fingerprint mismatch for key {Key}. Expected: {Expected}, Received: {Received}",
                    idempotencyKey, mismatch.ExpectedFingerprint, mismatch.ActualFingerprint);
                await _responseWriter.WriteErrorAsync(httpContext, 422, IdempotencyErrorKind.FingerprintMismatch,
                    "Idempotency key has already been used with a different request payload",
                    idempotencyKey);
                break;
        }
    }

    private bool IsEnabled(HttpContext httpContext) => _options.Enabled && _options.EnforcedMethods.Contains(httpContext.Request.Method, StringComparer.OrdinalIgnoreCase);

    private async Task HandleNewRequestAsync(HttpContext httpContext, string key, string fingerprint, byte[]? requestBody, TimeSpan responseTtl)
    {
        _logger.LogInformation("Idempotency key claimed: {Key}", key);

        var context = new IdempotencyContext(key, fingerprint, false, _keyGenerator);
        httpContext.Items["IdempotencyContext"] = context;

        var originalBodyStream = httpContext.Response.Body;
        var responseStored = false;

        try
        {
            using var responseBody = new MemoryStream();
            httpContext.Response.Body = responseBody;

            await _next(httpContext);

            responseBody.Position = 0;

            if (responseBody.TryGetBuffer(out var responseBuffer)
                && responseBuffer.Count <= _options.MaxResponseBodySize)
            {
                var headers = new Dictionary<string, string[]>();
                foreach (var header in httpContext.Response.Headers)
                {
                    if (HeaderFilter.ShouldStoreHeader(header.Key, _options))
                    {
                        headers[header.Key] = header.Value.ToArray()!;
                    }
                }

                var idempotentResponse = new IdempotentResponse
                {
                    StatusCode = httpContext.Response.StatusCode,
                    Headers = headers,
                    Body = responseBuffer.AsMemory()
                };

                var setTs = Stopwatch.GetTimestamp();
                await _store.SetResponseAsync(key, idempotentResponse, responseTtl);
                RecordStoreLatency(setTs, "set_response");
                responseStored = true;

                _logger.LogInformation(
                    "Idempotent response stored for key {Key}, status {StatusCode}",
                    key, httpContext.Response.StatusCode);
            }
            else
            {
                var releaseTs = Stopwatch.GetTimestamp();
                await _store.ReleaseClaimAsync(key);
                RecordStoreLatency(releaseTs, "release");
                IdempotencyMetrics.ClaimsReleased.Add(1);
                _logger.LogWarning(
                    "Response body for key {Key} exceeds MaxResponseBodySize ({MaxSize} bytes), claim released",
                    key, _options.MaxResponseBodySize);
            }

            // Write to the original stream directly from the MemoryStream
            // internal buffer, avoiding an extra array allocation.
            responseBody.Position = 0;
            await responseBody.CopyToAsync(originalBodyStream);
        }
        catch (Exception ex)
        {
            if (!responseStored)
            {
                var releaseTs = Stopwatch.GetTimestamp();
                await _store.ReleaseClaimAsync(key);
                RecordStoreLatency(releaseTs, "release");
                IdempotencyMetrics.ClaimsReleased.Add(1);
            }

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
        var getTs = Stopwatch.GetTimestamp();
        var response = await _store.GetResponseAsync(key);
        RecordStoreLatency(getTs, "get_response");

        if (response is null)
        {
            await _responseWriter.WriteErrorAsync(httpContext, 409, IdempotencyErrorKind.Conflict,
                "Request is being processed", key);
            return;
        }

        _logger.LogInformation("Replaying idempotent response for key {Key}, original status {StatusCode}", key, response.StatusCode);

        await _responseWriter.ReplayAsync(httpContext, response);
    }

    private async Task HandleConcurrentRequestAsync(HttpContext httpContext, string key)
    {
        if (_options.ConcurrentRequestPolicy == ConcurrentRequestPolicy.ReturnConflict)
        {
            await _responseWriter.WriteErrorAsync(httpContext, 409, IdempotencyErrorKind.Conflict,
                "Request with this idempotency key is currently being processed", key);
            return;
        }

        var deadline = DateTime.UtcNow.Add(_options.ConcurrentRequestTimeout);
        var delay = TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(delay);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 2000));

            var pollTs = Stopwatch.GetTimestamp();
            var response = await _store.GetResponseAsync(key);
            RecordStoreLatency(pollTs, "get_response");

            if (response is not null)
            {
                IdempotencyMetrics.ReplaysTotal.Add(1);
                _logger.LogInformation("Replaying idempotent response after wait for key {Key}", key);

                await _responseWriter.ReplayAsync(httpContext, response);
                return;
            }
        }

        await _responseWriter.WriteErrorAsync(httpContext, 409, IdempotencyErrorKind.Timeout,
            "Timed out waiting for concurrent request to complete", key);
    }

    private static void RecordStoreLatency(long startTimestamp, string operation)
    {
        IdempotencyMetrics.StoreLatency.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation));
    }
}
