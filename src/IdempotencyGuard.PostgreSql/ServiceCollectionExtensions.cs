using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyGuard.PostgreSql;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdempotencyGuardPostgresStore(
        this IServiceCollection services,
        Action<PostgresIdempotencyOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();
        return services;
    }
}
