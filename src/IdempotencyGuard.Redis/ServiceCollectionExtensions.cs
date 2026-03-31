using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IdempotencyGuard.Redis;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdempotencyGuardRedisStore(
        this IServiceCollection services,
        Action<RedisIdempotencyOptions> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<RedisIdempotencyOptions>>().Value;
            return ConnectionMultiplexer.Connect(options.ConnectionString);
        });
        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        return services;
    }

    public static IServiceCollection AddIdempotencyGuardRedisStore(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connectionString));

        services.Configure<RedisIdempotencyOptions>(options =>
        {
            options.ConnectionString = connectionString;
        });

        services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        return services;
    }
}
