using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.PostgreSql;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class PostgresMiddlewareTests : MiddlewareStoreContractTests, IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fixture;

    public PostgresMiddlewareTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void ConfigureStore(IServiceCollection services) =>
        services.AddIdempotencyGuardPostgresStore(options =>
        {
            options.ConnectionString = _fixture.ConnectionString;
            options.AutoCreateTable = true;
        });
}
