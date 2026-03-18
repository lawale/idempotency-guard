using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.Redis;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class RedisStoreTests : IdempotencyStoreContractTests, IClassFixture<RedisContainerFixture>
{
    private readonly RedisIdempotencyStore _store;

    public RedisStoreTests(RedisContainerFixture fixture)
    {
        var options = Options.Create(new RedisIdempotencyOptions
        {
            KeyPrefix = "test:"
        });
        _store = new RedisIdempotencyStore(fixture.Connection, options);
    }

    protected override IIdempotencyStore Store => _store;
}
