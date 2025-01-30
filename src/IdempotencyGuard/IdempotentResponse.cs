namespace IdempotencyGuard;

public class IdempotentResponse
{
    public required int StatusCode { get; init; }
    public required Dictionary<string, string[]> Headers { get; init; }
    public required byte[] Body { get; init; }
}
