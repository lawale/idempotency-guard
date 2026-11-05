using FluentAssertions;
using IdempotencyGuard.Integration.Tests.Fixtures;
using IdempotencyGuard.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class SqlServerStoreTests : IdempotencyStoreContractTests, IClassFixture<SqlServerContainerFixture>, IAsyncLifetime
{
    private readonly SqlServerIdempotencyStore _store;
    private readonly SqlServerContainerFixture _fixture;

    public SqlServerStoreTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
        var options = Options.Create(new SqlServerIdempotencyOptions
        {
            ConnectionString = fixture.ConnectionString,
            AutoCreateTable = true
        });
        _store = new SqlServerIdempotencyStore(options);
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
    public async Task Table_created_by_1x_is_upgraded_and_its_completed_entries_still_replay()
    {
        var table = $"legacy_{Guid.NewGuid():N}";
        await using (var conn = new SqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE [dbo].[{table}] (
                    [Key] NVARCHAR(256) NOT NULL PRIMARY KEY, Fingerprint NVARCHAR(128) NOT NULL,
                    State NVARCHAR(20) NOT NULL DEFAULT 'claimed', ClaimedAtUtc DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    CompletedAtUtc DATETIME2 NULL, ExpiresAtUtc DATETIME2 NOT NULL, StatusCode INT NULL,
                    HeadersJson NVARCHAR(MAX) NULL, ResponseBody VARBINARY(MAX) NULL);
                INSERT INTO [dbo].[{table}] ([Key], Fingerprint, State, CompletedAtUtc, ExpiresAtUtc, StatusCode, HeadersJson, ResponseBody)
                VALUES ('old-completed', 'fp', 'completed', SYSUTCDATETIME(), DATEADD(HOUR, 1, SYSUTCDATETIME()), 201, '{"{}"}', 0x6f6b);
                INSERT INTO [dbo].[{table}] ([Key], Fingerprint, State, ExpiresAtUtc)
                VALUES ('old-claim', 'fp', 'claimed', DATEADD(HOUR, -1, SYSUTCDATETIME()));
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var store = new SqlServerIdempotencyStore(Options.Create(new SqlServerIdempotencyOptions
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
