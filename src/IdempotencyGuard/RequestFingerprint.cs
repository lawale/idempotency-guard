using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdempotencyGuard;

public static class RequestFingerprint
{
    public static string Compute(string httpMethod, string path, byte[]? body, string? extraFingerprint = null)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendField(sha256, Encoding.UTF8.GetBytes(httpMethod.ToUpperInvariant()));
        AppendField(sha256, Encoding.UTF8.GetBytes(path));
        AppendField(sha256, body is { Length: > 0 } ? NormaliseJsonBody(body) : []);
        AppendField(sha256, Encoding.UTF8.GetBytes(extraFingerprint ?? string.Empty));

        var hash = sha256.GetHashAndReset();
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Length-prefixed so that a value can't move from one field into the next and hash the same.
    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    public static byte[]? ExtractProperties(byte[]? body, string[] propertyNames)
    {
        if (body is not { Length: > 0 } || propertyNames.Length == 0)
            return body;

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return body;

            var propertySet = new HashSet<string>(propertyNames, StringComparer.OrdinalIgnoreCase);

            var filtered = SortObject(doc.RootElement
                .EnumerateObject()
                .Where(p => propertySet.Contains(p.Name)));

            if (filtered.Count == 0)
                return body;

            return JsonSerializer.SerializeToUtf8Bytes(filtered);
        }
        catch (JsonException)
        {
            return body;
        }
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

    /// <summary>
    /// Sorts by name. A repeated name keeps its last value, matching what System.Text.Json
    /// and Json.NET hand to the application when they deserialize the same body.
    /// </summary>
    private static SortedDictionary<string, object?> SortObject(IEnumerable<JsonProperty> properties)
    {
        var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in properties)
        {
            sorted[property.Name] = SortProperties(property.Value);
        }

        return sorted;
    }

    private static object? SortProperties(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => SortObject(element.EnumerateObject()),

            JsonValueKind.Array => element
                .EnumerateArray()
                .Select(SortProperties)
                .ToArray(),

            JsonValueKind.String => element.GetString(),
            // Kept as a JSON number, written with its original text, so that 1 and "1" differ.
            JsonValueKind.Number => element,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }
}
