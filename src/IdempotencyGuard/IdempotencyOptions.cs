namespace IdempotencyGuard;

public class IdempotencyOptions
{
    public string HeaderName { get; set; } = "Idempotency-Key";

    public TimeSpan ClaimTtl { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ResponseTtl { get; set; } = TimeSpan.FromHours(24);

    public ConcurrentRequestPolicy ConcurrentRequestPolicy { get; set; } = ConcurrentRequestPolicy.WaitThenReplay;

    public TimeSpan ConcurrentRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public MissingKeyPolicy MissingKeyPolicy { get; set; } = MissingKeyPolicy.Allow;

    public HashSet<string> EnforcedMethods { get; set; } = ["POST", "PUT", "PATCH"];

    public int MaxResponseBodySize { get; set; } = 1_048_576;

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
