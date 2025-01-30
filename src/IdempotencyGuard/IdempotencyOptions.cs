namespace IdempotencyGuard;

public class IdempotencyOptions
{
    public string HeaderName { get; set; } = "Idempotency-Key";

    public TimeSpan ClaimTtl { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ResponseTtl { get; set; } = TimeSpan.FromHours(24);

    public ConcurrentRequestPolicy ConcurrentRequestPolicy { get; set; } = ConcurrentRequestPolicy.WaitThenReplay;

    public TimeSpan ConcurrentRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public MissingKeyPolicy MissingKeyPolicy { get; set; } = MissingKeyPolicy.Allow;

    public HashSet<string> EnforcedMethods { get; set; } = ["POST", "PUT", "PATCH"];

    public int MaxResponseBodySize { get; set; } = 1_048_576;

    public bool Enabled { get; set; } = true;

    public Func<HttpMethod, string, bool>? EndpointFilter { get; set; }
}

public enum ConcurrentRequestPolicy
{
    WaitThenReplay,
    ReturnConflict
}

public enum MissingKeyPolicy
{
    Allow,
    Reject
}
