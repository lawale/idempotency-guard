using Microsoft.AspNetCore.Builder;

namespace IdempotencyGuard.AspNetCore;

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseIdempotencyGuard(this IApplicationBuilder app)
    {
        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}
