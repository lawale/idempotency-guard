namespace IdempotencyGuard.AspNetCore;

internal class IdempotencyContext : IIdempotencyContext
{
    private readonly IDownstreamKeyGenerator _keyGenerator;

    public IdempotencyContext(string key, string requestFingerprint, bool isReplay, IDownstreamKeyGenerator keyGenerator)
    {
        Key = key;
        RequestFingerprint = requestFingerprint;
        IsReplay = isReplay;
        _keyGenerator = keyGenerator;
    }

    public string Key { get; }

    public bool IsReplay { get; }

    public string DownstreamKey(string provider, string operation) =>
        _keyGenerator.Generate(Key, provider, operation);

    public string RequestFingerprint { get; }
}
