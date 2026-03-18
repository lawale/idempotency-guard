namespace IdempotencyGuard.Integration.Tests.Stores;

public class InMemoryStoreTests : IdempotencyStoreContractTests, IDisposable
{
    private readonly InMemoryIdempotencyStore _store = new();

    protected override IIdempotencyStore Store => _store;

    public void Dispose()
    {
        _store.Dispose();
        GC.SuppressFinalize(this);
    }
}
