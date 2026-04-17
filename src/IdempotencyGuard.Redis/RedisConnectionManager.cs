using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace IdempotencyGuard.Redis;

internal sealed class RedisConnectionManager : IDisposable, IAsyncDisposable
{
    private readonly object _reconnectLock = new();
    private readonly ILogger<RedisConnectionManager> _logger;
    private readonly RedisIdempotencyOptions _options;

    private IConnectionMultiplexer? _redisConnection;
    private ConfigurationOptions? _redisConfigurationOptions;
    private DateTimeOffset _lastReconnectTime = DateTimeOffset.MinValue;
    private bool _firstInitialization = true;
    private bool _disposed;

    public RedisConnectionManager(
        IOptions<RedisIdempotencyOptions> options,
        ILogger<RedisConnectionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IDatabase GetDatabase()
    {
        ThrowIfDisposed();
        return GetConnection().GetDatabase();
    }

    public Task<TimeSpan> PingAsync()
    {
        ThrowIfDisposed();
        return GetDatabase().PingAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var connection = Interlocked.Exchange(ref _redisConnection, null);
        _disposed = true;
        connection?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        var connection = Interlocked.Exchange(ref _redisConnection, null);
        _disposed = true;

        if (connection is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            return;
        }

        connection?.Dispose();
    }

    private IConnectionMultiplexer GetConnection()
    {
        var connection = Volatile.Read(ref _redisConnection);
        if (connection is { IsConnected: true })
        {
            return connection;
        }

        return ForceReconnect();
    }

    private IConnectionMultiplexer ForceReconnect()
    {
        lock (_reconnectLock)
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

            _redisConnection = ConnectionMultiplexer.Connect(GetConfigurationOptions());

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
