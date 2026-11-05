using IdempotencyGuard.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace IdempotencyGuard.Stress.Tests.Stores;

public class InMemoryMiddlewareStressTests : MiddlewareStressTests
{
    public InMemoryMiddlewareStressTests(ITestOutputHelper output) : base(output) { }

    protected override string StoreName => "in-memory";

    protected override void ConfigureStore(IServiceCollection services) =>
        services.AddIdempotencyGuardInMemoryStore();
}
