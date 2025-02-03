using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IdempotencyGuard.AspNetCore;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdempotencyGuard(
        this IServiceCollection services,
        Action<IdempotencyOptions>? configure = null)
    {
        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.Configure<IdempotencyOptions>(_ => { });
        }

        services.TryAddSingleton<IDownstreamKeyGenerator, DefaultDownstreamKeyGenerator>();

        return services;
    }

    public static IServiceCollection AddIdempotencyGuardInMemoryStore(this IServiceCollection services)
    {
        var store = new InMemoryIdempotencyStore();
        services.AddSingleton<IIdempotencyStore>(store);
        services.AddSingleton(store);
        return services;
    }
}
