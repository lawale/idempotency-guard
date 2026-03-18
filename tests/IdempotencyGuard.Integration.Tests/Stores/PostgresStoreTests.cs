using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.PostgreSql;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class PostgresStoreTests : IdempotencyStoreContractTests, IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresIdempotencyStore _store;

    public PostgresStoreTests(PostgresContainerFixture fixture)
    {
        var options = Options.Create(new PostgresIdempotencyOptions
        {
            ConnectionString = fixture.ConnectionString,
            AutoCreateTable = true
        });
        _store = new PostgresIdempotencyStore(options);
    }

    protected override IIdempotencyStore Store => _store;
}
