namespace IdempotencyGuard.SqlServer;

public class SqlServerIdempotencyOptions
{
    public string ConnectionString { get; set; } = "";
    public string TableName { get; set; } = "IdempotencyEntries";
    public string SchemaName { get; set; } = "dbo";
    public bool AutoCreateTable { get; set; } = true;
}
