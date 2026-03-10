using System.Text;
using FluentAssertions;

namespace IdempotencyGuard.Tests;

public class RequestFingerprintTests
{
    [Fact]
    public void Same_input_produces_same_hash()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100, "currency": "USD"}""");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", body);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Different_body_produces_different_hash()
    {
        var body1 = Encoding.UTF8.GetBytes("""{"amount": 100}""");
        var body2 = Encoding.UTF8.GetBytes("""{"amount": 200}""");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body1);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", body2);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Different_method_produces_different_hash()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body);
        var hash2 = RequestFingerprint.Compute("PUT", "/payments", body);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Different_path_produces_different_hash()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body);
        var hash2 = RequestFingerprint.Compute("POST", "/refunds", body);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Method_is_case_insensitive()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var hash1 = RequestFingerprint.Compute("post", "/payments", body);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", body);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Json_property_order_does_not_affect_hash()
    {
        var body1 = Encoding.UTF8.GetBytes("""{"amount": 100, "currency": "USD"}""");
        var body2 = Encoding.UTF8.GetBytes("""{"currency": "USD", "amount": 100}""");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body1);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", body2);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Empty_body_produces_consistent_hash()
    {
        var hash1 = RequestFingerprint.Compute("POST", "/payments", null);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", null);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Empty_body_differs_from_body_with_content()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var hashNoBody = RequestFingerprint.Compute("POST", "/payments", null);
        var hashWithBody = RequestFingerprint.Compute("POST", "/payments", body);

        hashNoBody.Should().NotBe(hashWithBody);
    }

    [Fact]
    public void Non_json_body_produces_consistent_hash()
    {
        var body = Encoding.UTF8.GetBytes("plain text body");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", body);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Hash_is_lowercase_hex_string()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var hash = RequestFingerprint.Compute("POST", "/payments", body);

        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Nested_json_property_order_does_not_affect_hash()
    {
        var body1 = Encoding.UTF8.GetBytes("""{"payment": {"amount": 100, "currency": "USD"}, "customer": "abc"}""");
        var body2 = Encoding.UTF8.GetBytes("""{"customer": "abc", "payment": {"currency": "USD", "amount": 100}}""");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body1);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", body2);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void ExtractProperties_filters_to_specified_properties()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"amount": 100, "currency": "USD", "description": "test"}""");

        var filtered = RequestFingerprint.ExtractProperties(body, ["amount", "currency"]);

        var json = Encoding.UTF8.GetString(filtered!);
        json.Should().Contain("amount");
        json.Should().Contain("currency");
        json.Should().NotContain("description");
    }

    [Fact]
    public void ExtractProperties_is_case_insensitive()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"amount": 100, "currency": "USD"}""");

        var filteredLower = RequestFingerprint.ExtractProperties(body, ["amount", "currency"]);
        var filteredUpper = RequestFingerprint.ExtractProperties(body, ["Amount", "Currency"]);

        filteredLower.Should().BeEquivalentTo(filteredUpper);
    }

    [Fact]
    public void ExtractProperties_with_missing_property_skips_it()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"amount": 100, "currency": "USD"}""");

        var filtered = RequestFingerprint.ExtractProperties(body, ["amount", "nonexistent"]);

        var json = Encoding.UTF8.GetString(filtered!);
        json.Should().Contain("amount");
        json.Should().NotContain("nonexistent");
    }

    [Fact]
    public void ExtractProperties_with_no_matching_properties_returns_original_body()
    {
        var body = Encoding.UTF8.GetBytes(
            """{"amount": 100, "currency": "USD"}""");

        var filtered = RequestFingerprint.ExtractProperties(body, ["nonexistent"]);

        filtered.Should().BeEquivalentTo(body);
    }

    [Fact]
    public void ExtractProperties_with_non_json_body_returns_original_body()
    {
        var body = Encoding.UTF8.GetBytes("plain text body");

        var filtered = RequestFingerprint.ExtractProperties(body, ["amount"]);

        filtered.Should().BeEquivalentTo(body);
    }

    [Fact]
    public void ExtractProperties_with_null_body_returns_null()
    {
        var filtered = RequestFingerprint.ExtractProperties(null, ["amount"]);

        filtered.Should().BeNull();
    }

    [Fact]
    public void ExtractProperties_with_empty_property_names_returns_original_body()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var filtered = RequestFingerprint.ExtractProperties(body, []);

        filtered.Should().BeEquivalentTo(body);
    }

    [Fact]
    public void ExtractProperties_with_json_array_body_returns_original_body()
    {
        var body = Encoding.UTF8.GetBytes("""[{"amount": 100}]""");

        var filtered = RequestFingerprint.ExtractProperties(body, ["amount"]);

        filtered.Should().BeEquivalentTo(body);
    }

    [Fact]
    public void ExtractProperties_produces_deterministic_output_regardless_of_property_order()
    {
        var body1 = Encoding.UTF8.GetBytes(
            """{"currency": "USD", "amount": 100, "description": "test"}""");
        var body2 = Encoding.UTF8.GetBytes(
            """{"amount": 100, "description": "test", "currency": "USD"}""");

        var filtered1 = RequestFingerprint.ExtractProperties(body1, ["amount", "currency"]);
        var filtered2 = RequestFingerprint.ExtractProperties(body2, ["amount", "currency"]);

        filtered1.Should().BeEquivalentTo(filtered2);
    }

    [Fact]
    public void Compute_with_extracted_properties_ignores_non_fingerprint_fields()
    {
        var body1 = Encoding.UTF8.GetBytes(
            """{"amount": 100, "currency": "USD", "timestamp": "2024-01-01"}""");
        var body2 = Encoding.UTF8.GetBytes(
            """{"amount": 100, "currency": "USD", "timestamp": "2024-06-15"}""");

        var props = new[] { "amount", "currency" };
        var filtered1 = RequestFingerprint.ExtractProperties(body1, props);
        var filtered2 = RequestFingerprint.ExtractProperties(body2, props);

        var hash1 = RequestFingerprint.Compute("POST", "/payments", filtered1);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", filtered2);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Compute_with_extracted_properties_differs_when_fingerprint_fields_differ()
    {
        var body1 = Encoding.UTF8.GetBytes(
            """{"amount": 100, "currency": "USD", "description": "same"}""");
        var body2 = Encoding.UTF8.GetBytes(
            """{"amount": 200, "currency": "USD", "description": "same"}""");

        var props = new[] { "amount", "currency" };
        var filtered1 = RequestFingerprint.ExtractProperties(body1, props);
        var filtered2 = RequestFingerprint.ExtractProperties(body2, props);

        var hash1 = RequestFingerprint.Compute("POST", "/payments", filtered1);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", filtered2);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Extra_fingerprint_changes_hash()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var hashWithout = RequestFingerprint.Compute("POST", "/payments", body);
        var hashWith = RequestFingerprint.Compute("POST", "/payments", body, "q:version=2");

        hashWithout.Should().NotBe(hashWith);
    }

    [Fact]
    public void Same_extra_fingerprint_produces_same_hash()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body, "r:merchantId=42");
        var hash2 = RequestFingerprint.Compute("POST", "/payments", body, "r:merchantId=42");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Different_extra_fingerprint_produces_different_hash()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body, "r:merchantId=42");
        var hash2 = RequestFingerprint.Compute("POST", "/payments", body, "r:merchantId=99");

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Null_extra_fingerprint_matches_no_extra()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", body, null);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Empty_extra_fingerprint_matches_no_extra()
    {
        var body = Encoding.UTF8.GetBytes("""{"amount": 100}""");

        var hash1 = RequestFingerprint.Compute("POST", "/payments", body);
        var hash2 = RequestFingerprint.Compute("POST", "/payments", body, "");

        hash1.Should().Be(hash2);
    }
}
