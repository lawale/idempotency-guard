using FluentAssertions;
using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.PostgreSql;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class PostgresStoreTests : IdempotencyStoreContractTests, IClassFixture<PostgresContainerFixture>, IAsyncLifetime
{
    private readonly PostgresIdempotencyStore _store;
    private readonly PostgresContainerFixture _fixture;

    public PostgresStoreTests(PostgresContainerFixture fixture)
    {
        _fixture = fixture;
        var options = Options.Create(new PostgresIdempotencyOptions
        {
            ConnectionString = fixture.ConnectionString,
            AutoCreateTable = true
        });
        _store = new PostgresIdempotencyStore(options);
    }

    protected override IIdempotencyStore Store => _store;

    public async Task InitializeAsync()
    {
        // Warm up: ensure the table is created before any test runs.
        // Prevents concurrent CREATE TABLE race conditions in the Concurrent_claims test.
        var claimToken = ((ClaimResult.Claimed)await Store.TryClaimAsync("warmup", "warmup", TimeSpan.FromSeconds(1))).ClaimToken;
        await Store.ReleaseClaimAsync("warmup", claimToken);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public Task Instances_starting_together_create_the_table() =>
        InstancesStartingTogetherCanClaimAsync(_ => Task.CompletedTask, NewStore);

    [Fact]
    public Task Instances_starting_together_upgrade_a_1x_table() =>
        InstancesStartingTogetherCanClaimAsync(async table =>
        {
            await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE "public"."{table}" (
                    key TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'claimed',
                    claimed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), completed_at TIMESTAMPTZ,
                    expires_at TIMESTAMPTZ NOT NULL, status_code INT, headers_json TEXT, response_body BYTEA);
                CREATE INDEX "idx_{table}_expires" ON "public"."{table}" (expires_at);
                """;
            await cmd.ExecuteNonQueryAsync();
        }, NewStore);

    private PostgresIdempotencyStore NewStore(string table) => new(Options.Create(new PostgresIdempotencyOptions
    {
        ConnectionString = _fixture.ConnectionString,
        TableName = table,
        AutoCreateTable = true
    }));

    [Fact]
    public async Task Table_created_by_1x_is_upgraded_and_its_completed_entries_still_replay()
    {
        var table = $"legacy_{Guid.NewGuid():N}";
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE "public"."{table}" (
                    key TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'claimed',
                    claimed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), completed_at TIMESTAMPTZ,
                    expires_at TIMESTAMPTZ NOT NULL, status_code INT, headers_json TEXT, response_body BYTEA);
                INSERT INTO "public"."{table}" (key, fingerprint, state, completed_at, expires_at, status_code, headers_json, response_body)
                VALUES ('old-completed', 'fp', 'completed', NOW(), NOW() + INTERVAL '1 hour', 201, '{"{}"}', '\x6f6b');
                INSERT INTO "public"."{table}" (key, fingerprint, state, expires_at)
                VALUES ('old-claim', 'fp', 'claimed', NOW() - INTERVAL '1 hour');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var store = new PostgresIdempotencyStore(Options.Create(new PostgresIdempotencyOptions
        {
            ConnectionString = _fixture.ConnectionString,
            TableName = table,
            AutoCreateTable = true
        }));

        // A response stored by 1.x is still replayed.
        (await store.TryClaimAsync("old-completed", "fp", TimeSpan.FromMinutes(1))).Should().BeOfType<ClaimResult.Completed>();
        var replay = await store.GetResponseAsync("old-completed");
        replay!.StatusCode.Should().Be(201);

        // An abandoned 1.x claim has no token, and can still be taken over and completed.
        var takeover = await store.TryClaimAsync("old-claim", "fp", TimeSpan.FromMinutes(1));
        var token = takeover.Should().BeOfType<ClaimResult.Claimed>().Subject.ClaimToken;
        var response = new IdempotentResponse { StatusCode = 200, Headers = [], Body = "ok"u8.ToArray() };
        (await store.SetResponseAsync("old-claim", token, response, TimeSpan.FromMinutes(1))).Should().BeTrue();
    }
}
