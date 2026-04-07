using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.Redis;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class RedisStoreTests : IdempotencyStoreContractTests, IClassFixture<RedisContainerFixture>, IAsyncLifetime
{
    private readonly IServiceProvider _serviceProvider;

    public RedisStoreTests(RedisContainerFixture fixture)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdempotencyGuardRedisStore(options =>
        {
            options.ConnectionString = fixture.ConnectionString;
            options.KeyPrefix = "test:";
        });

        _serviceProvider = services.BuildServiceProvider();
    }

    protected override IIdempotencyStore Store => _serviceProvider.GetRequiredService<IIdempotencyStore>();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }
}
