using IdempotencyGuard.SqlServer;
using IdempotencyGuard.Stress.Tests.Fixtures;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace IdempotencyGuard.Stress.Tests.Stores;

public class SqlServerStoreStressTests : IdempotencyStoreStressTests, IClassFixture<SqlServerContainerFixture>, IAsyncLifetime
{
    private readonly SqlServerIdempotencyStore _store;

    public SqlServerStoreStressTests(SqlServerContainerFixture fixture, ITestOutputHelper output) : base(output)
    {
        _store = new SqlServerIdempotencyStore(Options.Create(new SqlServerIdempotencyOptions
        {
            ConnectionString = fixture.ConnectionString,
            AutoCreateTable = true
        }));
    }

    protected override IIdempotencyStore Store => _store;

    public async Task InitializeAsync()
    {
        await _store.TryClaimAsync("stress-warmup", "warmup", TimeSpan.FromSeconds(1));
        await _store.ReleaseClaimAsync("stress-warmup");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
