using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using IdempotencyGuard.Redis;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class RedisConnectionResilienceTests
{
    [Fact]
    public async Task store_connects_lazily_and_basic_operations_still_work()
    {
        await using var redis = await RedisTestInstance.StartAsync();
        var provider = CreateServiceProvider(redis.ConnectionString);

        try
        {
            var store = provider.GetRequiredService<IIdempotencyStore>();
            var key = UniqueKey();

            var claimResult = await store.TryClaimAsync(key, "fp-1", TimeSpan.FromMinutes(1));
            claimResult.Should().BeOfType<ClaimResult.Claimed>();

            var response = CreateResponse(201, """{"ok":true}""");
            await store.SetResponseAsync(key, response, TimeSpan.FromMinutes(5));

            var storedResponse = await store.GetResponseAsync(key);
            storedResponse.Should().NotBeNull();
            storedResponse!.StatusCode.Should().Be(201);

            await store.ReleaseClaimAsync(key);

            var afterRelease = await store.GetResponseAsync(key);
            afterRelease.Should().BeNull();
        }
        finally
        {
            await DisposeProviderAsync(provider);
        }
    }

    [Fact]
    public async Task store_recovers_after_redis_container_restart()
    {
        var hostPort = GetFreePort();
        var connectionString = CreateConnectionString(hostPort);
        await using var firstRedis = await RedisTestInstance.StartAsync(hostPort);
        var provider = CreateServiceProvider(
            connectionString,
            reconnectMinInterval: TimeSpan.FromMilliseconds(250));

        try
        {
            var store = provider.GetRequiredService<IIdempotencyStore>();

            await store.TryClaimAsync(UniqueKey(), "warmup", TimeSpan.FromSeconds(10));

            await firstRedis.DisposeAsync();

            Func<Task> operationWhileDown = async () =>
                await store.TryClaimAsync(UniqueKey(), "fp-1", TimeSpan.FromSeconds(10));

            await operationWhileDown.Should().ThrowAsync<RedisConnectionException>();

            await using var secondRedis = await RedisTestInstance.StartAsync(hostPort);

            await EventuallyAsync(async () =>
            {
                var result = await store.TryClaimAsync(UniqueKey(), "fp-2", TimeSpan.FromSeconds(10));
                result.Should().BeOfType<ClaimResult.Claimed>();
            });
        }
        finally
        {
            await DisposeProviderAsync(provider);
        }
    }

    [Fact]
    public async Task store_can_be_resolved_before_redis_is_available()
    {
        var hostPort = GetFreePort();
        var connectionString = CreateConnectionString(hostPort);
        var provider = CreateServiceProvider(
            connectionString,
            reconnectMinInterval: TimeSpan.FromMilliseconds(250));

        try
        {
            var store = provider.GetRequiredService<IIdempotencyStore>();

            await using var redis = await RedisTestInstance.StartAsync(hostPort);

            await EventuallyAsync(async () =>
            {
                var result = await store.TryClaimAsync(UniqueKey(), "fp-1", TimeSpan.FromSeconds(10));
                result.Should().BeOfType<ClaimResult.Claimed>();
            });
        }
        finally
        {
            await DisposeProviderAsync(provider);
        }
    }

    private static IServiceProvider CreateServiceProvider(
        string connectionString,
        TimeSpan? reconnectMinInterval = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIdempotencyGuardRedisStore(options =>
        {
            options.ConnectionString = connectionString;
            options.KeyPrefix = "test:";
            if (reconnectMinInterval is not null)
            {
                options.MinimumReconnectInterval = reconnectMinInterval.Value;
            }
        });

        return services.BuildServiceProvider();
    }

    private static async Task DisposeProviderAsync(IServiceProvider provider)
    {
        if (provider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }

    private static async Task EventuallyAsync(Func<Task> assertion, int attempts = 20, int delayMs = 250)
    {
        Exception? lastException = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                await assertion();
                return;
            }
            catch (RedisConnectionException ex)
            {
                lastException = ex;
            }
            catch (RedisTimeoutException ex)
            {
                lastException = ex;
            }
            catch (TimeoutException ex)
            {
                lastException = ex;
            }

            await Task.Delay(delayMs);
        }

        throw new Xunit.Sdk.XunitException($"Redis operation did not succeed after {attempts} attempts. Last error: {lastException}");
    }

    private static string CreateConnectionString(int hostPort) =>
        $"localhost:{hostPort},connectTimeout=1000,syncTimeout=1000,asyncTimeout=1000";

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string UniqueKey() => $"redis-resilience-{Guid.NewGuid():N}";

    private static IdempotentResponse CreateResponse(int statusCode, string body) =>
        new()
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, string[]>
            {
                ["Content-Type"] = ["application/json"]
            },
            Body = System.Text.Encoding.UTF8.GetBytes(body)
        };

    private sealed class RedisTestInstance : IAsyncDisposable
    {
        private readonly RedisContainer _container;

        private RedisTestInstance(RedisContainer container, string connectionString)
        {
            _container = container;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<RedisTestInstance> StartAsync(int? hostPort = null)
        {
            var configuredHostPort = hostPort ?? GetFreePort();
            var container = new RedisBuilder()
                .WithImage("redis:7-alpine")
                .WithPortBinding(configuredHostPort, 6379)
                .Build();

            await container.StartAsync();

            return new RedisTestInstance(container, CreateConnectionString(configuredHostPort));
        }

        public async ValueTask DisposeAsync()
        {
            await _container.DisposeAsync();
        }
    }
}
