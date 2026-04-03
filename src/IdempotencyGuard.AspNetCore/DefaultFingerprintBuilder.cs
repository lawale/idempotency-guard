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
    private const int _initialBufferCapacity = 4096; //4kb
    private const int _readBufferSize = 8192;  //8kb

    private readonly IdempotencyOptions _options;

    public DefaultFingerprintBuilder(IOptions<IdempotencyOptions> options)
    {
        _options = options.Value;
    }

    public async Task<FingerprintResult> ComputeAsync(
        HttpContext context, IdempotentAttribute? attribute)
    {
        var requestBody = await ReadRequestBodyAsync(context.Request, _options.MaxFingerprintBodySize);

        var fingerprintBody = _options.MaxFingerprintBodySize == 0
            ? null
            : requestBody;

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

    private static async Task<byte[]?> ReadRequestBodyAsync(HttpRequest request, int maxBytesToRead)
    {
        if (!request.Body.CanSeek)
        {
            request.EnableBuffering();
        }

        // Cap the memory allocation to MaxFingerprintBodySize to prevent
        // very large request bodies from consuming excessive memory.
        var bytesToRead = maxBytesToRead > 0
            ? maxBytesToRead
            : (int?)null;

        using var memoryStream = bytesToRead.HasValue
            ? new MemoryStream(Math.Min(bytesToRead.Value, _initialBufferCapacity))
            : new MemoryStream();

        request.Body.Position = 0;

        if (bytesToRead.HasValue)
        {
            var buffer = new byte[Math.Min(bytesToRead.Value, _readBufferSize)];
            var remaining = bytesToRead.Value;
            int bytesRead;
            while (remaining > 0 &&
                   (bytesRead = await request.Body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)))) > 0)
            {
                memoryStream.Write(buffer, 0, bytesRead);
                remaining -= bytesRead;
            }
        }
        else
        {
            await request.Body.CopyToAsync(memoryStream);
        }

        request.Body.Position = 0;

        if (!memoryStream.TryGetBuffer(out var result) || result.Count == 0)
            return null;

        // When the internal buffer is exactly right-sized, return it directly
        // to avoid the extra allocation that ToArray() always performs.
        return result.Count == result.Array!.Length
            ? result.Array
            : result.AsSpan().ToArray();
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

        return string.Join("|", segments.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }
}
