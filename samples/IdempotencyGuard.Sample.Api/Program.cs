using IdempotencyGuard;
using IdempotencyGuard.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddIdempotencyGuard(options =>
{
    options.HeaderName = "Idempotency-Key";
    options.ClaimTtl = TimeSpan.FromMinutes(5);
    options.ResponseTtl = TimeSpan.FromHours(24);
    options.ConcurrentRequestPolicy = ConcurrentRequestPolicy.WaitThenReplay;
    options.ConcurrentRequestTimeout = TimeSpan.FromSeconds(30);
    options.MissingKeyPolicy = MissingKeyPolicy.Reject;
    options.EnforcedMethods = ["POST", "PUT", "PATCH"];
});

builder.Services.AddIdempotencyGuardInMemoryStore();

var app = builder.Build();

app.UseIdempotencyGuard();

app.MapGet("/", () => "IdempotencyGuard Sample API - POST to /payments with an Idempotency-Key header");

app.MapPost("/payments", (PaymentRequest request) =>
{
    var payment = new PaymentResponse(
        Id: Guid.NewGuid().ToString("N")[..12],
        Amount: request.Amount,
        Currency: request.Currency,
        Status: "created",
        CreatedAt: DateTime.UtcNow);

    return Results.Created($"/payments/{payment.Id}", payment);
});

app.MapPost("/refunds", (RefundRequest request) =>
{
    var refund = new RefundResponse(
        Id: Guid.NewGuid().ToString("N")[..12],
        PaymentId: request.PaymentId,
        Amount: request.Amount,
        Status: "pending",
        CreatedAt: DateTime.UtcNow);

    return Results.Created($"/refunds/{refund.Id}", refund);
});

app.Run();

record PaymentRequest(decimal Amount, string Currency, string? Description = null);
record PaymentResponse(string Id, decimal Amount, string Currency, string Status, DateTime CreatedAt);
record RefundRequest(string PaymentId, decimal Amount, string? Reason = null);
record RefundResponse(string Id, string PaymentId, decimal Amount, string Status, DateTime CreatedAt);
