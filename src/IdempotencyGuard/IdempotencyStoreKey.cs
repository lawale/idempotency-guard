using IdempotencyGuard.Diagnostics;

namespace IdempotencyGuard;

/// <summary>
/// Builds the key an entry is stored under: the prefix, followed by a fixed-length digest of
/// the scope and the client's key. Stores therefore never see a key of unbounded length or
/// one that their collation could treat as equal to a different key.
/// </summary>
public static class IdempotencyStoreKey
{
    /// <summary>
    /// Longest allowed key prefix. With the 32-character digest this keeps every store key
    /// within the SQL Server store's 256-character column.
    /// </summary>
    public const int MaxPrefixLength = 128;

    /// <summary>
    /// Computes the store key for a client's key, for finding its entry when debugging.
    /// </summary>
    /// <param name="rawKey">The key the client sent.</param>
    /// <param name="scope">The value returned by the <c>KeyScope</c> resolver, if one applies.</param>
    /// <param name="policyName">The policy name, only if that policy defines its own <c>KeyScope</c>.</param>
    /// <param name="prefix">The key prefix, if one is configured.</param>
    public static string For(string rawKey, string? scope = null, string? policyName = null, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(rawKey);

        var segment = scope is null ? null : ScopeSegment(scope, policyName);
        return string.Concat(prefix, Digest(segment, rawKey));
    }

    /// <summary>
    /// Isolates one caller from another. A policy with its own resolver also hashes its name,
    /// so user "abc" and service "abc" stay apart under different policies. The fixed length
    /// means a scope can't run into the client's key and collide.
    /// </summary>
    internal static string ScopeSegment(string scope, string? policyName)
    {
        var identity = policyName is null ? scope : $"{policyName}\0{scope}";
        return $"{IdempotencyKeyHash.Compute(identity)}:";
    }

    internal static string Digest(string? scopeSegment, string rawKey) =>
        IdempotencyKeyHash.Compute(string.Concat(scopeSegment, rawKey));
}
