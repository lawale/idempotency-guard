using System.Diagnostics;
using IdempotencyGuard.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore;

public class IdempotencyMiddleware
{
    // AuthorizationMiddleware sets this item when it runs. EndpointMiddleware reads the same
    // item to detect endpoints that have authorization metadata but were never authorized.
    private const string AuthorizationInvokedItemKey = "__AuthorizationMiddlewareWithEndpointInvoked";

    private const string ClaimedResult = "claimed";
    private const string ReplayedResult = "replayed";
    private const string ConflictResult = "conflict";
    private const string FingerprintMismatchResult = "fingerprint_mismatch";
    private const string NotReplayableResult = "not_replayable";

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

        var endpoint = httpContext.GetEndpoint();

        // Read [Idempotent] attributes from endpoint metadata
        var idempotentAttr = endpoint is null
            ? null
            : IdempotentAttribute.Merge(endpoint.Metadata.GetOrderedMetadata<IdempotentAttribute>());

        if (_options.UnmarkedEndpoints == UnmarkedEndpointBehavior.Ignore && idempotentAttr is null)
        {
            await _next(httpContext);
            return;
        }

        ThrowIfAuthorizationHasNotRun(httpContext, endpoint);

        var policy = ResolvePolicy(idempotentAttr);

        var rawKey = httpContext.Request.Headers[_options.HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(rawKey))
        {
            // A marked endpoint's Required decides, and only unmarked endpoints use the global setting
            var requireKey = idempotentAttr?.Required ?? (_options.UnmarkedEndpoints == UnmarkedEndpointBehavior.RequireKey);

            if (requireKey)
            {
                await _responseWriter.WriteErrorAsync(httpContext, 400, IdempotencyErrorKind.MissingKey,
                    $"Missing required header: {_options.HeaderName}");
                return;
            }

            await _next(httpContext);
            return;
        }

        if (!IsValidKey(rawKey))
        {
            // The key is left out of the error: it's client input that failed validation.
            await _responseWriter.WriteErrorAsync(httpContext, 400, IdempotencyErrorKind.InvalidKey,
                $"{_options.HeaderName} must not contain control characters or exceed {_options.MaxKeyLength} characters");
            return;
        }

        if (!TryResolveScopeSegment(httpContext, idempotentAttr, policy, out var scopeSegment))
        {
            await _responseWriter.WriteErrorAsync(httpContext, 403, IdempotencyErrorKind.MissingScope,
                "Unable to determine the caller this idempotency key belongs to", rawKey);
            return;
        }

        var keyPrefix = httpContext.Items.TryGetValue(_options.KeyPrefixItemKey, out var prefixObj)
                && prefixObj is string perRequestPrefix
            ? perRequestPrefix
            : _options.KeyPrefix;

        if (keyPrefix?.Length > IdempotencyStoreKey.MaxPrefixLength)
        {
            throw new InvalidOperationException(
                $"The idempotency key prefix for this request is {keyPrefix.Length} characters, " +
                $"over the limit of {IdempotencyStoreKey.MaxPrefixLength}.");
        }

        // The key the application sees, which downstream keys derive from. It's separate from
        // the store key so that the storage format can't change what a payment provider receives.
        var idempotencyKey = string.Concat(keyPrefix, scopeSegment, rawKey);

        // The digest also identifies the request in logs and spans, so a log line leads
        // straight to its row, and the raw key can't be recovered from either.
        var keyHash = IdempotencyStoreKey.Digest(scopeSegment, rawKey);
        var storeKey = string.Concat(keyPrefix, keyHash);

        // Per-endpoint TTL overrides
        var claimTtl = idempotentAttr?.ClaimTtlSeconds > 0
            ? TimeSpan.FromSeconds(idempotentAttr.ClaimTtlSeconds)
            : _options.ClaimTtl;

        var responseTtl = idempotentAttr?.ResponseTtlSeconds > 0
            ? TimeSpan.FromSeconds(idempotentAttr.ResponseTtlSeconds)
            : _options.ResponseTtl;

        IdempotencyMetrics.RequestsTotal.Add(1);

        using var requestActivity = IdempotencyActivitySource.Source.StartActivity("idempotency.request", ActivityKind.Internal);
        requestActivity?.SetTag("idempotency.key_hash", keyHash);
        requestActivity?.SetTag("http.method", httpContext.Request.Method);
        requestActivity?.SetTag("http.route", httpContext.Request.Path.Value);

        var result = await _fingerprintBuilder.ComputeAsync(httpContext, idempotentAttr);

        var request = new GuardedRequest(
            rawKey,
            idempotencyKey,
            storeKey,
            keyHash,
            result.Fingerprint,
            claimTtl,
            responseTtl,
            policy?.ShouldStoreResponse ?? _options.ShouldStoreResponse,
            policy?.OversizedResponse ?? _options.OversizedResponse);

