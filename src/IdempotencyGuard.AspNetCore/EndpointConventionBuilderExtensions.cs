using Microsoft.AspNetCore.Builder;

namespace IdempotencyGuard.AspNetCore;

/// <summary>
/// Extension methods for <see cref="IEndpointConventionBuilder"/> to configure
/// idempotency on minimal API endpoints.
/// </summary>
public static class EndpointConventionBuilderExtensions
{
    /// <summary>
    /// Marks this endpoint as idempotent with default settings.
    /// Requires an Idempotency-Key header on every request.
    /// </summary>
    public static TBuilder RequireIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        return builder.RequireIdempotency(_ => { });
    }

    /// <summary>
    /// Marks this endpoint as idempotent with custom settings.
    /// Configure claim/response TTLs, fingerprinting options,
    /// and whether the idempotency key is required.
    /// </summary>
    /// <example>
    /// <code>
    /// app.MapPost("/payments", (PaymentRequest request) =>
    /// {
    ///     // ...
    /// }).RequireIdempotency(options =>
    /// {
    ///     options.ClaimTtlSeconds = 120;
    ///     options.FingerprintProperties = ["Amount", "Currency"];
    /// });
    /// </code>
    /// </example>
    public static TBuilder RequireIdempotency<TBuilder>(
        this TBuilder builder,
        Action<IdempotentAttribute> configure)
        where TBuilder : IEndpointConventionBuilder
    {
        var attribute = new IdempotentAttribute();
        configure(attribute);
        builder.WithMetadata(attribute);
        return builder;
    }
}
