using System.Security.Cryptography;
using System.Text;

namespace IdempotencyGuard;

public class DefaultDownstreamKeyGenerator : IDownstreamKeyGenerator
{
    public string Generate(string originalKey, string provider, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var input = $"{originalKey}:{provider}:{operation}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
