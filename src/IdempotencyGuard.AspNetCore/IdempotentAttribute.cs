namespace IdempotencyGuard.AspNetCore;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class IdempotentAttribute : Attribute
{
    public bool Required { get; set; } = true;
    public int ClaimTtlSeconds { get; set; }
    public int ResponseTtlSeconds { get; set; }
}
