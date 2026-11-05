namespace IdempotencyGuard.AspNetCore;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class IdempotentAttribute : Attribute
{
    public bool Required { get; set; } = true;
    public int ClaimTtlSeconds { get; set; }
    public int ResponseTtlSeconds { get; set; }

    /// <summary>
    /// Name of a policy registered with <see cref="IdempotencyOptions.AddPolicy"/>.
    /// When <c>null</c>, the global values on <see cref="IdempotencyOptions"/> apply.
    /// An unknown name fails at startup.
    /// </summary>
    public string? Policy { get; set; }

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

    /// <summary>
    /// Combines attributes from least to most specific (controller, then action). Each
    /// setting comes from the most specific attribute that sets it. <see cref="Required"/>
    /// has no unset value, so it always comes from the most specific attribute.
    /// </summary>
    internal static IdempotentAttribute? Merge(IReadOnlyList<IdempotentAttribute> attributes)
    {
        if (attributes.Count <= 1)
            return attributes.Count == 0 ? null : attributes[0];

        var merged = new IdempotentAttribute();

        foreach (var attribute in attributes)
        {
            merged.Required = attribute.Required;
            merged.ClaimTtlSeconds = attribute.ClaimTtlSeconds > 0 ? attribute.ClaimTtlSeconds : merged.ClaimTtlSeconds;
            merged.ResponseTtlSeconds = attribute.ResponseTtlSeconds > 0 ? attribute.ResponseTtlSeconds : merged.ResponseTtlSeconds;
            merged.Policy = attribute.Policy ?? merged.Policy;
            merged.FingerprintProperties = attribute.FingerprintProperties ?? merged.FingerprintProperties;
            merged.FingerprintQueryParameters = attribute.FingerprintQueryParameters ?? merged.FingerprintQueryParameters;
            merged.FingerprintRouteValues = attribute.FingerprintRouteValues ?? merged.FingerprintRouteValues;
        }

        return merged;
    }
}
