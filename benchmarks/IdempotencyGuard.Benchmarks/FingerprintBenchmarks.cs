using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using IdempotencyGuard;

namespace IdempotencyGuard.Benchmarks;

/// <summary>
/// Cost of hashing a request into a fingerprint. This runs once per guarded
/// request and is the only CPU-bound work the middleware adds, so it sets the
/// floor on per-request overhead.
/// </summary>
[MemoryDiagnoser]
public class FingerprintBenchmarks
{
    private byte[] _body = [];

    [Params(256, 4_096, 65_536)]
    public int BodyBytes { get; set; }

    [GlobalSetup]
    public void Setup() => _body = BuildJsonBody(BodyBytes);

    [Benchmark]
    public string Compute() => RequestFingerprint.Compute("POST", "/payments", _body);

    [Benchmark]
    public byte[]? ExtractProperties() => RequestFingerprint.ExtractProperties(_body, ["amount", "currency"]);

    private static byte[] BuildJsonBody(int approximateBytes)
    {
        var payload = new Dictionary<string, object>
        {
            ["amount"] = 1250,
            ["currency"] = "USD",
            ["reference"] = "inv_00042"
        };

        // Pad with line items until the serialised body is roughly the target size.
        var items = new List<object>();
        while (JsonSerializer.SerializeToUtf8Bytes(payload).Length < approximateBytes)
        {
            items.Add(new { sku = $"sku-{items.Count:D5}", qty = 1, price = 999 });
            payload["items"] = items;
        }

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
    }
}
