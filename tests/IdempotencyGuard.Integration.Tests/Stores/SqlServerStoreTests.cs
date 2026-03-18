using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.SqlServer;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class SqlServerStoreTests : IdempotencyStoreContractTests, IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerIdempotencyStore _store;

    public SqlServerStoreTests(SqlServerContainerFixture fixture)
    {
        var options = Options.Create(new SqlServerIdempotencyOptions
        {
            ConnectionString = fixture.ConnectionString,
            AutoCreateTable = true
        });
        _store = new SqlServerIdempotencyStore(options);
    }

    protected override IIdempotencyStore Store => _store;
}
