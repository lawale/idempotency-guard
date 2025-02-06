namespace IdempotencyGuard.PostgreSql;

public class PostgresIdempotencyOptions
{
    public string ConnectionString { get; set; } = "";
    public string TableName { get; set; } = "idempotency_entries";
    public string SchemaName { get; set; } = "public";
    public bool AutoCreateTable { get; set; } = true;
}
