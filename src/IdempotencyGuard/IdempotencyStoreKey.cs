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

    private const char ScopeSeparator = '\u001F';

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
    /// Isolates one caller from another. The hash is unkeyed, so anyone can compute a segment.
    /// What stops a caller on a route with no scope sending one as part of their key is the
    /// separator: control characters in client keys get a 400.
    /// <para>
    /// The identity is tagged and the policy name's length included, so a global scope can't
    /// imitate a policy's, and user "abc" and service "abc" stay apart under different policies.
    /// </para>
    /// </summary>
    internal static string ScopeSegment(string scope, string? policyName)
    {
        var identity = policyName is null
            ? $"g\0{scope}"
            : $"p\0{policyName.Length}:{policyName}\0{scope}";
        return $"{IdempotencyKeyHash.Compute(identity)}{ScopeSeparator}";
    }

    internal static string Digest(string? scopeSegment, string rawKey) =>
        IdempotencyKeyHash.Compute(string.Concat(scopeSegment, rawKey));
}
