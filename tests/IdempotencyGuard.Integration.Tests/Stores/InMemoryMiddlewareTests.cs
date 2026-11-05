using IdempotencyGuard.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace IdempotencyGuard.Integration.Tests.Stores;

public class InMemoryMiddlewareTests : MiddlewareStoreContractTests
{
    protected override void ConfigureStore(IServiceCollection services) =>
        services.AddIdempotencyGuardInMemoryStore();
}
