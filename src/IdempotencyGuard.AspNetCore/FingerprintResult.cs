namespace IdempotencyGuard.AspNetCore;

/// <summary>
/// The output of fingerprint computation: the hex hash string.
/// </summary>
public sealed record FingerprintResult(string Fingerprint);
