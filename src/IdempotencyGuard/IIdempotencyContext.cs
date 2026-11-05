namespace IdempotencyGuard;

public interface IIdempotencyContext
{
    /// <summary>
    /// The prefix, scope and client's key. On a scoped request it contains a control character,
    /// so it isn't valid in an HTTP header: use <see cref="DownstreamKey"/> to call another service.
    /// </summary>
    string Key { get; }

    bool IsReplay { get; }

    string DownstreamKey(string provider, string operation);

    string RequestFingerprint { get; }
}
