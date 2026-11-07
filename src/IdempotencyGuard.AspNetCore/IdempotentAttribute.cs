namespace IdempotencyGuard.AspNetCore;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class IdempotentAttribute : Attribute
{
    // Nullable so that Merge can tell "not set" from true. It can't be nullable on the public
    // property, because C# doesn't allow a nullable type as an attribute argument.
    private bool? _required;

    /// <summary>
    /// Whether a request needs a key. Without one, a required endpoint returns a 400, and any
    /// other runs the handler with no duplicate protection: the guard never generates a key.
    /// Set this to <c>false</c> while moving clients over to sending keys, so that an endpoint
    /// can have its own settings before every client sends one. Default: <c>true</c>.
    /// </summary>
    public bool Required
    {
        get => _required ?? true;
        set => _required = value;
    }

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
    /// setting comes from the most specific attribute that sets it.
    /// </summary>
    internal static IdempotentAttribute? Merge(IReadOnlyList<IdempotentAttribute> attributes)
    {
        if (attributes.Count <= 1)
            return attributes.Count == 0 ? null : attributes[0];

        var merged = new IdempotentAttribute();

        foreach (var attribute in attributes)
        {
            merged._required = attribute._required ?? merged._required;
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
