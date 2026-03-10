namespace IdempotencyGuard;

public class IdempotentResponse
{
    public required int StatusCode { get; init; }
    public required Dictionary<string, string[]> Headers { get; init; }
    public required ReadOnlyMemory<byte> Body { get; init; }
}
