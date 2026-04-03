namespace IdempotencyGuard;

public class IdempotencyOptions
{
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>
    /// Name of the response header added to replayed (cached) responses to indicate
    /// the response was served from the idempotency store rather than re-executing the handler.
    /// Default: <c>"X-Idempotent-Replayed"</c>.
    /// </summary>
    public string ReplayedHeaderName { get; set; } = "X-Idempotent-Replayed";

    public TimeSpan ClaimTtl { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ResponseTtl { get; set; } = TimeSpan.FromHours(24);

    public ConcurrentRequestPolicy ConcurrentRequestPolicy { get; set; } = ConcurrentRequestPolicy.WaitThenReplay;

    public TimeSpan ConcurrentRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public MissingKeyPolicy MissingKeyPolicy { get; set; } = MissingKeyPolicy.Allow;

    public HashSet<string> EnforcedMethods { get; set; } = ["POST", "PUT", "PATCH"];

    public int MaxResponseBodySize { get; set; } = 1_048_576;

    /// <summary>
    /// Maximum number of request body bytes used for fingerprint hashing.
    /// Bodies larger than this are fingerprinted using only the first N bytes.
    /// Default: 1 MB. Set to 0 to skip body hashing entirely.
    /// </summary>
    public int MaxFingerprintBodySize { get; set; } = 1_048_576;

    /// <summary>
    /// Prefix prepended to every idempotency key before it is passed to the store.
    /// Use this to namespace keys by environment or tenant (e.g. <c>"staging:"</c>, <c>"tenant-42:"</c>).
    /// Default: <c>null</c> (no prefix). Applied across all store implementations.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// The <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> key used to read
    /// a per-request key prefix override. When the item is present and is a non-null
    /// <see cref="string"/>, it takes precedence over <see cref="KeyPrefix"/>.
    /// Default: <c>"IdempotencyKeyPrefix"</c>.
    /// </summary>
    public string KeyPrefixItemKey { get; set; } = DefaultKeyPrefixItemKey;

    /// <summary>
    /// Default value for <see cref="KeyPrefixItemKey"/>.
    /// </summary>
    public const string DefaultKeyPrefixItemKey = "IdempotencyKeyPrefix";

    public bool Enabled { get; set; } = true;

    public Func<HttpMethod, string, bool>? EndpointFilter { get; set; }

    /// <summary>
    /// Custom error response factory. When set, the middleware calls this to produce the
    /// JSON body for all error responses (400, 409, 422) instead of the default
    /// <c>{"error":"..."}</c> format. The returned object is serialized with System.Text.Json.
    /// </summary>
    public Func<IdempotencyProblem, object>? ErrorResponseFactory { get; set; }

    /// <summary>
    /// Additional response headers to exclude from storage beyond the built-in defaults
    /// (hop-by-hop + transient headers). Ignored if <see cref="HeaderAllowList"/> is set.
    /// </summary>
    public HashSet<string>? HeaderDenyList { get; set; }

    /// <summary>
    /// If set, ONLY these response headers will be stored and replayed.
    /// Overrides <see cref="HeaderDenyList"/> and the built-in defaults entirely.
    /// </summary>
    public HashSet<string>? HeaderAllowList { get; set; }

    /// <summary>
    /// Configuration for the background expired-entry cleanup service.
    /// </summary>
    public CleanupOptions Cleanup { get; set; } = new();
}

public static class HeaderFilter
{
    /// <summary>
    /// Hop-by-hop headers (RFC 9110 §7.6.1) plus common transient headers
    /// that must not be stored or replayed.
    /// </summary>
    public static readonly HashSet<string> DefaultDenyList = new(StringComparer.OrdinalIgnoreCase)
    {
        // Hop-by-hop (RFC 9110)
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",

        // Transient / session-specific
        "Set-Cookie",
        "WWW-Authenticate",
        "Proxy-Connection",
        "Alt-Svc",

        // Server-generated per-response
        "Server",
        "Date",
    };

    public static bool ShouldStoreHeader(string headerName, IdempotencyOptions options)
    {
        if (options.HeaderAllowList is { Count: > 0 })
        {
            return options.HeaderAllowList.Any(
                h => string.Equals(h, headerName, StringComparison.OrdinalIgnoreCase));
        }

        if (DefaultDenyList.Contains(headerName))
            return false;

        if (options.HeaderDenyList is not null
            && options.HeaderDenyList.Any(
                h => string.Equals(h, headerName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }
}

public enum ConcurrentRequestPolicy
{
    WaitThenReplay,
    ReturnConflict
}

public enum MissingKeyPolicy
{
    Allow,
    Reject
}

public enum IdempotencyErrorKind
{
    MissingKey,
    FingerprintMismatch,
    Conflict,
    Timeout
}

public class IdempotencyProblem
{
    public required int StatusCode { get; init; }
    public required IdempotencyErrorKind Kind { get; init; }
    public required string Message { get; init; }
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// Options for the background expired-entry cleanup service.
/// </summary>
public class CleanupOptions
{
    /// <summary>
    /// Whether the background cleanup hosted service is enabled. Default: <c>true</c>.
    /// Set to <c>false</c> to disable automatic cleanup (e.g. if using an external scheduler
    /// or calling <see cref="IPurgableIdempotencyStore.PurgeExpiredAsync"/> directly).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Time between cleanup sweeps. Default: 5 minutes.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum entries to delete per database round-trip. Default: 1000.
    /// </summary>
    public int BatchSize { get; set; } = 1_000;

    /// <summary>
    /// Maximum batch iterations per sweep to prevent runaway loops. Default: 100.
    /// After this many iterations the sweep yields until the next interval.
    /// </summary>
    public int MaxIterationsPerSweep { get; set; } = 100;
}
