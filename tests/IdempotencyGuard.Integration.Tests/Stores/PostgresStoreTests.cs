using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.PostgreSql;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class PostgresStoreTests : IdempotencyStoreContractTests, IClassFixture<PostgresContainerFixture>, IAsyncLifetime
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

    public async Task InitializeAsync()
    {
        // Warm up: ensure the table is created before any test runs.
        // Prevents concurrent CREATE TABLE race conditions in the Concurrent_claims test.
        await Store.TryClaimAsync("warmup", "warmup", TimeSpan.FromSeconds(1));
        await Store.ReleaseClaimAsync("warmup");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
