namespace IdempotencyGuard.Redis;

public class RedisIdempotencyOptions
{
    public string ConnectionString { get; set; } = "localhost:6379";
    public string KeyPrefix { get; set; } = "idempotency:";
    public TimeSpan MinimumReconnectInterval { get; set; } = TimeSpan.FromSeconds(60);
}
