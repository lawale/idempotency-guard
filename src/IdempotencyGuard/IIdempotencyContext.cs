namespace IdempotencyGuard;

public interface IIdempotencyContext
{
    string Key { get; }

    bool IsReplay { get; }

    string DownstreamKey(string provider, string operation);

    string RequestFingerprint { get; }
}
