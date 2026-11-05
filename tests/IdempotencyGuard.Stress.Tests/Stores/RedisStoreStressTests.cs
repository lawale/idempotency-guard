using IdempotencyGuard.Redis;
using IdempotencyGuard.Stress.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace IdempotencyGuard.Stress.Tests.Stores;

public class RedisStoreStressTests : IdempotencyStoreStressTests, IClassFixture<RedisContainerFixture>, IAsyncLifetime
{
    private readonly ServiceProvider _serviceProvider;

    public RedisStoreStressTests(RedisContainerFixture fixture, ITestOutputHelper output) : base(output)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdempotencyGuardRedisStore(options =>
        {
            options.ConnectionString = fixture.ConnectionString;
            options.KeyPrefix = "stress:";
        });

        _serviceProvider = services.BuildServiceProvider();
    }

    protected override IIdempotencyStore Store => _serviceProvider.GetRequiredService<IIdempotencyStore>();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _serviceProvider.DisposeAsync();
}
