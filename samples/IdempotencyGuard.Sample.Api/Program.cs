var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "IdempotencyGuard Sample API");

app.Run();
