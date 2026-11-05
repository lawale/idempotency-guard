using System.Diagnostics;
using IdempotencyGuard.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore;

public class IdempotencyMiddleware
{
    // AuthorizationMiddleware sets this item when it runs. EndpointMiddleware reads the same
    // item to detect endpoints that have authorization metadata but were never authorized.
    private const string AuthorizationInvokedItemKey = "__AuthorizationMiddlewareWithEndpointInvoked";

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
        ThrowIfAuthorizationHasNotRun(httpContext, endpoint);

        // Read [Idempotent] attributes from endpoint metadata
        var idempotentAttr = endpoint is null
            ? null
            : IdempotentAttribute.Merge(endpoint.Metadata.GetOrderedMetadata<IdempotentAttribute>());
        var policy = ResolvePolicy(idempotentAttr);

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

        var idempotencyKey = string.Concat(keyPrefix, scopeSegment, rawKey);

        // Per-endpoint TTL overrides
        var claimTtl = idempotentAttr?.ClaimTtlSeconds > 0
            ? TimeSpan.FromSeconds(idempotentAttr.ClaimTtlSeconds)
            : _options.ClaimTtl;

        var responseTtl = idempotentAttr?.ResponseTtlSeconds > 0
            ? TimeSpan.FromSeconds(idempotentAttr.ResponseTtlSeconds)
            : _options.ResponseTtl;

        IdempotencyMetrics.RequestsTotal.Add(1);

        var keyHash = IdempotencyKeyHash.Compute(idempotencyKey);

        using var requestActivity = IdempotencyActivitySource.Source.StartActivity("idempotency.request", ActivityKind.Internal);
        requestActivity?.SetTag("idempotency.key_hash", keyHash);
        requestActivity?.SetTag("http.method", httpContext.Request.Method);
        requestActivity?.SetTag("http.route", httpContext.Request.Path.Value);

        var result = await _fingerprintBuilder.ComputeAsync(httpContext, idempotentAttr);

        var request = new GuardedRequest(
            rawKey,
            idempotencyKey,
            keyHash,
            result.Fingerprint,
            claimTtl,
            responseTtl,
            policy?.ShouldStoreResponse ?? _options.ShouldStoreResponse);

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
    /// Builds the part of the store key that isolates one caller from another. The scope is
    /// hashed to a fixed length, so a scope can't run into the client's key and collide, and
    /// caller identifiers stay out of the store. A policy's own resolver also hashes the
    /// policy name, so user "abc" and service "abc" stay apart under different policies.
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

        var scopeIdentity = policy?.KeyScope is not null ? $"{attribute!.Policy}\0{scope}" : scope;
        segment = $"{IdempotencyKeyHash.Compute(scopeIdentity)}:";
        return true;
    }

    private async Task<ClaimResult> TryClaimAsync(GuardedRequest request, CancellationToken requestAborted)
    {
        ClaimResult claimResult;
        var claimTs = Stopwatch.GetTimestamp();
        using (var claimActivity = IdempotencyActivitySource.StartStoreActivity("claim", request.KeyHash))
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
        _logger.LogInformation("Idempotency key claimed: {KeyHash}", request.KeyHash);

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
                    "Response body for key {KeyHash} exceeds MaxResponseBodySize ({MaxSize} bytes), claim released",
                    request.KeyHash, _options.MaxResponseBodySize);
            }
            else if (request.ShouldStoreResponse?.Invoke(statusCode) == false)
            {
                claimSettled = true;
                await ReleaseClaimAsync(request, claimToken);
                _logger.LogInformation(
                    "Response with status {StatusCode} for key {KeyHash} not stored, claim released",
                    statusCode, request.KeyHash);
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

                // Deliberately not cancellable. The handler's side effects have already happened,
                // so a client hanging up now must not lose the response that makes retries safe.
                bool stored;
                var setTs = Stopwatch.GetTimestamp();
                using (IdempotencyActivitySource.StartStoreActivity("set_response", request.KeyHash))
                {
                    stored = await _store.SetResponseAsync(
                        request.Key, claimToken, idempotentResponse, request.ResponseTtl, CancellationToken.None);
                }
                RecordStoreLatency(setTs, "set_response");
                claimSettled = true;

                if (stored)
                {
                    _logger.LogInformation(
                        "Idempotent response stored for key {KeyHash}, status {StatusCode}",
                        request.KeyHash, statusCode);
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
                await ReleaseClaimAsync(request, claimToken);
                _logger.LogWarning(ex, "Idempotency claim released after failure for key {KeyHash}", request.KeyHash);
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
        using (IdempotencyActivitySource.StartStoreActivity("release", request.KeyHash))
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
        using (IdempotencyActivitySource.StartStoreActivity("get_response", request.KeyHash))
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
                "Request is being processed", request.RawKey);
            return;
        }

        _logger.LogInformation("Replaying idempotent response for key {KeyHash}, original status {StatusCode}", request.KeyHash, response.StatusCode);

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
                "Request with this idempotency key is currently being processed", request.RawKey);
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

            // Claim rather than only read: if the original request failed and released its
            // claim, no response will ever appear, and this request is free to take over.
            switch (await TryClaimAsync(request, requestAborted))
            {
                case ClaimResult.Claimed claimed:
                    IdempotencyMetrics.ClaimsTotal.Add(1);
                    await HandleNewRequestAsync(httpContext, request, claimed.ClaimToken, requestAborted);
                    return;

                case ClaimResult.Completed:
                    var response = await GetResponseAsync(request, requestAborted);
                    if (response is not null)
                    {
                        IdempotencyMetrics.ReplaysTotal.Add(1);
                        _logger.LogInformation("Replaying idempotent response after wait for key {KeyHash}", request.KeyHash);

                        await _responseWriter.ReplayAsync(httpContext, response);
                        return;
                    }
                    break;

                case ClaimResult.FingerprintMismatch mismatch:
                    await WriteFingerprintMismatchAsync(httpContext, request, mismatch);
                    return;
            }
        }

        await _responseWriter.WriteErrorAsync(httpContext, 409, IdempotencyErrorKind.Timeout,
            "Timed out waiting for concurrent request to complete", request.RawKey);
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
        string KeyHash,
        string Fingerprint,
        TimeSpan ClaimTtl,
        TimeSpan ResponseTtl,
        Func<int, bool>? ShouldStoreResponse);
}
