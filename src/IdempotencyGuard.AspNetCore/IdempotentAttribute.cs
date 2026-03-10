namespace IdempotencyGuard.AspNetCore;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class IdempotentAttribute : Attribute
{
    public bool Required { get; set; } = true;
    public int ClaimTtlSeconds { get; set; }
    public int ResponseTtlSeconds { get; set; }

    /// <summary>
    /// Top-level JSON property names to include in the request fingerprint.
    /// When set, only these properties are hashed; all other body content is ignored.
    /// Property matching is case-insensitive, so <c>nameof(PaymentRequest.Amount)</c>
    /// matches the JSON key <c>"amount"</c> regardless of serializer casing.
    /// When <c>null</c> or empty, the entire request body is used (default behaviour).
    /// </summary>
    public string[]? FingerprintProperties { get; set; }

    /// <summary>
    /// Query parameter names to include in the request fingerprint.
    /// Values are matched by exact parameter name (case-insensitive).
    /// When <c>null</c> or empty, query parameters are not included in the fingerprint (default behaviour).
    /// </summary>
    public string[]? FingerprintQueryParameters { get; set; }

    /// <summary>
    /// Route value names to include in the request fingerprint.
    /// Values are matched by exact route parameter name (case-insensitive).
    /// When <c>null</c> or empty, route values are not included in the fingerprint (default behaviour).
    /// </summary>
    public string[]? FingerprintRouteValues { get; set; }
}
