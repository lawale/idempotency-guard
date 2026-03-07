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

    // Cap request body bytes used for fingerprint hashing (default: 1 MB)
    options.MaxFingerprintBodySize = 1_048_576;

    // Namespace keys by environment to prevent collisions across deployments
    options.KeyPrefix = builder.Environment.IsProduction() ? "prod:" : "dev:";

    // Customise error responses to match RFC 7807 Problem Details
    options.ErrorResponseFactory = problem => new
    {
        type = $"https://docs.example.com/errors/idempotency/{problem.Kind}",
        title = problem.Kind switch
        {
            IdempotencyErrorKind.MissingKey => "Missing Idempotency Key",
            IdempotencyErrorKind.FingerprintMismatch => "Payload Mismatch",
            IdempotencyErrorKind.Conflict => "Request In Progress",
            IdempotencyErrorKind.Timeout => "Request Timed Out",
            _ => "Idempotency Error"
        },
        status = problem.StatusCode,
        detail = problem.Message,
        idempotencyKey = problem.IdempotencyKey
    };

    // Only replay content-type and location headers
    options.HeaderAllowList = ["Content-Type", "Location"];

    // Background cleanup of expired entries
    options.Cleanup.Enabled = true;
    options.Cleanup.Interval = TimeSpan.FromMinutes(5);
    options.Cleanup.BatchSize = 1_000;
});

builder.Services.AddIdempotencyGuardInMemoryStore();

var app = builder.Build();

app.UseIdempotencyGuard();

app.MapGet("/", () => "IdempotencyGuard Sample API — POST to /payments or /refunds with an Idempotency-Key header");

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
