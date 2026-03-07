using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyGuard.SqlServer;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdempotencyGuardSqlServerStore(
        this IServiceCollection services,
        Action<SqlServerIdempotencyOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<IIdempotencyStore, SqlServerIdempotencyStore>();
        services.AddSingleton<IPurgableIdempotencyStore>(sp =>
            (IPurgableIdempotencyStore)sp.GetRequiredService<IIdempotencyStore>());
        return services;
    }
}
