using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.SqlServer;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class SqlServerStoreTests : IdempotencyStoreContractTests, IClassFixture<SqlServerContainerFixture>, IAsyncLifetime
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

    public async Task InitializeAsync()
    {
        // Warm up: ensure the table is created before any test runs.
        // Prevents concurrent CREATE TABLE race conditions in the Concurrent_claims test.
        await Store.TryClaimAsync("warmup", "warmup", TimeSpan.FromSeconds(1));
        await Store.ReleaseClaimAsync("warmup");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
