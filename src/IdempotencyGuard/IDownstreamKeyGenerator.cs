namespace IdempotencyGuard;

public interface IDownstreamKeyGenerator
{
    string Generate(string originalKey, string provider, string operation);
}
