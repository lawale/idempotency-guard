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
}
