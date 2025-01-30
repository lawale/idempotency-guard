namespace IdempotencyGuard;

public class IdempotencyEntry
{
    public required string Key { get; init; }
    public required string RequestFingerprint { get; init; }
    public IdempotencyState State { get; set; }
    public DateTime ClaimedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    public int? StatusCode { get; set; }
    public string? ResponseHeaders { get; set; }
    public byte[]? ResponseBody { get; set; }
}
