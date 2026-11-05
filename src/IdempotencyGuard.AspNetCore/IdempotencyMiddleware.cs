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
        var requestAborted = httpContext.RequestAborted;

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

        var keyPrefix = httpContext.Items.TryGetValue(_options.KeyPrefixItemKey, out var prefixObj)
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

        using var requestActivity = IdempotencyActivitySource.Source.StartActivity("idempotency.request", ActivityKind.Internal);
        requestActivity?.SetTag("idempotency.key", idempotencyKey);
        requestActivity?.SetTag("http.method", httpContext.Request.Method);
        requestActivity?.SetTag("http.route", httpContext.Request.Path.Value);

        var result = await _fingerprintBuilder.ComputeAsync(httpContext, idempotentAttr);

        var request = new GuardedRequest(
            idempotencyKey,
            result.Fingerprint,
            claimTtl,
            responseTtl);

        var claimResult = await TryClaimAsync(request, requestAborted);

        string resultTag;
        switch (claimResult)
        {
            case ClaimResult.Claimed claimed:
                resultTag = "claimed";
                IdempotencyMetrics.ClaimsTotal.Add(1);
                await HandleNewRequestAsync(httpContext, request, claimed.ClaimToken, requestAborted);
                break;

            case ClaimResult.Completed:
                resultTag = "replayed";
                IdempotencyMetrics.ReplaysTotal.Add(1);
                await ReplayResponseAsync(httpContext, request, requestAborted);
                break;

            case ClaimResult.AlreadyClaimed:
                resultTag = "conflict";
                IdempotencyMetrics.ConflictsTotal.Add(1);
                await HandleConcurrentRequestAsync(httpContext, request, requestAborted);
                break;

            case ClaimResult.FingerprintMismatch mismatch:
                resultTag = "fingerprint_mismatch";
                await WriteFingerprintMismatchAsync(httpContext, request, mismatch);
                break;

            default:
                resultTag = "unknown";
                break;
        }

        requestActivity?.SetTag("idempotency.result", resultTag);
    }

    private bool IsEnabled(HttpContext httpContext) => _options.Enabled && _options.EnforcedMethods.Contains(httpContext.Request.Method, StringComparer.OrdinalIgnoreCase);

    private async Task<ClaimResult> TryClaimAsync(GuardedRequest request, CancellationToken requestAborted)
    {
        ClaimResult claimResult;
        var claimTs = Stopwatch.GetTimestamp();
        using (var claimActivity = IdempotencyActivitySource.StartStoreActivity("claim", request.Key))
        {
            claimResult = await _store.TryClaimAsync(request.Key, request.Fingerprint, request.ClaimTtl, requestAborted);
            claimActivity?.SetTag("idempotency.claim.result", claimResult switch
            {
                ClaimResult.Claimed => "claimed",
                ClaimResult.Completed => "completed",
                ClaimResult.AlreadyClaimed => "already_claimed",
                ClaimResult.FingerprintMismatch => "fingerprint_mismatch",
                _ => "unknown"
            });
        }
        RecordStoreLatency(claimTs, "claim");
        return claimResult;
    }

    private async Task HandleNewRequestAsync(
        HttpContext httpContext,
        GuardedRequest request,
        string claimToken,
        CancellationToken requestAborted)
    {
        _logger.LogInformation("Idempotency key claimed: {Key}", request.Key);

        var context = new IdempotencyContext(request.Key, request.Fingerprint, false, _keyGenerator);
        httpContext.Items["IdempotencyContext"] = context;

        var originalBodyStream = httpContext.Response.Body;
        var claimSettled = false;

        try
        {
            using var responseBody = new MemoryStream();
            httpContext.Response.Body = responseBody;

            await _next(httpContext);

            responseBody.Position = 0;

            var statusCode = httpContext.Response.StatusCode;

            if (!responseBody.TryGetBuffer(out var responseBuffer)
                || responseBuffer.Count > _options.MaxResponseBodySize)
            {
                claimSettled = true;
                await ReleaseClaimAsync(request, claimToken);
                _logger.LogWarning(
                    "Response body for key {Key} exceeds MaxResponseBodySize ({MaxSize} bytes), claim released",
                    request.Key, _options.MaxResponseBodySize);
            }
            else
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
                    StatusCode = statusCode,
                    Headers = headers,
                    Body = responseBuffer.Count == responseBuffer.Array!.Length
                        ? responseBuffer.Array
                        : responseBuffer.AsSpan().ToArray()
                };

                bool stored;
                var setTs = Stopwatch.GetTimestamp();
                using (IdempotencyActivitySource.StartStoreActivity("set_response", request.Key))
                {
                    stored = await _store.SetResponseAsync(
                        request.Key, claimToken, idempotentResponse, request.ResponseTtl, requestAborted);
                }
                RecordStoreLatency(setTs, "set_response");
                claimSettled = true;

                if (stored)
                {
                    _logger.LogInformation(
                        "Idempotent response stored for key {Key}, status {StatusCode}",
                        request.Key, statusCode);
                }
                else
                {
                    IdempotencyMetrics.ClaimsLost.Add(1);
                    _logger.LogWarning(
                        "Response for key {Key} not stored: the handler outlived ClaimTtl ({ClaimTtl}) and the claim was lost. " +
                        "A retry may run the handler again; consider raising ClaimTtl",
                        request.Key, request.ClaimTtl);
                }
            }

            // Write to the original stream directly from the MemoryStream
            // internal buffer, avoiding an extra array allocation.
            responseBody.Position = 0;
            await responseBody.CopyToAsync(originalBodyStream, requestAborted);
        }
        catch (Exception ex)
        {
            if (!claimSettled)
            {
                await ReleaseClaimAsync(request, claimToken);
                _logger.LogWarning(ex, "Idempotency claim released after failure for key {Key}", request.Key);
            }

            throw;
        }
        finally
        {
            httpContext.Response.Body = originalBodyStream;
        }
    }

    private async Task ReleaseClaimAsync(GuardedRequest request, string claimToken)
    {
        var releaseTs = Stopwatch.GetTimestamp();
        using (IdempotencyActivitySource.StartStoreActivity("release", request.Key))
        {
            await _store.ReleaseClaimAsync(request.Key, claimToken, CancellationToken.None);
        }
        RecordStoreLatency(releaseTs, "release");
        IdempotencyMetrics.ClaimsReleased.Add(1);
    }

    private async Task<IdempotentResponse?> GetResponseAsync(GuardedRequest request, CancellationToken requestAborted)
    {
        IdempotentResponse? response;
        var getTs = Stopwatch.GetTimestamp();
        using (IdempotencyActivitySource.StartStoreActivity("get_response", request.Key))
        {
            response = await _store.GetResponseAsync(request.Key, requestAborted);
        }
        RecordStoreLatency(getTs, "get_response");
        return response;
    }

    private async Task ReplayResponseAsync(
        HttpContext httpContext,
        GuardedRequest request,
        CancellationToken requestAborted)
    {
        var response = await GetResponseAsync(request, requestAborted);

        if (response is null)
        {
            await _responseWriter.WriteErrorAsync(httpContext, 409, IdempotencyErrorKind.Conflict,
                "Request is being processed", request.Key);
            return;
        }

        _logger.LogInformation("Replaying idempotent response for key {Key}, original status {StatusCode}", request.Key, response.StatusCode);

        await _responseWriter.ReplayAsync(httpContext, response);
    }

    private async Task HandleConcurrentRequestAsync(
        HttpContext httpContext,
        GuardedRequest request,
        CancellationToken requestAborted)
    {
        if (_options.ConcurrentRequestPolicy == ConcurrentRequestPolicy.ReturnConflict)
        {
            await _responseWriter.WriteErrorAsync(httpContext, 409, IdempotencyErrorKind.Conflict,
                "Request with this idempotency key is currently being processed", request.Key);
            return;
        }

        var deadline = DateTime.UtcNow.Add(_options.ConcurrentRequestTimeout);
        var delay = TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(delay, requestAborted);
            }
            catch (OperationCanceledException) when (requestAborted.IsCancellationRequested)
            {
                return;
            }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 2000));

            var response = await GetResponseAsync(request, requestAborted);

            if (response is not null)
            {
                IdempotencyMetrics.ReplaysTotal.Add(1);
                _logger.LogInformation("Replaying idempotent response after wait for key {Key}", request.Key);

                await _responseWriter.ReplayAsync(httpContext, response);
                return;
            }
        }

        await _responseWriter.WriteErrorAsync(httpContext, 409, IdempotencyErrorKind.Timeout,
            "Timed out waiting for concurrent request to complete", request.Key);
    }

    private async Task WriteFingerprintMismatchAsync(
        HttpContext httpContext,
        GuardedRequest request,
        ClaimResult.FingerprintMismatch mismatch)
    {
        IdempotencyMetrics.FingerprintMismatchesTotal.Add(1);
        _logger.LogWarning(
            "Idempotency fingerprint mismatch for key {Key}. Expected: {Expected}, Received: {Received}",
            request.Key, mismatch.ExpectedFingerprint, mismatch.ActualFingerprint);
        await _responseWriter.WriteErrorAsync(httpContext, 422, IdempotencyErrorKind.FingerprintMismatch,
            "Idempotency key has already been used with a different request payload",
            request.Key);
    }

    private static void RecordStoreLatency(long startTimestamp, string operation)
    {
        IdempotencyMetrics.StoreLatency.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation));
    }

    private sealed record GuardedRequest(
        string Key,
        string Fingerprint,
        TimeSpan ClaimTtl,
        TimeSpan ResponseTtl);
}
