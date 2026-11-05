using FluentAssertions;

namespace IdempotencyGuard.Tests;

public class IdempotencyStoreKeyTests
{
    // Literal values, computed outside this code: changing them orphans every stored entry.
    [Fact]
    public void Unscoped_key_is_a_digest_of_the_client_key()
    {
        IdempotencyStoreKey.For("abc-123").Should().Be("5942d94f524882e0f29bf0a1e5a6dcc9");
    }

    [Fact]
    public void Prefix_stays_readable_in_front_of_the_digest()
    {
        IdempotencyStoreKey.For("abc-123", prefix: "prod:").Should().Be("prod:5942d94f524882e0f29bf0a1e5a6dcc9");
    }

    [Fact]
    public void Scoped_key_includes_the_scope_in_the_digest()
    {
        IdempotencyStoreKey.For("abc-123", scope: "alice").Should().Be("489b7fa0d5423f45f38231747e99182c");
    }

    [Fact]
    public void Policy_scoped_key_includes_the_policy_name_in_the_digest()
    {
        IdempotencyStoreKey.For("abc-123", scope: "alice", policyName: "user").Should().Be("a2f93b1b4d6ae97f8c2a910e10171f87");
    }

    [Theory]
    [InlineData("k")]
    [InlineData("a key with spaces and unicode é")]
    public void Digest_is_always_32_lowercase_hex_characters(string rawKey)
    {
        IdempotencyStoreKey.For(rawKey).Should().MatchRegex("^[0-9a-f]{32}$");
    }

    [Fact]
    public void Digest_length_does_not_depend_on_the_length_of_the_client_key()
    {
        IdempotencyStoreKey.For(new string('k', 100_000)).Should().HaveLength(32);
    }

    [Fact]
    public void Keys_differing_only_by_case_have_different_digests()
    {
        IdempotencyStoreKey.For("abc").Should().NotBe(IdempotencyStoreKey.For("ABC"));
    }

    [Fact]
    public void Longest_prefix_fits_the_sql_server_key_column()
    {
        var key = IdempotencyStoreKey.For("abc", prefix: new string('p', IdempotencyStoreKey.MaxPrefixLength));

        key.Length.Should().BeLessThanOrEqualTo(256);
    }
}
