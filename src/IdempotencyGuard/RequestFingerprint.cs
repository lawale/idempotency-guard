using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdempotencyGuard;

public static class RequestFingerprint
{
    public static string Compute(string httpMethod, string path, byte[]? body)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        sha256.AppendData(Encoding.UTF8.GetBytes(httpMethod.ToUpperInvariant()));
        sha256.AppendData(Encoding.UTF8.GetBytes(":"));
        sha256.AppendData(Encoding.UTF8.GetBytes(path));

        if (body is { Length: > 0 })
        {
            sha256.AppendData(Encoding.UTF8.GetBytes(":"));
            var normalised = NormaliseJsonBody(body);
            sha256.AppendData(normalised);
        }

        var hash = sha256.GetHashAndReset();
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] NormaliseJsonBody(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var sortedBytes = JsonSerializer.SerializeToUtf8Bytes(
                SortProperties(doc.RootElement));
            return sortedBytes;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static object? SortProperties(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element
                .EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToDictionary(p => p.Name, p => SortProperties(p.Value)),

            JsonValueKind.Array => element
                .EnumerateArray()
                .Select(SortProperties)
                .ToArray(),

            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }
}
