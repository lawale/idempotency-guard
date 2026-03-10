namespace IdempotencyGuard.AspNetCore;

/// <summary>
/// The output of fingerprint computation: the hex hash string and the
/// raw request body bytes (needed later for response storage without
/// re-reading the stream).
/// </summary>
public sealed record FingerprintResult(string Fingerprint, byte[]? RequestBody);
