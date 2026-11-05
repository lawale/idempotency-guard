using Xunit.Abstractions;

namespace IdempotencyGuard.Stress.Tests.Stores;

public class InMemoryStoreStressTests : IdempotencyStoreStressTests, IDisposable
{
    private readonly InMemoryIdempotencyStore _store = new();

    public InMemoryStoreStressTests(ITestOutputHelper output) : base(output) { }

    protected override IIdempotencyStore Store => _store;

    public void Dispose()
    {
        _store.Dispose();
        GC.SuppressFinalize(this);
    }
}
