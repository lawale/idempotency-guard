using Microsoft.AspNetCore.Http;

namespace IdempotencyGuard.AspNetCore;

/// <summary>
/// Computes a request fingerprint from the HTTP context and optional
/// per-endpoint attribute configuration. Implement this interface to
/// customize how request identity is determined (e.g. HMAC-based,
/// header-inclusive, or content-type-aware strategies).
/// </summary>
public interface IFingerprintBuilder
{
    /// <summary>
    /// Reads the request body and computes a deterministic fingerprint hash
    /// incorporating the HTTP method, path, body content, and any extra
    /// fingerprint segments configured via <see cref="IdempotentAttribute"/>.
    /// </summary>
    Task<FingerprintResult> ComputeAsync(HttpContext context, IdempotentAttribute? attribute);
}
