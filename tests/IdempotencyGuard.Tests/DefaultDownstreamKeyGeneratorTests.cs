using FluentAssertions;

namespace IdempotencyGuard.Tests;

public class DefaultDownstreamKeyGeneratorTests
{
    private readonly DefaultDownstreamKeyGenerator _generator = new();

    [Fact]
    public void Same_inputs_produce_same_key()
    {
        var key1 = _generator.Generate("original-key", "stripe", "charge");
        var key2 = _generator.Generate("original-key", "stripe", "charge");

        key1.Should().Be(key2);
    }

    [Fact]
    public void Different_provider_produces_different_key()
    {
        var key1 = _generator.Generate("original-key", "stripe", "charge");
        var key2 = _generator.Generate("original-key", "paystack", "charge");

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Different_operation_produces_different_key()
    {
        var key1 = _generator.Generate("original-key", "stripe", "charge");
        var key2 = _generator.Generate("original-key", "stripe", "refund");

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Different_original_key_produces_different_key()
    {
        var key1 = _generator.Generate("key-1", "stripe", "charge");
        var key2 = _generator.Generate("key-2", "stripe", "charge");

        key1.Should().NotBe(key2);
    }

    [Fact]
    public void Output_is_lowercase_hex()
    {
        var key = _generator.Generate("original-key", "stripe", "charge");

        key.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Throws_on_null_or_empty_inputs()
    {
        var act1 = () => _generator.Generate("", "stripe", "charge");
        var act2 = () => _generator.Generate("key", "", "charge");
        var act3 = () => _generator.Generate("key", "stripe", "");

        act1.Should().Throw<ArgumentException>();
        act2.Should().Throw<ArgumentException>();
        act3.Should().Throw<ArgumentException>();
    }
}
