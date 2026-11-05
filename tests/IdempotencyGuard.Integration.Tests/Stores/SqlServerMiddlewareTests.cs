using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class SqlServerMiddlewareTests : MiddlewareStoreContractTests, IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture;

    public SqlServerMiddlewareTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void ConfigureStore(IServiceCollection services) =>
        services.AddIdempotencyGuardSqlServerStore(options =>
        {
            options.ConnectionString = _fixture.ConnectionString;
            options.AutoCreateTable = true;
        });
}
