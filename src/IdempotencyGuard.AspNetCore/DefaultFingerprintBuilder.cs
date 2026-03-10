using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore;

/// <summary>
/// Default fingerprint strategy: reads the request body, applies the
/// configured size cap and property extraction from
/// <see cref="IdempotentAttribute"/>, incorporates query/route extra
/// fingerprint segments, and delegates to
/// <see cref="RequestFingerprint.Compute"/>.
/// </summary>
public class DefaultFingerprintBuilder : IFingerprintBuilder
{
    private readonly IdempotencyOptions _options;

    public DefaultFingerprintBuilder(IOptions<IdempotencyOptions> options)
    {
        _options = options.Value;
    }

    public async Task<FingerprintResult> ComputeAsync(
        HttpContext context, IdempotentAttribute? attribute)
    {
        var requestBody = await ReadRequestBodyAsync(context.Request);

        var fingerprintBody = requestBody is not null
            && _options.MaxFingerprintBodySize > 0
            && requestBody.Length > _options.MaxFingerprintBodySize
                ? requestBody[.._options.MaxFingerprintBodySize]
                : (_options.MaxFingerprintBodySize == 0 ? null : requestBody);

        if (attribute?.FingerprintProperties is { Length: > 0 } fingerprintProperties)
            fingerprintBody = RequestFingerprint.ExtractProperties(fingerprintBody, fingerprintProperties);

        var extraFingerprint = BuildExtraFingerprint(context, attribute);

        var fingerprint = RequestFingerprint.Compute(
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            fingerprintBody,
            extraFingerprint);

        return new FingerprintResult(fingerprint, requestBody);
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

        if (!memoryStream.TryGetBuffer(out var buffer) || buffer.Count == 0)
            return null;

        // When the internal buffer is exactly right-sized, return it directly
        // to avoid the extra allocation that ToArray() always performs.
        return buffer.Count == buffer.Array!.Length
            ? buffer.Array
            : buffer.AsSpan().ToArray();
    }

    private static string? BuildExtraFingerprint(HttpContext httpContext, IdempotentAttribute? attr)
    {
        if (attr is null)
            return null;

        var segments = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (attr.FingerprintQueryParameters is { Length: > 0 } queryParams)
        {
            var query = httpContext.Request.Query;
            foreach (var name in queryParams)
            {
                if (query.TryGetValue(name, out var value))
                    segments[$"q:{name}"] = value.ToString();
            }
        }

        if (attr.FingerprintRouteValues is { Length: > 0 } routeValues)
        {
            var route = httpContext.Request.RouteValues;
            foreach (var name in routeValues)
            {
                if (route.TryGetValue(name, out var value) && value is not null)
                    segments[$"r:{name}"] = value.ToString()!;
            }
        }

        if (segments.Count == 0)
            return null;

        return string.Join("|", segments.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
