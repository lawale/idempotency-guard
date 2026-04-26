using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IdempotencyGuard.Redis;

internal sealed class RedisConnectionManager : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly ILogger<RedisConnectionManager> _logger;
    private readonly RedisIdempotencyOptions _options;

    private IConnectionMultiplexer? _redisConnection;
    private ConfigurationOptions? _redisConfigurationOptions;
    private DateTimeOffset _lastReconnectTime = DateTimeOffset.MinValue;
    private bool _firstInitialization = true;
    private volatile bool _disposed;

    public RedisConnectionManager(
        IOptions<RedisIdempotencyOptions> options,
        ILogger<RedisConnectionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask<IDatabase> GetDatabaseAsync()
    {
        ThrowIfDisposed();
        var connection = await GetConnectionAsync();
        return connection.GetDatabase();
    }

    public async Task<TimeSpan> PingAsync()
    {
        ThrowIfDisposed();
        var db = await GetDatabaseAsync();
        return await db.PingAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var connection = Interlocked.Exchange(ref _redisConnection, null);
        connection?.Dispose();
        _reconnectLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var connection = Interlocked.Exchange(ref _redisConnection, null);

        if (connection is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            connection?.Dispose();
        }

        _reconnectLock.Dispose();
    }

    private ValueTask<IConnectionMultiplexer> GetConnectionAsync()
    {
        var connection = Volatile.Read(ref _redisConnection);
        if (connection is { IsConnected: true })
        {
            return new ValueTask<IConnectionMultiplexer>(connection);
        }

        return ReconnectAsync();
    }

    private async ValueTask<IConnectionMultiplexer> ReconnectAsync()
    {
        await _reconnectLock.WaitAsync();
        try
        {
            ThrowIfDisposed();

            if (_redisConnection is { IsConnected: true })
            {
                return _redisConnection;
            }

            var utcNow = DateTimeOffset.UtcNow;
            var elapsed = utcNow - _lastReconnectTime;

            if (!_firstInitialization && elapsed < _options.MinimumReconnectInterval && _redisConnection is not null)
            {
                _logger.LogDebug(
                    "Redis reconnect skipped - last attempt was {ElapsedSeconds}s ago (min interval: {MinIntervalSeconds}s)",
                    (int)elapsed.TotalSeconds,
                    (int)_options.MinimumReconnectInterval.TotalSeconds);

                return _redisConnection;
            }

            _firstInitialization = false;
            var oldConnection = _redisConnection;
            _lastReconnectTime = utcNow;

            _redisConnection = await ConnectionMultiplexer.ConnectAsync(GetConfigurationOptions());

            if (_redisConnection.IsConnected)
            {
                _logger.LogInformation("Redis connection established");
            }
            else
            {
                _logger.LogWarning("Redis multiplexer created but not yet connected; background reconnect is in progress");
            }

            try
            {
                oldConnection?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing previous Redis connection");
            }

            return _redisConnection;
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private ConfigurationOptions GetConfigurationOptions()
    {
        return _redisConfigurationOptions ??= BuildConfigurationOptions(_options.ConnectionString);
    }

    private static ConfigurationOptions BuildConfigurationOptions(string connectionString)
    {
        var configurationOptions = ConfigurationOptions.Parse(connectionString, true);
        configurationOptions.AbortOnConnectFail = false;
        return configurationOptions;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
