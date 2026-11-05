using System.Security.Cryptography;
using System.Text;

namespace IdempotencyGuard.Diagnostics;

/// <summary>
/// One-way digest of a key. An idempotency key can replay a stored response, so the raw
/// value must not reach logs or traces, while the digest still correlates them.
/// </summary>
internal static class IdempotencyKeyHash
{
    private const int DigestBytes = 16;

    internal static string Compute(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, DigestBytes).ToLowerInvariant();
    }
}
