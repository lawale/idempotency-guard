using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore;

public static class ApplicationBuilderExtensions
{
    // UseRouting() sets this property. WebApplication's implicit routing runs ahead of the
    // user's pipeline and doesn't set it on the builder the guard is added to.
    private const string EndpointRouteBuilderKey = "__EndpointRouteBuilder";

    public static IApplicationBuilder UseIdempotencyGuard(this IApplicationBuilder app)
    {
        var routingAlreadyAdded = app.Properties.ContainsKey(EndpointRouteBuilderKey);

        // This runs when the pipeline is built, once every Use call has been made, so a later
        // UseRouting() is visible. It adds nothing to the pipeline at request time.
        app.Use(next =>
        {
            if (!routingAlreadyAdded
                && app.Properties.ContainsKey(EndpointRouteBuilderKey)
                && app.ApplicationServices.GetRequiredService<IOptions<IdempotencyOptions>>().Value.UnmarkedEndpoints == UnmarkedEndpointBehavior.Ignore)
            {
                throw new InvalidOperationException(
                    "UnmarkedEndpointBehavior.Ignore needs routing to run first, but UseRouting() was called after " +
                    "UseIdempotencyGuard(), so no endpoint would be guarded. Call UseRouting() first.");
            }

            return next;
        });

        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}