        var claimResult = await TryClaimAsync(request, requestAborted);

        string resultTag;
        switch (claimResult)
        {
            case ClaimResult.Claimed claimed:
                resultTag = ClaimedResult;
                IdempotencyMetrics.ClaimsTotal.Add(1);
                await HandleNewRequestAsync(httpContext, request, claimed.ClaimToken, requestAborted);
                break;

            case ClaimResult.Completed:
                resultTag = await ReplayResponseAsync(httpContext, request, requestAborted);
                break;

            case ClaimResult.AlreadyClaimed:
                resultTag = await HandleConcurrentRequestAsync(httpContext, request, requestAborted);
                break;

            case ClaimResult.FingerprintMismatch mismatch:
                resultTag = FingerprintMismatchResult;
                await WriteFingerprintMismatchAsync(httpContext, request, mismatch);
                break;

            default:
                resultTag = "unknown";
                break;
        }

        requestActivity?.SetTag("idempotency.result", resultTag);
    }

#pragma warning disable IDEMP001 // Still honoured until it's removed in 3.0
    private bool IsEnabled(HttpContext httpContext) => _options.Enabled && _options.EnforcedMethods.Contains(httpContext.Request.Method, StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDEMP001

    private bool IsValidKey(string rawKey) =>
        (_options.MaxKeyLength <= 0 || rawKey.Length <= _options.MaxKeyLength)
        && !rawKey.Any(char.IsControl);

    /// <summary>
    /// A guard that runs before authorization replays stored responses to callers who would
    /// have been rejected, and stores their 401s where the rightful caller's retry finds them.
    /// </summary>
    private void ThrowIfAuthorizationHasNotRun(HttpContext httpContext, Endpoint? endpoint)
    {
        if (endpoint is null
            || _options.SuppressAuthorizationOrderCheck
            || httpContext.Items.ContainsKey(AuthorizationInvokedItemKey)
            || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return;
        }

        var requiresAuthorization = endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null
            || endpoint.Metadata.GetMetadata<AuthorizationPolicy>() is not null
            || endpoint.Metadata.GetMetadata<IAuthorizationRequirementData>() is not null;

        if (requiresAuthorization)
        {
            throw new InvalidOperationException(
                $"Endpoint '{endpoint.DisplayName}' requires authorization, but UseIdempotencyGuard() ran before it. " +
                "A guard that runs first serves stored responses to unauthorized callers. " +
                "Call UseAuthentication() and UseAuthorization() before UseIdempotencyGuard().");
        }
    }

    private IdempotencyPolicy? ResolvePolicy(IdempotentAttribute? attribute)
    {
        if (attribute?.Policy is not { } name)
        {
            return null;
        }

        return _options.TryGetPolicy(name, out var policy)
            ? policy
            : throw new InvalidOperationException(
                $"Idempotency policy '{name}' is not registered. Register it with IdempotencyOptions.AddPolicy.");
    }

    /// <summary>
    /// Resolves the part of the key that isolates one caller from another.
    /// </summary>
    /// <returns><c>false</c> if the resolver could not determine a scope.</returns>
    private bool TryResolveScopeSegment(
        HttpContext httpContext,
        IdempotentAttribute? attribute,
        IdempotencyPolicy? policy,
        out string? segment)
    {
        segment = null;

        var resolver = policy?.KeyScope ?? _options.KeyScope;
        if (resolver is null)
        {
            return true;
        }

        var scope = resolver(httpContext);
        if (scope is null)
        {
            return false;
        }

        segment = IdempotencyStoreKey.ScopeSegment(scope, policy?.KeyScope is not null ? attribute!.Policy : null);
        return true;
    }

    private async Task<ClaimResult> TryClaimAsync(GuardedRequest request, CancellationToken requestAborted)
    {
        ClaimResult claimResult;
        var claimTs = Stopwatch.GetTimestamp();
        using (var claimActivity = IdempotencyActivitySource.StartStoreActivity("claim", request.KeyHash))
        {
            claimResult = await _store.TryClaimAsync(request.StoreKey, request.Fingerprint, request.ClaimTtl, requestAborted);
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
        _logger.LogInformation("Idempotency key claimed: {KeyHash}", request.KeyHash);

        var context = new IdempotencyContext(request.Key, request.Fingerprint, false, _keyGenerator);
        httpContext.Items["IdempotencyContext"] = context;

        var originalBodyStream = httpContext.Response.Body;
        var claimSettled = false;

        try
        {
            using var responseBody = new MemoryStream();
            httpContext.Response.Body = responseBody;
            var bodyFeature = httpContext.Features.Get<IHttpResponseBodyFeature>()!;

            await _next(httpContext);

            // Bytes still in BodyWriter would be lost when Response.Body is restored, and the
            // truncated response stored. Unlike a flush, this doesn't create a writer if the
            // handler never used one.
            await bodyFeature.CompleteAsync();

            responseBody.Position = 0;

            var statusCode = httpContext.Response.StatusCode;

            var oversized = !responseBody.TryGetBuffer(out var responseBuffer)
                || responseBuffer.Count > _options.MaxResponseBodySize;

            // Checked first: an oversized response that retries are meant to rerun, such as a
            // 500, shouldn't be turned away for its size.
            if (request.ShouldStoreResponse?.Invoke(statusCode) == false)
            {
                claimSettled = true;
                await ReleaseClaimAsync(request, claimToken, "should_store_response");
                _logger.LogInformation(
                    "Response with status {StatusCode} for key {KeyHash} not stored, claim released",
                    statusCode, request.KeyHash);
            }
            else if (oversized && request.OversizedResponse == OversizedResponseBehavior.RunHandlerAgain)
            {
                IdempotencyMetrics.OversizedResponses.Add(1, new KeyValuePair<string, object?>("behavior", "run_handler_again"));
                claimSettled = true;
                await ReleaseClaimAsync(request, claimToken, "response_too_large");
                _logger.LogWarning(
                    "Response body for key {KeyHash} exceeds MaxResponseBodySize ({MaxSize} bytes), claim released",
                    request.KeyHash, _options.MaxResponseBodySize);
            }
            else
            {
                IdempotentResponse idempotentResponse;
                if (oversized)
                {
                    IdempotencyMetrics.OversizedResponses.Add(1, new KeyValuePair<string, object?>("behavior", "reject_retries"));
                    _logger.LogWarning(
                        "Response body for key {KeyHash} exceeds MaxResponseBodySize ({MaxSize} bytes), retries will get a 410",
                        request.KeyHash, _options.MaxResponseBodySize);
                    idempotentResponse = IdempotencyResponseWriter.CreateNotReplayableMarker(statusCode);
                }
                else
                {
                    var headers = new Dictionary<string, string[]>();
                    foreach (var header in httpContext.Response.Headers)
                    {
                        if (HeaderFilter.ShouldStoreHeader(header.Key, _options)
                            && !IdempotencyResponseWriter.IsMarkerHeader(header.Key))
                        {
                            headers[header.Key] = header.Value.ToArray()!;
                        }
                    }

                    idempotentResponse = new IdempotentResponse
                    {
                        StatusCode = statusCode,
                        Headers = headers,
                        Body = responseBuffer.Count == responseBuffer.Array!.Length
                            ? responseBuffer.Array
                            : responseBuffer.AsSpan().ToArray()
                    };
                }

                // Deliberately not cancellable. The handler's side effects have already happened,
                // so a client hanging up now must not lose the response that makes retries safe.
                bool stored;
                var setTs = Stopwatch.GetTimestamp();
                using (IdempotencyActivitySource.StartStoreActivity("set_response", request.KeyHash))
                {
                    stored = await _store.SetResponseAsync(
                        request.StoreKey, claimToken, idempotentResponse, request.ResponseTtl, CancellationToken.None);
                }
                RecordStoreLatency(setTs, "set_response");
                claimSettled = true;

                if (stored)
                {
                    _logger.LogInformation(
                        "Idempotent response stored for key {KeyHash}, status {StatusCode}",
                        request.KeyHash, idempotentResponse.StatusCode);
                }
                else
                {
                    IdempotencyMetrics.ClaimsLost.Add(1);
                    _logger.LogWarning(
                        "Response for key {KeyHash} not stored: the handler outlived ClaimTtl ({ClaimTtl}) and the claim was lost. " +
                        "A retry may run the handler again; consider raising ClaimTtl",
                        request.KeyHash, request.ClaimTtl);
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
                await ReleaseClaimAsync(request, claimToken, "failure");
                _logger.LogWarning(ex, "Idempotency claim released after failure for key {KeyHash}", request.KeyHash);
            }

            throw;
        }
        finally
        {
            httpContext.Response.Body = originalBodyStream;
        }
    }

    private async Task ReleaseClaimAsync(GuardedRequest request, string claimToken, string reason)
    {
        bool released;
        var releaseTs = Stopwatch.GetTimestamp();
        using (IdempotencyActivitySource.StartStoreActivity("release", request.KeyHash))
        {
            released = await _store.ReleaseClaimAsync(request.StoreKey, claimToken, CancellationToken.None);
        }
        RecordStoreLatency(releaseTs, "release");

        if (released)
        {
            IdempotencyMetrics.ClaimsReleased.Add(1, new KeyValuePair<string, object?>("reason", reason));
        }
    }

    private async Task<IdempotentResponse?> GetResponseAsync(GuardedRequest request, CancellationToken requestAborted)
    {
        IdempotentResponse? response;
        var getTs = Stopwatch.GetTimestamp();
        using (IdempotencyActivitySource.StartStoreActivity("get_response", request.KeyHash))
        {
            response = await _store.GetResponseAsync(request.StoreKey, requestAborted);
        }
        RecordStoreLatency(getTs, "get_response");
        return response;
    }

    private async Task<string> ReplayResponseAsync(
        HttpContext httpContext,
        GuardedRequest request,
        CancellationToken requestAborted)
    {
        var response = await GetResponseAsync(request, requestAborted);

        if (response is null)
        {
            IdempotencyMetrics.ConflictsTotal.Add(1);
            await _responseWriter.WriteErrorAsync(httpContext, 409, IdempotencyErrorKind.Conflict,
                "Request is being processed", request.RawKey);
            return ConflictResult;
        }

        return await WriteStoredResponseAsync(httpContext, request, response);
    }

    private async Task<string> WriteStoredResponseAsync(
        HttpContext httpContext,
        GuardedRequest request,
        IdempotentResponse response)
    {
        if (IdempotencyResponseWriter.TryGetNotReplayableStatus(response, out var originalStatusCode))
        {
            IdempotencyMetrics.ReplaysRejected.Add(1);
            _logger.LogInformation(
                "Rejecting retry for key {KeyHash}: the original response, status {StatusCode}, was too large to store",
                request.KeyHash, originalStatusCode);

            await _responseWriter.WriteNotReplayableAsync(httpContext, originalStatusCode, request.RawKey);
            return NotReplayableResult;
        }

        IdempotencyMetrics.ReplaysTotal.Add(1);
        _logger.LogInformation("Replaying idempotent response for key {KeyHash}, original status {StatusCode}", request.KeyHash, response.StatusCode);

        await _responseWriter.ReplayAsync(httpContext, response);
        return ReplayedResult;
    }

    /// <returns>How the request ended, for the span's <c>idempotency.result</c> tag.</returns>
    private async Task<string> HandleConcurrentRequestAsync(
        HttpContext httpContext,
        GuardedRequest request,
        CancellationToken requestAborted)
    {
        if (_options.ConcurrentRequestPolicy == ConcurrentRequestPolicy.ReturnConflict)
        {
            IdempotencyMetrics.ConflictsTotal.Add(1);
            await _responseWriter.WriteErrorAsync(httpContext, 409, IdempotencyErrorKind.Conflict,
                "Request with this idempotency key is currently being processed", request.RawKey);
            return ConflictResult;
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
                return "cancelled";
            }
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 2000));

            // Claim rather than only read: if the original request failed and released its
            // claim, no response will ever appear, and this request is free to take over.
            switch (await TryClaimAsync(request, requestAborted))
            {
                case ClaimResult.Claimed claimed:
                    IdempotencyMetrics.ClaimsTotal.Add(1);
                    await HandleNewRequestAsync(httpContext, request, claimed.ClaimToken, requestAborted);
                    return ClaimedResult;

                case ClaimResult.Completed:
                    var response = await GetResponseAsync(request, requestAborted);
                    if (response is not null)
                    {
                        return await WriteStoredResponseAsync(httpContext, request, response);
                    }
                    break;

                case ClaimResult.FingerprintMismatch mismatch:
                    await WriteFingerprintMismatchAsync(httpContext, request, mismatch);
                    return FingerprintMismatchResult;
            }
        }

        IdempotencyMetrics.ConflictsTotal.Add(1);
        await _responseWriter.WriteErrorAsync(httpContext, 409, IdempotencyErrorKind.Timeout,
            "Timed out waiting for concurrent request to complete", request.RawKey);
        return ConflictResult;
    }

    private async Task WriteFingerprintMismatchAsync(
        HttpContext httpContext,
        GuardedRequest request,
        ClaimResult.FingerprintMismatch mismatch)
    {
        IdempotencyMetrics.FingerprintMismatchesTotal.Add(1);
        _logger.LogWarning(
            "Idempotency fingerprint mismatch for key {KeyHash}. Expected: {Expected}, Received: {Received}",
            request.KeyHash, mismatch.ExpectedFingerprint, mismatch.ActualFingerprint);
        await _responseWriter.WriteErrorAsync(httpContext, 422, IdempotencyErrorKind.FingerprintMismatch,
            "Idempotency key has already been used with a different request payload",
            request.RawKey);
    }

    private static void RecordStoreLatency(long startTimestamp, string operation)
    {
        IdempotencyMetrics.StoreLatency.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            new KeyValuePair<string, object?>("operation", operation));
    }

    private sealed record GuardedRequest(
        string RawKey,
        string Key,
        string StoreKey,
        string KeyHash,
        string Fingerprint,
        TimeSpan ClaimTtl,
        TimeSpan ResponseTtl,
        Func<int, bool>? ShouldStoreResponse,
        OversizedResponseBehavior OversizedResponse);
}
