using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.Redis;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class RedisMiddlewareTests : MiddlewareStoreContractTests, IClassFixture<RedisContainerFixture>
{
    private readonly RedisContainerFixture _fixture;

    public RedisMiddlewareTests(RedisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void ConfigureStore(IServiceCollection services) =>
        services.AddIdempotencyGuardRedisStore(options =>
        {
            options.ConnectionString = _fixture.ConnectionString;
            options.KeyPrefix = "test-mw:";
        });
}
