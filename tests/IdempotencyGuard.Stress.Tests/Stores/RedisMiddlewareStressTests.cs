using IdempotencyGuard.Redis;
using IdempotencyGuard.Stress.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace IdempotencyGuard.Stress.Tests.Stores;

public class RedisMiddlewareStressTests : MiddlewareStressTests, IClassFixture<RedisContainerFixture>
{
    private readonly RedisContainerFixture _fixture;

    public RedisMiddlewareStressTests(RedisContainerFixture fixture, ITestOutputHelper output) : base(output)
    {
        _fixture = fixture;
    }

    protected override string StoreName => "redis";

    protected override void ConfigureStore(IServiceCollection services) =>
        services.AddIdempotencyGuardRedisStore(options =>
        {
            options.ConnectionString = _fixture.ConnectionString;
            options.KeyPrefix = "stress-mw:";
        });
}
