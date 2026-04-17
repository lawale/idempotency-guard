using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.Redis;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdempotencyGuardRedisStore(
        this IServiceCollection services,
        Action<RedisIdempotencyOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<RedisConnectionManager>();
        services.TryAddSingleton<RedisIdempotencyStore>(sp =>
            new RedisIdempotencyStore(
                sp.GetRequiredService<RedisConnectionManager>(),
                sp.GetRequiredService<IOptions<RedisIdempotencyOptions>>()));
        services.AddSingleton<IIdempotencyStore>(sp => sp.GetRequiredService<RedisIdempotencyStore>());
        return services;
    }

    public static IServiceCollection AddIdempotencyGuardRedisStore(
        this IServiceCollection services,
        string connectionString)
    {
        return services.AddIdempotencyGuardRedisStore(options =>
        {
            options.ConnectionString = connectionString;
        });
    }
}
