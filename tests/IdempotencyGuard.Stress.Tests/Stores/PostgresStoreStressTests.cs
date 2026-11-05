using IdempotencyGuard.PostgreSql;
using IdempotencyGuard.Stress.Tests.Fixtures;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace IdempotencyGuard.Stress.Tests.Stores;

public class PostgresStoreStressTests : IdempotencyStoreStressTests, IClassFixture<PostgresContainerFixture>, IAsyncLifetime
{
    private readonly PostgresIdempotencyStore _store;

    public PostgresStoreStressTests(PostgresContainerFixture fixture, ITestOutputHelper output) : base(output)
    {
        _store = new PostgresIdempotencyStore(Options.Create(new PostgresIdempotencyOptions
        {
            ConnectionString = fixture.ConnectionString,
            AutoCreateTable = true
        }));
    }

    protected override IIdempotencyStore Store => _store;

    // Create the table before the load starts: schema creation racing hundreds of
    // concurrent claims is a test-setup artefact, not the behaviour under test.
    public async Task InitializeAsync()
    {
        await _store.TryClaimAsync("stress-warmup", "warmup", TimeSpan.FromSeconds(1));
        await _store.ReleaseClaimAsync("stress-warmup");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
